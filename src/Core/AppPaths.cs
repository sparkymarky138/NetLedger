using System;
using System.IO;
using System.Security.Principal;

namespace NetLedger.Core;

public static class AppPaths
{
    public const string AppName = "NetLedger";

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DataDirNoCreate =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string Database => Path.Combine(DataDir, "netledger.db");
    public static string ErrorLog => Path.Combine(DataDir, "error.log");

    public static bool IsAdmin =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void LogError(string context, Exception ex)
    {
        try { File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\r\n"); }
        catch { /* logging must never throw */ }
    }
}

public static class Fmt
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double b)
    {
        if (b < 0) b = 0;
        int i = 0;
        while (b >= 1024 && i < Units.Length - 1) { b /= 1024; i++; }
        if (i == 0) return $"{b:0} B";
        return b < 10 ? $"{b:0.00} {Units[i]}" : b < 100 ? $"{b:0.0} {Units[i]}" : $"{b:0} {Units[i]}";
    }

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";
}
