using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace NetLedger.Core;

/// <summary>
/// Creates and removes Windows Firewall rules through the INetFwPolicy2 COM API (HNetCfg.FwPolicy2).
/// Every rule carries the group name <see cref="Group"/> so they can all be found and removed later.
/// </summary>
public static class FirewallManager
{
    public const string Group = "NetLedger";
    const int ActionBlock = 0;
    const int DirIn = 1, DirOut = 2;
    const int AllProfiles = 0x7FFFFFFF;

    static dynamic Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

    static dynamic NewRule() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;

    static bool IsOurs(dynamic rule)
    {
        object? g = rule.Grouping;
        return g is string s && s == Group;
    }

    static string? AppOf(dynamic rule)
    {
        object? a = rule.ApplicationName;
        return a as string;
    }

    /// <summary>Executable paths that currently have a NetLedger block rule.</summary>
    public static HashSet<string> GetBlockedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic pol = Policy();
        foreach (dynamic r in pol.Rules)
        {
            bool ours = IsOurs(r);
            if (!ours) continue;
            int action = (int)r.Action;
            if (action != ActionBlock) continue;
            string? app = AppOf(r);
            if (!string.IsNullOrEmpty(app)) set.Add(app);
        }
        return set;
    }

    public static int CountRules()
    {
        int n = 0;
        dynamic pol = Policy();
        foreach (dynamic r in pol.Rules) { bool ours = IsOurs(r); if (ours) n++; }
        return n;
    }

    /// <summary>
    /// Switches every NetLedger rule on or off without deleting it (used by snooze).
    /// Returns how many rules changed.
    /// </summary>
    public static int SetAllEnabled(bool enabled)
    {
        int n = 0;
        dynamic pol = Policy();
        foreach (dynamic r in pol.Rules)
        {
            bool ours = IsOurs(r);
            if (!ours) continue;
            bool current = (bool)r.Enabled;
            if (current == enabled) continue;
            r.Enabled = enabled;
            n++;
        }
        return n;
    }

    /// <summary>Number of NetLedger rules that are currently switched on.</summary>
    public static int CountEnabled()
    {
        int n = 0;
        dynamic pol = Policy();
        foreach (dynamic r in pol.Rules)
        {
            bool ours = IsOurs(r);
            if (ours && (bool)r.Enabled) n++;
        }
        return n;
    }

    /// <summary>Adds inbound and outbound block rules for one executable (created switched off while snoozed).</summary>
    public static void Block(string path, string displayName, bool enabled = true)
    {
        Allow(path); // never stack duplicates
        string tag = ShortHash(path);
        dynamic pol = Policy();
        foreach (int dir in new[] { DirOut, DirIn })
        {
            dynamic rule = NewRule();
            rule.Name = $"NetLedger: block {displayName} ({(dir == DirOut ? "out" : "in")}) [{tag}]";
            rule.Description = $"Created by NetLedger for {path}. Remove with NetLedger.exe --uninstall.";
            rule.ApplicationName = path;
            rule.Protocol = 256; // any
            rule.Direction = dir;
            rule.Action = ActionBlock;
            rule.Profiles = AllProfiles;
            rule.Grouping = Group;
            rule.Enabled = enabled;
            pol.Rules.Add(rule);
        }
    }

    /// <summary>Removes NetLedger's rules for one executable (Windows' default then allows it).</summary>
    public static int Allow(string path) =>
        RemoveWhere(app => string.Equals(app, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes every rule NetLedger has ever created.</summary>
    public static int RemoveAll() => RemoveWhere(_ => true);

    static int RemoveWhere(Func<string?, bool> matchApp)
    {
        dynamic pol = Policy();
        var names = new List<string>();
        foreach (dynamic r in pol.Rules)
        {
            bool ours = IsOurs(r);
            if (!ours) continue;
            string? app = AppOf(r);
            if (matchApp(app)) names.Add((string)r.Name);
        }
        foreach (var n in names) pol.Rules.Remove(n);
        return names.Count;
    }

    /// <summary>Human-readable firewall state for the active profiles, or null if it cannot be read.</summary>
    public static string? DescribeState()
    {
        try
        {
            dynamic pol = Policy();
            int current = (int)pol.CurrentProfileTypes;
            var parts = new List<string>();
            foreach (var (bit, name) in new[] { (1, "Domain"), (2, "Private"), (4, "Public") })
            {
                if ((current & bit) == 0) continue;
                bool on = (bool)pol.FirewallEnabled[bit];
                parts.Add($"{name}: {(on ? "on" : "OFF")}");
            }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch { return null; }
    }

    /// <summary>True if Windows Firewall is switched off for any active profile (blocking would not work).</summary>
    public static bool AnyActiveProfileOff()
    {
        var s = DescribeState();
        return s != null && s.Contains("OFF");
    }

    static string ShortHash(string path)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }
}
