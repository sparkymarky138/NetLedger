using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetLedger.Core;

namespace NetLedger.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class IconCache
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(AppInfo info)
    {
        if (info.IsPseudo) return null;
        return Cache.GetOrAdd(info.Path, p =>
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                if (icon == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        });
    }
}

public sealed class AppRow : Observable
{
    public AppRow(AppInfo info)
    {
        Info = info;
        Icon = IconCache.Get(info);
    }

    public AppInfo Info { get; }
    public string Key => Info.Key;
    public string Name => Info.Name;
    public string Detail => Info.IsPseudo ? "Traffic the kernel handles itself, or from processes that already exited" : Info.Path;
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public bool CanBlock => !Info.IsPseudo;

    double _rateDown, _rateUp;
    long _sessionDown, _sessionUp, _todayDown, _todayUp;
    bool _blocked, _busy;

    public void Update(AppStat s)
    {
        if (Set(ref _rateDown, s.RateRecv, nameof(RateDown)) | Set(ref _rateUp, s.RateSent, nameof(RateUp)))
        {
            Raise(nameof(RateDownText)); Raise(nameof(RateUpText)); Raise(nameof(Activity));
        }
        if (Set(ref _sessionDown, s.SessionRecv, nameof(SessionDown)) | Set(ref _sessionUp, s.SessionSent, nameof(SessionUp)))
            Raise(nameof(SessionText));
        if (Set(ref _todayDown, s.TodayRecv, nameof(TodayDown)) | Set(ref _todayUp, s.TodaySent, nameof(TodayUp)))
        {
            Raise(nameof(TodayText)); Raise(nameof(TodayTotal));
        }
        if (!_busy) Blocked = s.Blocked;
        Raise(nameof(HasActivity));
    }

    public double RateDown => _rateDown;
    public double RateUp => _rateUp;
    public double Activity => _rateDown + _rateUp;
    public long SessionDown => _sessionDown;
    public long SessionUp => _sessionUp;
    public long TodayDown => _todayDown;
    public long TodayUp => _todayUp;
    public long TodayTotal => _todayDown + _todayUp;
    public bool HasActivity => Blocked || _sessionDown + _sessionUp + _todayDown + _todayUp > 0;

    public string RateDownText => _rateDown < 1 ? "–" : Fmt.Rate(_rateDown);
    public string RateUpText => _rateUp < 1 ? "–" : Fmt.Rate(_rateUp);
    public string TodayText => $"↓ {Fmt.Bytes(_todayDown)}   ↑ {Fmt.Bytes(_todayUp)}";
    public string SessionText => $"↓ {Fmt.Bytes(_sessionDown)}   ↑ {Fmt.Bytes(_sessionUp)}";

    public bool Blocked { get => _blocked; set { if (Set(ref _blocked, value)) Raise(nameof(HasActivity)); } }
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
}

public sealed class TopRow
{
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public bool HasIcon => Icon != null;
    public required string Down { get; init; }
    public required string Up { get; init; }
    public double Share { get; init; } // 0..1 of the busiest app
}

public sealed class ConnRow : Observable
{
    public required string Id { get; init; }
    public required AppInfo Info { get; init; }
    public int Pid { get; init; }
    public string AppName => Info.Name;
    public ImageSource? Icon => IconCache.Get(Info);
    public bool HasIcon => Icon != null;
    public required string Protocol { get; init; }
    public required string Local { get; init; }
    public required string Remote { get; init; }
    public System.Net.IPAddress? RemoteIp { get; init; }

    string _state = "", _host = "";
    public string State { get => _state; set => Set(ref _state, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
}

public sealed class HistRow
{
    public required AppTotal Total { get; init; }
    public string Name => Total.Info.Name;
    public string Detail => Total.Info.IsPseudo ? "" : Total.Info.Path;
    public ImageSource? Icon => IconCache.Get(Total.Info);
    public bool HasIcon => Icon != null;
    public string Down => Fmt.Bytes(Total.Recv);
    public string Up => Fmt.Bytes(Total.Sent);
    public string Sum => Fmt.Bytes(Total.Total);
    public double Share { get; init; }
}

public sealed class AlertRow : Observable
{
    public required AppInfo Info { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string Name => Info.Name;
    public string Detail => Info.Path;
    public string When => Time.ToString("ddd HH:mm");
    public ImageSource? Icon => IconCache.Get(Info);
    public bool HasIcon => Icon != null;

    string _status = "";
    bool _busy, _wasBlocked;
    public string Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(Pending)); Raise(nameof(Handled)); } }
    }
    public bool Pending => _status.Length == 0;
    public bool Handled => !Pending;
    public bool WasBlocked { get => _wasBlocked; set => Set(ref _wasBlocked, value); }
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
}
