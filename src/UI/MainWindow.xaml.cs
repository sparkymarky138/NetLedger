using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NetLedger.Core;

namespace NetLedger.UI;

public partial class MainWindow : Window
{
    const int LivePoints = 300;

    readonly MonitorEngine _engine;
    readonly Dictionary<string, AppRow> _rows = new();
    readonly ObservableCollection<AppRow> _apps = new();
    readonly ListCollectionView _appsView;
    readonly ObservableCollection<AlertRow> _alerts = new();
    readonly ObservableCollection<ConnRow> _conns = new();
    readonly Dictionary<string, ConnRow> _connIndex = new();
    readonly ListCollectionView _connView;
    readonly double[] _liveDown = new double[LivePoints];
    readonly double[] _liveUp = new double[LivePoints];
    readonly DateTime[] _liveTime = new DateTime[LivePoints];
    int _liveCount;
    readonly DispatcherTimer _connTimer;
    string _page = "overview";
    string _range = "day";
    AppInfo? _historyApp;
    bool _closingForExit, _trayHintShown;

    public Action<string, string, Action?>? Notify { get; set; }
    public Action<string>? StatusText { get; set; }
    public Action<bool, string?>? SnoozeStateChanged { get; set; }
    bool _wasSnoozed;

    public MainWindow(MonitorEngine engine)
    {
        _engine = engine;
        InitializeComponent();

        _appsView = new ListCollectionView(_apps) { IsLiveSorting = true, IsLiveFiltering = true, Filter = AppFilter };
        _appsView.LiveFilteringProperties.Add(nameof(AppRow.HasActivity));
        ApplyAppSort("activity");
        AppsList.ItemsSource = _appsView;

        _connView = new ListCollectionView(_conns) { Filter = ConnFilter };
        _connView.SortDescriptions.Add(new SortDescription(nameof(ConnRow.AppName), ListSortDirection.Ascending));
        ConnList.ItemsSource = _connView;

        AlertsList.ItemsSource = _alerts;
        _alerts.CollectionChanged += (_, _) => UpdateAlertBadge();

        _connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connTimer.Tick += (_, _) => RefreshConnections();

        _engine.Snapshot += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));

        LoadSettings();
        UpdateBanner();
        UpdateAlertBadge();
    }

    // ------------------------------------------------------------------ window / tray

    public void StartHidden()
    {
        // Create the window handle without showing it, so the tray works straight away.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingForExit)
        {
            if (TrayCheck.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    Notify?.Invoke("NetLedger is still monitoring", "It keeps running in the tray. Right-click the tray icon to exit.", null);
                }
            }
            else
            {
                e.Cancel = true;
                ((App)Application.Current).ExitApp();
            }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ navigation

    void OnNav(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        string page = sender == NavApps ? "apps" : sender == NavConnections ? "connections" : sender == NavHistory ? "history"
            : sender == NavAlerts ? "alerts" : sender == NavSettings ? "settings" : "overview";
        ShowPageCore(page);
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "apps" => NavApps, "connections" => NavConnections, "history" => NavHistory,
            "alerts" => NavAlerts, "settings" => NavSettings, _ => NavOverview
        };
        if (nav.IsChecked == true) ShowPageCore(page);
        else nav.IsChecked = true;
    }

    void ShowPageCore(string page)
    {
        _page = page;
        PageOverview.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PageApps.Visibility = page == "apps" ? Visibility.Visible : Visibility.Collapsed;
        PageConnections.Visibility = page == "connections" ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        PageAlerts.Visibility = page == "alerts" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "connections") { RefreshConnections(); _connTimer.Start(); }
        else _connTimer.Stop();
        if (page == "history") RefreshHistory();
        if (page == "settings") RefreshSettingsInfo();
        if (page == "overview") RedrawLive();
    }

    void OnSeeAllApps(object sender, RoutedEventArgs e) => ShowPage("apps");

    // ------------------------------------------------------------------ live snapshot

    void OnSnapshot(EngineSnapshot s)
    {
        // Live graph buffer
        if (_liveCount < LivePoints) _liveCount++;
        Array.Copy(_liveDown, 1, _liveDown, 0, LivePoints - 1);
        Array.Copy(_liveUp, 1, _liveUp, 0, LivePoints - 1);
        Array.Copy(_liveTime, 1, _liveTime, 0, LivePoints - 1);
        _liveDown[^1] = s.RateRecv;
        _liveUp[^1] = s.RateSent;
        _liveTime[^1] = s.Time;

        string down = Fmt.Rate(s.RateRecv), up = Fmt.Rate(s.RateSent);
        RailDown.Text = down;
        RailUp.Text = up;
        StatusText?.Invoke((_engine.IsSnoozed ? "NetLedger (snoozed)" : "NetLedger") + $"  ↓ {down}  ↑ {up}");

        foreach (var a in s.Apps)
        {
            if (!_rows.TryGetValue(a.Info.Key, out var row))
            {
                row = new AppRow(a.Info);
                _rows[a.Info.Key] = row;
                _apps.Add(row);
            }
            row.Update(a);
        }

        if (!IsVisible) return;

        KpiDown.Text = down;
        KpiUp.Text = up;
        KpiToday.Text = Fmt.Bytes(s.TodayRecv + s.TodaySent);
        KpiTodaySplit.Text = $"↓ {Fmt.Bytes(s.TodayRecv)}   ↑ {Fmt.Bytes(s.TodaySent)}";
        KpiSession.Text = Fmt.Bytes(s.SessionRecv + s.SessionSent);
        KpiSessionSplit.Text = $"↓ {Fmt.Bytes(s.SessionRecv)}   ↑ {Fmt.Bytes(s.SessionSent)}";

        if (_page == "overview")
        {
            RedrawLive();
            var top = s.Apps.Where(a => a.RateRecv + a.RateSent >= 1)
                .OrderByDescending(a => a.RateRecv + a.RateSent).Take(5).ToList();
            double max = top.Count > 0 ? top[0].RateRecv + top[0].RateSent : 1;
            TopList.ItemsSource = top.Select(a => new TopRow
            {
                Name = a.Info.Name, Icon = IconCache.Get(a.Info),
                Down = "↓ " + Fmt.Rate(a.RateRecv), Up = "↑ " + Fmt.Rate(a.RateSent),
                Share = (a.RateRecv + a.RateSent) / max
            }).ToList();
            TopEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void RedrawLive()
    {
        int n = _liveCount;
        if (n < 2) { LiveChart.SetData(Array.Empty<double>(), Array.Empty<double>(), _ => "", true); return; }
        var d = _liveDown[^n..];
        var u = _liveUp[^n..];
        var t = _liveTime[^n..];
        LiveChart.SetData(d, u, i => t[i].ToString("HH:mm:ss"), true);
    }

    // ------------------------------------------------------------------ apps page

    bool AppFilter(object o)
    {
        var r = (AppRow)o;
        if (ShowIdleCheck?.IsChecked != true && !r.HasActivity) return false;
        var q = AppSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Info.Path.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void OnAppFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) _appsView.Refresh();
    }

    void OnAppSort(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && sender is RadioButton { Tag: string mode }) ApplyAppSort(mode);
    }

    void ApplyAppSort(string mode)
    {
        using (_appsView.DeferRefresh())
        {
            _appsView.SortDescriptions.Clear();
            _appsView.LiveSortingProperties.Clear();
            switch (mode)
            {
                case "today":
                    Add(nameof(AppRow.TodayTotal), true);
                    break;
                case "name":
                    Add(nameof(AppRow.Name), false);
                    break;
                case "blocked":
                    Add(nameof(AppRow.Blocked), true);
                    Add(nameof(AppRow.Name), false);
                    break;
                default:
                    Add(nameof(AppRow.Activity), true);
                    Add(nameof(AppRow.TodayTotal), true);
                    break;
            }
            if (mode != "name") _appsView.SortDescriptions.Add(new SortDescription(nameof(AppRow.Name), ListSortDirection.Ascending));
        }

        void Add(string prop, bool desc)
        {
            _appsView.SortDescriptions.Add(new SortDescription(prop, desc ? ListSortDirection.Descending : ListSortDirection.Ascending));
            _appsView.LiveSortingProperties.Add(prop);
        }
    }

    static bool IsWindowsComponent(string path)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase);
    }

    async void OnToggleBlock(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRow row || row.IsBusy || !row.CanBlock) return;
        await SetBlocked(row.Info, !row.Blocked);
    }

    async Task<bool> SetBlocked(AppInfo info, bool block)
    {
        if (block && IsWindowsComponent(info.Path) &&
            MessageBox.Show(this,
                $"{info.Name} is part of Windows.\n\n{info.Path}\n\nBlocking Windows components can break updates, name lookups or other features. Block it anyway?",
                "Block a Windows component?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        _rows.TryGetValue(info.Key, out var row);
        if (row != null) row.IsBusy = true;
        try
        {
            await Task.Run(() => _engine.SetBlocked(info, block));
            if (row != null) row.Blocked = block;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Changing firewall rule", ex);
            MessageBox.Show(this, "Windows Firewall did not accept the change:\n\n" + ex.Message, "NetLedger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            if (row != null) row.IsBusy = false;
        }
    }

    AppRow? SelectedApp => AppsList.SelectedItem as AppRow;

    void OnAppHistory(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } r) return;
        _historyApp = r.Info;
        ShowPage("history");
        RefreshHistory();
    }

    void OnAppOpenFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { CanBlock: true } r && File.Exists(r.Info.Path))
            Process.Start("explorer.exe", $"/select,\"{r.Info.Path}\"");
    }

    void OnAppCopyPath(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { CanBlock: true } r) Clipboard.SetText(r.Info.Path);
    }

    // ------------------------------------------------------------------ connections page

    static readonly ConcurrentDictionary<IPAddress, string> DnsNames = new();
    static readonly ConcurrentDictionary<IPAddress, byte> DnsPending = new();

    static string LookupHost(IPAddress ip)
    {
        if (DnsNames.TryGetValue(ip, out var name)) return name;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return "";
        if (DnsPending.TryAdd(ip, 0))
        {
            Task.Run(async () =>
            {
                try
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    var done = await Task.WhenAny(lookup, Task.Delay(4000));
                    DnsNames[ip] = done == lookup && lookup.IsCompletedSuccessfully ? lookup.Result.HostName : "";
                }
                catch { DnsNames[ip] = ""; }
            });
        }
        return "";
    }

    static string Endpoint(IPEndPoint ep) =>
        ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ep.Address}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";

    async void RefreshConnections()
    {
        bool includeAll = ShowListeningCheck.IsChecked == true;
        List<ConnectionInfo> list;
        try { list = await Task.Run(ConnectionTable.GetAll); }
        catch (Exception ex) { AppPaths.LogError("Connections", ex); return; }

        var seen = new HashSet<string>();
        foreach (var c in list)
        {
            if (!includeAll && (c.Remote == null || IPAddress.IsLoopback(c.Remote.Address))) continue;
            string id = $"{c.Protocol}|{c.Local}|{c.Remote}|{c.Pid}";
            seen.Add(id);
            if (!_connIndex.TryGetValue(id, out var row))
            {
                row = new ConnRow
                {
                    Id = id, Pid = c.Pid, Info = ProcessResolver.Resolve(c.Pid), Protocol = c.Protocol,
                    Local = Endpoint(c.Local), Remote = c.Remote == null ? "" : Endpoint(c.Remote), RemoteIp = c.Remote?.Address
                };
                _connIndex[id] = row;
                _conns.Add(row);
            }
            row.State = c.State;
            if (row.RemoteIp != null && row.Host.Length == 0) row.Host = LookupHost(row.RemoteIp);
        }
        for (int i = _conns.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(_conns[i].Id)) continue;
            _connIndex.Remove(_conns[i].Id);
            _conns.RemoveAt(i);
        }
        ConnCount.Text = $"{_connView.Count} shown";
    }

    bool ConnFilter(object o)
    {
        var q = ConnSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        var r = (ConnRow)o;
        return r.AppName.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Remote.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Local.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Info.Path.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void OnConnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (sender == ShowListeningCheck) RefreshConnections();
        _connView.Refresh();
        ConnCount.Text = $"{_connView.Count} shown";
    }

    // ------------------------------------------------------------------ history page

    void OnRange(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string r }) _range = r;
        if (IsInitialized && _page == "history") RefreshHistory();
    }

    void OnHistoryClear(object sender, RoutedEventArgs e)
    {
        _historyApp = null;
        RefreshHistory();
    }

    void OnHistorySelect(object sender, SelectionChangedEventArgs e)
    {
        if (HistList.SelectedItem is HistRow h && h.Total.Info.Key != _historyApp?.Key)
        {
            _historyApp = h.Total.Info;
            RefreshHistory();
        }
    }

    bool _historyBusy;

    async void RefreshHistory()
    {
        if (_historyBusy) return;
        _historyBusy = true;
        try
        {
            var (bucket, count, fmt, label) = _range switch
            {
                "hour" => (1, 60, "HH:mm", "last hour"),
                "week" => (60, 168, "ddd HH:mm", "last 7 days"),
                "month" => (1440, 30, "MMM d", "last 30 days"),
                _ => (15, 96, "HH:mm", "last 24 hours")
            };
            string? key = _historyApp?.Key;
            var (tl, totals) = await Task.Run(() =>
            {
                _engine.FlushNow();
                var t = _engine.Store.QueryTimeline(bucket, count, key);
                return (t, _engine.Store.AppTotals(t.FromMinute));
            });

            HistoryChart.SetData(tl.Recv.Select(v => (double)v).ToArray(), tl.Sent.Select(v => (double)v).ToArray(),
                i => tl.Starts[i].ToString(fmt, CultureInfo.CurrentCulture), false);

            HistoryScope.Text = _historyApp == null ? $"All apps, {label}" : $"{_historyApp.Name}, {label}";
            HistoryClear.Visibility = _historyApp == null ? Visibility.Collapsed : Visibility.Visible;
            long down = tl.Recv.Sum(), up = tl.Sent.Sum();
            HistorySum.Text = $"↓ {Fmt.Bytes(down)}   ↑ {Fmt.Bytes(up)}   per {(bucket >= 1440 ? "day" : bucket >= 60 ? "hour" : bucket + " min")}";

            long max = totals.Count > 0 ? Math.Max(1, totals[0].Total) : 1;
            var rows = totals.Select(t => new HistRow { Total = t, Share = (double)t.Total / max }).ToList();
            HistList.SelectionChanged -= OnHistorySelect;
            HistList.ItemsSource = rows;
            HistList.SelectedItem = rows.FirstOrDefault(r => r.Total.Info.Key == _historyApp?.Key);
            HistList.SelectionChanged += OnHistorySelect;
        }
        catch (Exception ex) { AppPaths.LogError("History", ex); }
        finally { _historyBusy = false; }
    }

    // ------------------------------------------------------------------ alerts

    public void AddAlert(AppInfo info)
    {
        if (_alerts.Any(a => a.Info.Key == info.Key)) return;
        _alerts.Insert(0, new AlertRow { Info = info });
        Notify?.Invoke("New app on the network", $"{info.Name}\n{info.Path}", () =>
        {
            ((App)Application.Current).ShowMainWindow();
            ShowPage("alerts");
        });
    }

    void UpdateAlertBadge()
    {
        int pending = _alerts.Count(a => a.Pending);
        AlertBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertBadgeText.Text = pending.ToString();
        AlertsEmpty.Visibility = _alerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAlertAllow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlertRow a || a.IsBusy) return;
        a.Status = "✓  Kept allowed";
        UpdateAlertBadge();
    }

    async void OnAlertBlock(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlertRow a || a.IsBusy) return;
        a.IsBusy = true;
        try
        {
            if (await SetBlocked(a.Info, true)) { a.WasBlocked = true; a.Status = "✕  Blocked"; }
        }
        finally { a.IsBusy = false; }
        UpdateAlertBadge();
    }

    void OnClearAlerts(object sender, RoutedEventArgs e)
    {
        foreach (var a in _alerts.Where(a => !a.Pending).ToList()) _alerts.Remove(a);
    }

    // ------------------------------------------------------------------ settings

    void LoadSettings()
    {
        AlertsCheck.IsChecked = _engine.AlertsEnabled;
        TrayCheck.IsChecked = _engine.Store.GetSetting("close_to_tray", "1") == "1";
        try { AutostartCheck.IsChecked = Autostart.IsEnabled(); } catch { AutostartCheck.IsChecked = false; }
        DataPath.Text = "History and settings are stored in " + AppPaths.DataDir;
        var v = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text = $"NetLedger {v?.ToString(3)}. Counts traffic with Windows' own kernel network events (ETW) and controls access with " +
                         "Windows Firewall rules grouped under \"NetLedger\". Run NetLedger.exe --uninstall to remove every rule it created.";
    }

    void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        _engine.AlertsEnabled = AlertsCheck.IsChecked == true;
        _engine.Store.SetSetting("close_to_tray", TrayCheck.IsChecked == true ? "1" : "0");
    }

    void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AutostartCheck.IsChecked == true) Autostart.Enable();
            else Autostart.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "NetLedger", MessageBoxButton.OK, MessageBoxImage.Warning);
            try { AutostartCheck.IsChecked = Autostart.IsEnabled(); } catch { }
        }
    }

    async void RefreshSettingsInfo()
    {
        var (state, count) = await Task.Run(() => (FirewallManager.DescribeState(), _engine.BlockedCount));
        FirewallState.Text = state == null ? "Could not read the Windows Firewall state." : "Windows Firewall for the active network: " + state;
        FirewallCount.Text = count == 1 ? "1 app is blocked by NetLedger." : $"{count} apps are blocked by NetLedger.";
        UpdateBanner(state);
    }

    async void OnRemoveAllRules(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove every firewall rule NetLedger created? All blocked apps will be allowed again.",
                "NetLedger", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            int n = await Task.Run(_engine.RemoveAllRules);
            foreach (var r in _rows.Values) r.Blocked = false;
            MessageBox.Show(this, $"Removed {n} rule(s).", "NetLedger");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetLedger", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshSettingsInfo();
    }

    void OnOpenFirewall(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("wf.msc") { UseShellExecute = true });

    void OnOpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });

    void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all saved usage history? Current counters keep running.", "NetLedger",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _engine.FlushNow();
        _engine.Store.ClearUsage();
        _engine.FlushNow();
    }

    void UpdateBanner(string? firewallState = null)
    {
        string? msg = null;
        if (_engine.EtwError != null)
            msg = "NetLedger cannot read network traffic: " + _engine.EtwError + " Live counts and history will stay empty.";
        else if (firewallState != null && firewallState.Contains("OFF"))
            msg = "Windows Firewall is switched off for the active network (" + firewallState + "), so blocking apps has no effect.";
        Banner.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = msg ?? "";
        if (_engine.EtwError != null)
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusLabel.Text = "Not monitoring";
        }
    }

    // ------------------------------------------------------------------ snooze

    public async void SnoozeFor(TimeSpan? duration)
    {
        try { await Task.Run(() => _engine.Snooze(duration)); }
        catch (Exception ex)
        {
            AppPaths.LogError("Snooze", ex);
            MessageBox.Show(this, "Could not snooze: " + ex.Message, "NetLedger", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async void ResumeNow()
    {
        try { await Task.Run(_engine.Resume); }
        catch (Exception ex)
        {
            AppPaths.LogError("Resume", ex);
            MessageBox.Show(this, "Could not switch blocking back on: " + ex.Message, "NetLedger", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnSnooze(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out int minutes))
            SnoozeFor(minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
    }

    void OnResume(object sender, RoutedEventArgs e) => ResumeNow();

    static string SnoozeLabel(DateTime until) =>
        until == DateTime.MaxValue ? "until you resume"
        : until.Date == DateTime.Today ? $"until {until:HH:mm}"
        : $"until {until:ddd HH:mm}";

    public void UpdateSnoozeUi()
    {
        bool snoozed = _engine.SnoozeUntil is not null;
        string? label = snoozed ? SnoozeLabel(_engine.SnoozeUntil!.Value) : null;
        int blocked = _engine.BlockedCount;

        SnoozeBanner.Visibility = snoozed ? Visibility.Visible : Visibility.Collapsed;
        SnoozeText.Text = snoozed
            ? $"Snoozed {label}. New-app alerts are muted" +
              (blocked == 0 ? "" : blocked == 1 ? " and your 1 blocked app is allowed for now" : $" and your {blocked} blocked apps are allowed for now") +
              ". Usage is still recorded."
            : "";
        SnoozeSettingsState.Text = snoozed ? $"Snoozed {label}." : "Not snoozed.";
        Snooze15.IsEnabled = Snooze60.IsEnabled = SnoozeForever.IsEnabled = !snoozed;
        SnoozeResume.IsEnabled = snoozed;

        if (_engine.EtwError == null)
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource(snoozed ? "UpBrush" : "OkBrush");
            StatusLabel.Text = snoozed ? "Snoozed " + label : "Monitoring";
        }
        SnoozeStateChanged?.Invoke(snoozed, label);

        if (_wasSnoozed && !snoozed)
            Notify?.Invoke("Snooze is over", "Blocking and new-app alerts are back on.", null);
        _wasSnoozed = snoozed;
    }

    public async Task SaveSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var page in new[] { "overview", "apps", "connections", "history", "alerts", "settings" })
            await SnapshotPage(dir, page, page);

        // Also capture the snoozed state, then resume.
        await Task.Run(() => _engine.Snooze(TimeSpan.FromMinutes(15)));
        UpdateSnoozeUi();
        await SnapshotPage(dir, "apps", "snoozed-apps");
        await SnapshotPage(dir, "settings", "snoozed-settings");
        await Task.Run(_engine.Resume);
        UpdateSnoozeUi();

        await CheckAlertButtons(dir);
    }

    /// <summary>Diagnostic: verifies a real mouse click on the alert buttons would reach them.</summary>
    async Task CheckAlertButtons(string dir)
    {
        var log = new System.Text.StringBuilder();
        var demo = new AlertRow { Info = ProcessResolver.InfoForPath(Path.Combine(Environment.SystemDirectory, "curl.exe")) };
        _alerts.Insert(0, demo);
        ShowPage("alerts");
        await Task.Delay(600);
        UpdateLayout();

        if (AlertsList.ItemContainerGenerator.ContainerFromItem(demo) is ListBoxItem item)
        {
            foreach (var btn in FindChildren<Button>(item))
            {
                var center = btn.TranslatePoint(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2), this);
                var hit = InputHitTest(center) as DependencyObject;
                bool reaches = hit != null && (hit == btn || btn.IsAncestorOf(hit));
                log.AppendLine($"{(reaches ? "PASS" : "FAIL")}  click on '{btn.Content}' reaches the button (hit: {hit?.GetType().Name})");
            }
            await SnapshotPage(dir, "alerts", "alert-before");
            var keep = FindChildren<Button>(item).First(b => (string)b.Content == "Keep allowed");
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(keep);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); // same as a click
            await Task.Delay(300);
            log.AppendLine($"{(demo.Handled ? "PASS" : "FAIL")}  after Keep allowed: status='{demo.Status}', badge pending={_alerts.Count(a => a.Pending)}");
            await SnapshotPage(dir, "alerts", "alert-after");
        }
        else log.AppendLine("FAIL  demo alert row not found");

        _alerts.Remove(demo);
        File.WriteAllText(Path.Combine(dir, "alert-check.txt"), log.ToString());
    }

    static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindChildren<T>(child)) yield return d;
        }
    }

    async Task SnapshotPage(string dir, string page, string file)
    {
        ShowPage(page);
        await Task.Delay(page is "connections" or "history" ? 2500 : 600);
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file + ".png"));
        enc.Save(fs);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshSettingsInfo();
    }
}

/// <summary>share (0..1) × available width → bar width.</summary>
public sealed class ShareWidthConverter : IMultiValueConverter
{
    public static readonly ShareWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double share = values.Length > 0 && values[0] is double s ? s : 0;
        double width = values.Length > 1 && values[1] is double w ? w : 0;
        return Math.Max(2, Math.Clamp(share, 0, 1) * width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
