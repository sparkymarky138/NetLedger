using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace NetLedger.Core;

public sealed class AppCounter
{
    public AppCounter(AppInfo info) { Info = info; }
    public AppInfo Info { get; }
    public long Sent, Recv;                 // bytes since NetLedger started
    internal long LastSent, LastRecv;       // for per-second rates
    internal long FlushedSent, FlushedRecv; // already written to the database
}

/// <summary>Accumulates ETW traffic events into per-app counters.</summary>
public sealed class TrafficAggregator
{
    readonly ConcurrentDictionary<string, AppCounter> _counters = new();
    readonly ConcurrentQueue<AppCounter> _new = new();
    readonly object _createLock = new();

    public void Add(int pid, int bytes, bool sent)
    {
        var c = GetOrCreate(ProcessResolver.Resolve(pid), announce: true);
        if (sent) Interlocked.Add(ref c.Sent, bytes);
        else Interlocked.Add(ref c.Recv, bytes);
    }

    public AppCounter GetOrCreate(AppInfo info, bool announce)
    {
        if (_counters.TryGetValue(info.Key, out var c)) return c;
        lock (_createLock)
        {
            if (_counters.TryGetValue(info.Key, out c)) return c;
            c = new AppCounter(info);
            _counters[info.Key] = c;
            if (announce) _new.Enqueue(c);
            return c;
        }
    }

    public ICollection<AppCounter> Counters => _counters.Values;
    public bool TryDequeueNew(out AppCounter c) => _new.TryDequeue(out c!);
}

public sealed record AppStat(AppInfo Info, long SessionSent, long SessionRecv, double RateSent, double RateRecv,
    long TodaySent, long TodayRecv, bool Blocked);

public sealed record EngineSnapshot(DateTime Time, IReadOnlyList<AppStat> Apps, double RateSent, double RateRecv,
    long SessionSent, long SessionRecv, long TodaySent, long TodayRecv);

