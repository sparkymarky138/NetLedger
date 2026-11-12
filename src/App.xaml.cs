using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NetLedger.Core;
using NetLedger.UI;

namespace NetLedger;

public partial class App : Application
{
    const string MutexName = @"Global\NetLedger.SingleInstance";
    const string ShowEventName = @"Global\NetLedger.Show";
    const string ExitEventName = @"Global\NetLedger.Exit";

    Mutex? _mutex;
    bool _ownsMutex;
    MonitorEngine? _engine;
    MainWindow? _window;
    TrayIcon? _tray;
    bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

        if (args.Contains("--selftest"))
        {
            int i = Array.IndexOf(args, "--selftest");
            string log = i + 1 < e.Args.Length ? e.Args[i + 1] : Path.Combine(AppPaths.DataDir, "selftest.log");
            Shutdown(SelfTest.Run(log));
            return;
        }
        if (args.Contains("--uninstall") || args.Contains("--remove-rules"))
        {
            Shutdown(Uninstall(full: args.Contains("--uninstall"), purge: args.Contains("--purge"), quiet: args.Contains("--quiet")));
            return;
        }

        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Already running: bring the existing window forward instead.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            AppPaths.LogError("UI", ev.Exception);
            MessageBox.Show("Something went wrong:\n\n" + ev.Exception.Message + "\n\nDetails were written to " + AppPaths.ErrorLog,
                "NetLedger", MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppPaths.LogError("Fatal", (Exception)ev.ExceptionObject);

        ListenForSignal(ShowEventName, ShowMainWindow);
        ListenForSignal(ExitEventName, ExitApp);

        _engine = new MonitorEngine(AppPaths.Database);
        _engine.Start();

        _window = new MainWindow(_engine);
        _tray = new TrayIcon(ShowMainWindow, () => { ShowMainWindow(); _window.ShowPage("alerts"); }, ExitApp,
            d => _window.SnoozeFor(d), () => _window.ResumeNow());
        _window.Notify = (title, text, onClick) => _tray.Notify(title, text, onClick);
        _window.StatusText = s => _tray.SetText(s);
        _window.SnoozeStateChanged = (snoozed, label) => _tray.SetSnoozed(snoozed, label);
        _engine.SnoozeChanged += () => Dispatcher.BeginInvoke(() => _window.UpdateSnoozeUi());
        _window.UpdateSnoozeUi();

        _engine.NewApp += info => Dispatcher.BeginInvoke(() => _window.AddAlert(info));

        if (_engine.EtwError != null)
            _tray.Notify("NetLedger can't see traffic", _engine.EtwError, ShowMainWindow);

        if (args.Contains("--minimized")) _window.StartHidden();
        else _window.Show();

        if (args.Contains("--snapshot"))
        {
            // Diagnostic: collect ~10 s of data, render every page to PNG in the given folder, then exit.
            int i = Array.IndexOf(args, "--snapshot");
            string dir = i + 1 < e.Args.Length ? e.Args[i + 1] : AppPaths.DataDir;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                try { await _window.SaveSnapshots(dir); }
                catch (Exception ex) { AppPaths.LogError("Snapshot", ex); }
                ExitApp();
            };
            t.Start();
        }
    }

    void ListenForSignal(string name, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(action);
        }) { IsBackground = true, Name = "Signal " + name }.Start();
    }

    public void ShowMainWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false;
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _window?.CloseForExit(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _engine?.Dispose(); } catch (Exception ex) { AppPaths.LogError("Shutdown", ex); }
        if (_ownsMutex) { try { _mutex?.ReleaseMutex(); } catch { } }
        Shutdown(0);
    }

    /// <summary>--remove-rules removes firewall rules; --uninstall also removes the sign-in task (and data with --purge).</summary>
    static int Uninstall(bool full, bool purge, bool quiet)
    {
        var sb = new StringBuilder();
        int code = 0;

        if (full)
        {
            // Ask a running copy to exit so it releases the database and the trace session.
            try
            {
                EventWaitHandle.OpenExisting(ExitEventName).Set();
                using var m = new Mutex(false, MutexName);
                try { if (m.WaitOne(8000)) m.ReleaseMutex(); } catch (AbandonedMutexException) { }
                sb.AppendLine("Closed the running NetLedger.");
            }
            catch (WaitHandleCannotBeOpenedException) { /* not running */ }
        }

        try { sb.AppendLine($"Removed {FirewallManager.RemoveAll()} NetLedger firewall rule(s)."); }
        catch (Exception ex) { sb.AppendLine("Could not remove firewall rules: " + ex.Message); code = 1; }

        if (full)
        {
            try
            {
                if (Autostart.IsEnabled()) { Autostart.Disable(); sb.AppendLine("Removed the start-at-sign-in task."); }
            }
            catch (Exception ex) { sb.AppendLine(ex.Message); code = 1; }

            try { EtwNetworkMonitor.StopSession(EtwNetworkMonitor.SessionName); } catch { }

            if (purge)
            {
                try
                {
                    if (Directory.Exists(AppPaths.DataDirNoCreate))
                    {
                        Directory.Delete(AppPaths.DataDirNoCreate, true);
                        sb.AppendLine("Deleted usage history and settings (" + AppPaths.DataDirNoCreate + ").");
                    }
                }
                catch (Exception ex) { sb.AppendLine("Could not delete data folder: " + ex.Message); code = 1; }
            }
            else sb.AppendLine("Usage history kept in " + AppPaths.DataDirNoCreate + " (add --purge to delete it).");

            sb.AppendLine().Append("You can now delete NetLedger.exe.");
        }

        if (!quiet)
            MessageBox.Show(sb.ToString(), "NetLedger", MessageBoxButton.OK,
                code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return code;
    }
}
