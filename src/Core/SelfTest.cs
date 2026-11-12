using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NetLedger.Core;

/// <summary>
/// End-to-end check run with <c>NetLedger.exe --selftest [logfile]</c>: traffic attribution via ETW,
/// firewall block/allow against a private copy of curl.exe, connection table and SQLite.
/// Touches only rules for that private curl copy (plus a RemoveAll check when no other NetLedger rules exist).
/// </summary>
public static class SelfTest
{
    public static int Run(string logPath)
    {
        var log = new StringBuilder();
        bool ok = true;
        void L(string s)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {s}");
            try { File.WriteAllText(logPath, log.ToString()); } catch { }
        }
        void Check(string name, bool pass, string detail = "")
        {
            ok &= pass;
            L($"{(pass ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "NetLedgerSelfTest");
        string curl = Path.Combine(dir, "curl.exe");
        try
        {
            L($"NetLedger self-test. Admin={AppPaths.IsAdmin}  OS={Environment.OSVersion.VersionString}  exe={Environment.ProcessPath}");
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(Environment.SystemDirectory, "curl.exe"), curl, true);
            int preexisting = FirewallManager.CountRules();
            L($"Existing NetLedger rules: {preexisting}. Firewall: {FirewallManager.DescribeState() ?? "unknown"}");

            var agg = new TrafficAggregator();
            using (var etw = new EtwNetworkMonitor())
            {
                etw.ProcessStarted += ProcessResolver.NoteProcessStart;
                etw.Traffic += agg.Add;
                etw.Start();
                Thread.Sleep(1500);

                int code = RunCurl(curl, "https://speed.cloudflare.com/__down?bytes=3000000", 60);
                Check("curl download while allowed", code == 0, $"exit={code}");
                Thread.Sleep(3000);
                var c = agg.Counters.FirstOrDefault(x => x.Info.Path.Equals(curl, StringComparison.OrdinalIgnoreCase));
                Check("ETW attributes the download to curl.exe", c != null && c.Recv > 2_500_000,
                    c == null ? "no counter for curl" : $"recv={c.Recv:N0} sent={c.Sent:N0}");
                L($"ETW events: {etw.EventsSeen:N0}. Apps seen: " +
                  string.Join("; ", agg.Counters.OrderByDescending(x => x.Recv + x.Sent).Take(12)
                      .Select(x => $"{x.Info.Name}={Fmt.Bytes(x.Recv + x.Sent)}")));
            }

            FirewallManager.Block(curl, "curl self-test");
            Check("block rules created", FirewallManager.GetBlockedPaths().Contains(curl));
            var sw = Stopwatch.StartNew();
            int blockedCode = RunCurl(curl, "https://www.example.com/", 20);
            Check("curl fails while blocked", blockedCode != 0, $"exit={blockedCode} after {sw.Elapsed.TotalSeconds:0.0}s");

            if (preexisting == 0)
            {
                // Snooze switches NetLedger's rules off without deleting them, and back on afterwards.
                int off = FirewallManager.SetAllEnabled(false);
                int snoozedCode = RunCurl(curl, "https://www.example.com/", 30);
                Check("snooze: blocked curl works while rules are off", off == 2 && snoozedCode == 0 &&
                      FirewallManager.GetBlockedPaths().Contains(curl), $"rulesOff={off} exit={snoozedCode}");
                int on = FirewallManager.SetAllEnabled(true);
                int resumedCode = RunCurl(curl, "https://www.example.com/", 20);
                Check("resume: curl blocked again", on == 2 && resumedCode != 0 && FirewallManager.CountEnabled() == 2,
                    $"rulesOn={on} exit={resumedCode}");
            }
            else L("SKIP  snooze check (other NetLedger rules exist)");

            FirewallManager.Allow(curl);
            Check("block rules removed on allow", !FirewallManager.GetBlockedPaths().Contains(curl));
            int allowedCode = RunCurl(curl, "https://www.example.com/", 30);
            Check("curl works again after allow", allowedCode == 0, $"exit={allowedCode}");

            if (preexisting == 0)
            {
                FirewallManager.Block(curl, "curl self-test 2");
                int removed = FirewallManager.RemoveAll();
                Check("RemoveAll clears every NetLedger rule", removed == 2 && FirewallManager.CountRules() == 0, $"removed={removed}");
            }
            else L("SKIP  RemoveAll check (other NetLedger rules exist)");

            var conns = ConnectionTable.GetAll();
            Check("connection table readable", conns.Count > 0,
                $"{conns.Count} endpoints, {conns.Count(x => x.State == "Established")} established TCP");

            string db = Path.Combine(dir, "selftest.db");
            if (File.Exists(db)) File.Delete(db);
            using (var store = new UsageStore(db))
            {
                var info = ProcessResolver.InfoForPath(curl);
                long m = UsageStore.NowMinute();
                store.AddUsage(m, new[] { (info, 100L, 2000L) });
                store.AddUsage(m, new[] { (info, 50L, 1000L) });
                var t = store.AppTotals(m - 1).FirstOrDefault();
                var tl = store.QueryTimeline(1, 5, info.Key);
                Check("SQLite history round-trip", t != null && t.Sent == 150 && t.Recv == 3000 && tl.Recv[^1] == 3000,
                    t == null ? "no row" : $"sent={t.Sent} recv={t.Recv} lastBucket={tl.Recv[^1]}");
            }
        }
        catch (Exception ex)
        {
            Check("unexpected exception", false, ex.ToString());
        }
        finally
        {
            try { FirewallManager.Allow(curl); } catch { }
        }

        L(ok ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
        return ok ? 0 : 1;
    }

    static int RunCurl(string curl, string url, int maxSeconds)
    {
        var psi = new ProcessStartInfo(curl, $"-s -S -o NUL --max-time {maxSeconds} \"{url}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
