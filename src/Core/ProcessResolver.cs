using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NetLedger.Core;

/// <summary>An application identified by its executable path.</summary>
public sealed record AppInfo(string Key, string Path, string Name)
{
    /// <summary>Pseudo-apps (kernel, unknown) cannot be blocked: they have no executable path.</summary>
    public bool IsPseudo => Path.Length == 0;
    public string FileName => IsPseudo ? "" : System.IO.Path.GetFileName(Path);
    public string Folder => IsPseudo ? "" : System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Maps process IDs to executables, including short-lived processes seen via process-start events.</summary>
public static class ProcessResolver
{
    public static readonly AppInfo System = new("system", "", "System (Windows kernel)");
    public static readonly AppInfo Unknown = new("unknown", "", "Unknown or exited process");

    // pid -> (path, expiry). Entries from process-start events never expire; they are replaced when the PID is reused.
    static readonly ConcurrentDictionary<int, (string Path, long Expires)> PidPaths = new();
    static readonly ConcurrentDictionary<string, AppInfo> Infos = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string>? _devices;
    static readonly object DevLock = new();

    public static void NoteProcessStart(int pid, string? ntImagePath)
    {
        string? path = TryQueryPath(pid);
        if (path == null && ntImagePath != null) path = DevicePathToDos(ntImagePath);
        if (path != null) PidPaths[pid] = (path, long.MaxValue);
        else PidPaths.TryRemove(pid, out _);
    }

    public static AppInfo Resolve(int pid)
    {
        if (pid == 0 || pid == 4) return System;
        long now = Environment.TickCount64;
        if (PidPaths.TryGetValue(pid, out var e) && e.Expires > now) return InfoForPath(e.Path);
        var path = TryQueryPath(pid);
        if (path == null) return Unknown;
        PidPaths[pid] = (path, now + 60_000);
        return InfoForPath(path);
    }

    public static AppInfo FromStored(string key, string path, string name)
    {
        if (key == System.Key) return System;
        if (key == Unknown.Key) return Unknown;
        return Infos.GetOrAdd(path, p => new AppInfo(p.ToLowerInvariant(), p, name));
    }

    public static AppInfo InfoForPath(string path) =>
        Infos.GetOrAdd(path, p => new AppInfo(p.ToLowerInvariant(), p, DisplayName(p)));

    static string DisplayName(string path)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d) && d.Length <= 60) return d;
        }
        catch { /* file may be gone */ }
        return global::System.IO.Path.GetFileNameWithoutExtension(path);
    }

    static string? TryQueryPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            int size = buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    static string? DevicePathToDos(string ntPath)
    {
        lock (DevLock)
        {
            if (_devices == null)
            {
                _devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var buf = new char[512];
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    string drive = c + ":";
                    if (QueryDosDeviceW(drive, buf, buf.Length) > 0)
                    {
                        string dev = new string(buf).Split('\0')[0];
                        if (dev.Length > 0) _devices[dev] = drive;
                    }
                }
            }
            foreach (var kv in _devices)
                if (ntPath.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase))
                    return kv.Value + ntPath.Substring(kv.Key.Length);
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, [Out] char[] buf, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int QueryDosDeviceW(string device, [Out] char[] target, int max);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
