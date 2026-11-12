using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace NetLedger.UI;

/// <summary>Notification-area icon. NetLedger keeps monitoring while only this icon is visible.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _snoozeMenu;
    readonly WinForms.ToolStripMenuItem _resumeItem;
    Action? _balloonAction;

    public TrayIcon(Action open, Action openAlerts, Action exit, Action<TimeSpan?> snooze, Action resume)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open NetLedger", null, (_, _) => open());
        menu.Items.Add("New-app alerts", null, (_, _) => openAlerts());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _snoozeMenu = new WinForms.ToolStripMenuItem("Snooze alerts and blocking");
        _snoozeMenu.DropDownItems.Add("For 15 minutes", null, (_, _) => snooze(TimeSpan.FromMinutes(15)));
        _snoozeMenu.DropDownItems.Add("For 1 hour", null, (_, _) => snooze(TimeSpan.FromHours(1)));
        _snoozeMenu.DropDownItems.Add("Until I resume", null, (_, _) => snooze(null));
        menu.Items.Add(_snoozeMenu);
        _resumeItem = new WinForms.ToolStripMenuItem("Resume now", null, (_, _) => resume()) { Visible = false };
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit (stop monitoring)", null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "NetLedger",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
    }

    static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null)
            {
                var big = Icon.ExtractAssociatedIcon(path);
                if (big != null) return new Icon(big, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    public void SetSnoozed(bool snoozed, string? label)
    {
        _snoozeMenu.Visible = !snoozed;
        _resumeItem.Visible = snoozed;
        _resumeItem.Text = snoozed && label != null ? $"Resume now ({label})" : "Resume now";
    }

    public void SetText(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