/// <summary>
/// Owns the ETW consumer, the counters, the history database and the firewall state.
/// Ticks once a second on a background timer and publishes an <see cref="EngineSnapshot"/>.
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    public UsageStore Store { get; }
    public TrafficAggregator Aggregator { get; } = new();
    public string? EtwError { get; private set; }
    public long EventsSeen => _etw?.EventsSeen ?? 0;

    public event Action<EngineSnapshot>? Snapshot;
    public event Action<AppInfo>? NewApp;

    EtwNetworkMonitor? _etw;
    Timer? _timer;
    readonly object _tickLock = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTickSeconds;
    long _lastMinute = UsageStore.NowMinute();
    DateTime _todayDate = DateTime.Today;
    Dictionary<string, (long Sent, long Recv)> _todayBase = new();
    volatile HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    readonly DateTime _learningUntil;
    bool _disposed;

    // ---- snooze: alerts muted and NetLedger's block rules switched off until this time ----
    readonly object _snoozeLock = new();
    DateTime? _snoozeUntil;
    public DateTime? SnoozeUntil => _snoozeUntil;
    public bool IsSnoozed => _snoozeUntil != null;
    public event Action? SnoozeChanged;

    public MonitorEngine(string dbPath)
    {
        Store = new UsageStore(dbPath);
        // On the very first run every app is "new"; stay quiet for two minutes while we learn the baseline.
        _learningUntil = Store.InitialAppCount == 0 ? DateTime.Now.AddMinutes(2) : DateTime.MinValue;
    }

    public bool AlertsEnabled
    {
        get => Store.GetSetting("alerts", "1") == "1";
        set => Store.SetSetting("alerts", value ? "1" : "0");
    }

    public void Start()
    {
        RestoreSnoozeState();
        try { RefreshBlocked(); } catch (Exception ex) { AppPaths.LogError("Reading firewall rules", ex); }
        ReloadTodayBase();

        // Show today's apps and blocked apps even before they send anything this session.
        foreach (var t in Store.AppTotals(UsageStore.TodayStartMinute()))
            Aggregator.GetOrCreate(t.Info, announce: false);
        foreach (var path in _blocked)
        {
            var info = ProcessResolver.InfoForPath(path);
            Store.EnsureApp(info);
            Aggregator.GetOrCreate(info, announce: false);
        }

        try
        {
            _etw = new EtwNetworkMonitor();
            _etw.ProcessStarted += ProcessResolver.NoteProcessStart;
            _etw.Traffic += Aggregator.Add;
            _etw.Start();
        }
        catch (Exception ex)
        {
            EtwError = ex.Message;
            AppPaths.LogError("Starting ETW", ex);
            _etw?.Dispose();
            _etw = null;
        }

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    void Tick()
    {
        if (!Monitor.TryEnter(_tickLock)) return;
        try
        {
            if (_disposed) return;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Max(0.2, now - _lastTickSeconds);
            _lastTickSeconds = now;

            long minute = UsageStore.NowMinute();
            if (minute != _lastMinute)
            {
                FlushLocked(_lastMinute);
                _lastMinute = minute;
                if (DateTime.Today != _todayDate) _todayDate = DateTime.Today;
                ReloadTodayBase();
            }

            if (_snoozeUntil is { } until && DateTime.Now >= until)
            {
                try { Resume(); } catch (Exception ex) { AppPaths.LogError("Ending snooze", ex); }
            }

            HandleNewApps();

            var stats = new List<AppStat>();
            double rs = 0, rr = 0;
            long ss = 0, sr = 0, ts = 0, tr = 0;
            var blocked = _blocked;
            foreach (var c in Aggregator.Counters)
            {
                long s = Interlocked.Read(ref c.Sent), r = Interlocked.Read(ref c.Recv);
                double rateS = (s - c.LastSent) / dt, rateR = (r - c.LastRecv) / dt;
                c.LastSent = s; c.LastRecv = r;
                _todayBase.TryGetValue(c.Info.Key, out var tb);
                long todayS = tb.Sent + (s - c.FlushedSent), todayR = tb.Recv + (r - c.FlushedRecv);
                stats.Add(new AppStat(c.Info, s, r, rateS, rateR, todayS, todayR,
                    !c.Info.IsPseudo && blocked.Contains(c.Info.Path)));
                rs += rateS; rr += rateR; ss += s; sr += r; ts += todayS; tr += todayR;
            }

            Snapshot?.Invoke(new EngineSnapshot(DateTime.Now, stats, rs, rr, ss, sr, ts, tr));
        }
        catch (Exception ex) { AppPaths.LogError("Tick", ex); }
        finally { Monitor.Exit(_tickLock); }
    }

    void HandleNewApps()
    {
        while (Aggregator.TryDequeueNew(out var c))
        {
            bool known = Store.IsKnown(c.Info.Key);
            Store.EnsureApp(c.Info);
            if (!known && !c.Info.IsPseudo && !IsSnoozed && DateTime.Now >= _learningUntil && AlertsEnabled)
                NewApp?.Invoke(c.Info);
        }
    }

    void FlushLocked(long minute)
    {
        var rows = new List<(AppInfo, long, long)>();
        foreach (var c in Aggregator.Counters)
        {
            long s = Interlocked.Read(ref c.Sent), r = Interlocked.Read(ref c.Recv);
            long ds = s - c.FlushedSent, dr = r - c.FlushedRecv;
            if (ds == 0 && dr == 0) continue;
            rows.Add((c.Info, ds, dr));
            c.FlushedSent = s; c.FlushedRecv = r;
        }
        if (rows.Count > 0) Store.AddUsage(minute, rows);
    }

    /// <summary>Writes pending counts now (before history queries and on exit).</summary>
    public void FlushNow()
    {
        lock (_tickLock)
        {
            FlushLocked(UsageStore.NowMinute());
            ReloadTodayBase();
        }
    }

    void ReloadTodayBase()
    {
        var d = new Dictionary<string, (long, long)>();
        foreach (var t in Store.AppTotals(UsageStore.TodayStartMinute())) d[t.Info.Key] = (t.Sent, t.Recv);
        _todayBase = d;
    }

    // ---- firewall ----

    public bool IsBlocked(AppInfo info) => !info.IsPseudo && _blocked.Contains(info.Path);
    public int BlockedCount => _blocked.Count;

    public void RefreshBlocked() => _blocked = FirewallManager.GetBlockedPaths();

    public void SetBlocked(AppInfo info, bool block)
    {
        if (info.IsPseudo) throw new InvalidOperationException("This entry has no program file, so it cannot be blocked.");
        if (block)
        {
            bool snoozed = IsSnoozed;
            // While snoozed the rule is created switched off; Resume() switches it on with the others.
            FirewallManager.Block(info.Path, info.Name, enabled: !snoozed);
            // Drop live connections so the block applies at once, not just to new connections.
            if (!snoozed) ConnectionTable.CloseTcpConnections(pid => ProcessResolver.Resolve(pid).Key == info.Key);
        }
        else
        {
            FirewallManager.Allow(info.Path);
        }
        RefreshBlocked();
    }

    public int RemoveAllRules()
    {
        int n = FirewallManager.RemoveAll();
        RefreshBlocked();
        return n;
    }

    // ---- snooze ----

    const string SnoozeKey = "snooze_until";

    /// <summary>Mutes alerts and switches off NetLedger's block rules. null duration = until Resume().</summary>
    public void Snooze(TimeSpan? duration)
    {
        lock (_snoozeLock)
        {
            var until = duration == null ? DateTime.MaxValue : DateTime.Now + duration.Value;
            FirewallManager.SetAllEnabled(false);
            _snoozeUntil = until;
            Store.SetSetting(SnoozeKey, (until == DateTime.MaxValue ? long.MaxValue : until.Ticks).ToString());
        }
        SnoozeChanged?.Invoke();
    }

    /// <summary>Ends a snooze: block rules back on (and blocked apps' live connections reset), alerts back on.</summary>
    public void Resume()
    {
        lock (_snoozeLock)
        {
            if (_snoozeUntil == null) return;
            FirewallManager.SetAllEnabled(true);
            var blocked = _blocked;
            if (blocked.Count > 0)
                ConnectionTable.CloseTcpConnections(pid =>
                {
                    var i = ProcessResolver.Resolve(pid);
                    return !i.IsPseudo && blocked.Contains(i.Path);
                });
            _snoozeUntil = null;
            Store.SetSetting(SnoozeKey, "0");
        }
        SnoozeChanged?.Invoke();
    }

    /// <summary>
    /// On start-up: continue a snooze that has not expired yet; otherwise make sure no rule was
    /// left switched off (for example after a crash or power cut during a snooze).
    /// </summary>
    void RestoreSnoozeState()
    {
        try
        {
            long.TryParse(Store.GetSetting(SnoozeKey, "0"), out long t);
            DateTime? until = t == 0 ? null : t == long.MaxValue ? DateTime.MaxValue : new DateTime(t);
            if (until != null && until > DateTime.Now)
            {
                _snoozeUntil = until;
                FirewallManager.SetAllEnabled(false);
            }
            else
            {
                Store.SetSetting(SnoozeKey, "0");
                FirewallManager.SetAllEnabled(true);
            }
        }
        catch (Exception ex) { AppPaths.LogError("Restoring snooze state", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer?.Dispose();
        _etw?.Dispose();
        // Never leave blocking switched off once NetLedger is no longer running to switch it back on.
        if (IsSnoozed)
        {
            try { Resume(); } catch (Exception ex) { AppPaths.LogError("Ending snooze on exit", ex); }
        }
        lock (_tickLock)
        {
            try { FlushLocked(UsageStore.NowMinute()); } catch (Exception ex) { AppPaths.LogError("Final flush", ex); }
            _disposed = true;
        }
        Store.Dispose();
    }
}
