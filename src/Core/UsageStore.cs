using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace NetLedger.Core;

public sealed record AppTotal(AppInfo Info, long Sent, long Recv)
{
    public long Total => Sent + Recv;
}

public sealed record Timeline(long FromMinute, int BucketMinutes, DateTime[] Starts, long[] Sent, long[] Recv);

/// <summary>Per-app, per-minute usage history in SQLite (%LOCALAPPDATA%\NetLedger\netledger.db).</summary>
public sealed class UsageStore : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();
    readonly Dictionary<string, long> _ids = new();
    public int InitialAppCount { get; }

    public static long NowMinute() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
    public static long MinuteOf(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds() / 60;
    public static long TodayStartMinute() => MinuteOf(DateTime.Today);

    public UsageStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(@"
CREATE TABLE IF NOT EXISTS apps(
  id INTEGER PRIMARY KEY,
  key TEXT NOT NULL UNIQUE,
  path TEXT NOT NULL,
  name TEXT NOT NULL,
  first_seen INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS usage(
  app_id INTEGER NOT NULL,
  minute INTEGER NOT NULL,
  sent INTEGER NOT NULL,
  recv INTEGER NOT NULL,
  PRIMARY KEY(app_id, minute)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_usage_minute ON usage(minute);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, key FROM apps";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _ids[r.GetString(1)] = r.GetInt64(0);
        }
        InitialAppCount = _ids.Count;

        // Keep about 13 months of history.
        Exec("DELETE FROM usage WHERE minute < $m", ("$m", NowMinute() - 400L * 1440));
    }

    void Exec(string sql, params (string, object)[] ps)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    public bool IsKnown(string key) { lock (_lock) return _ids.ContainsKey(key); }

    public long EnsureApp(AppInfo info)
    {
        lock (_lock)
        {
            if (_ids.TryGetValue(info.Key, out var id)) return id;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO apps(key, path, name, first_seen) VALUES($k, $p, $n, $t) " +
                              "ON CONFLICT(key) DO UPDATE SET name = excluded.name RETURNING id";
            cmd.Parameters.AddWithValue("$k", info.Key);
            cmd.Parameters.AddWithValue("$p", info.Path);
            cmd.Parameters.AddWithValue("$n", info.Name);
            cmd.Parameters.AddWithValue("$t", NowMinute());
            id = Convert.ToInt64(cmd.ExecuteScalar());
            _ids[info.Key] = id;
            return id;
        }
    }

    public void AddUsage(long minute, IEnumerable<(AppInfo Info, long Sent, long Recv)> rows)
    {
        lock (_lock)
        {
            var resolved = new List<(long Id, long Sent, long Recv)>();
            foreach (var (info, sent, recv) in rows) resolved.Add((EnsureApp(info), sent, recv));
            if (resolved.Count == 0) return;

            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO usage(app_id, minute, sent, recv) VALUES($a, $m, $s, $r) " +
                              "ON CONFLICT(app_id, minute) DO UPDATE SET sent = sent + excluded.sent, recv = recv + excluded.recv";
            var pa = cmd.Parameters.Add("$a", SqliteType.Integer);
            var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pr = cmd.Parameters.Add("$r", SqliteType.Integer);
            foreach (var (id, sent, recv) in resolved)
            {
                pa.Value = id;
                pm.Value = minute; ps.Value = sent; pr.Value = recv;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Usage per app since <paramref name="fromMinute"/>, largest first.</summary>
    public List<AppTotal> AppTotals(long fromMinute)
    {
        var list = new List<AppTotal>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT a.key, a.path, a.name, SUM(u.sent), SUM(u.recv)
FROM usage u JOIN apps a ON a.id = u.app_id
WHERE u.minute >= $f GROUP BY a.id ORDER BY SUM(u.sent) + SUM(u.recv) DESC";
            cmd.Parameters.AddWithValue("$f", fromMinute);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AppTotal(ProcessResolver.FromStored(r.GetString(0), r.GetString(1), r.GetString(2)),
                    r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    /// <summary>Bucketed totals for the last <paramref name="count"/> buckets, optionally for one app.</summary>
    public Timeline QueryTimeline(int bucketMinutes, int count, string? appKey)
    {
        int off = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes; // align buckets to local time
        long nowBucket = (NowMinute() + off) / bucketMinutes;
        long firstBucket = nowBucket - count + 1;
        long from = firstBucket * bucketMinutes - off;
        var sent = new long[count];
        var recv = new long[count];
        var starts = new DateTime[count];
        for (int i = 0; i < count; i++)
            starts[i] = DateTimeOffset.FromUnixTimeSeconds((from + (long)i * bucketMinutes) * 60).LocalDateTime;

        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (u.minute + $off) / $b AS bk, SUM(u.sent), SUM(u.recv) FROM usage u " +
                              (appKey != null ? "JOIN apps a ON a.id = u.app_id AND a.key = $k " : "") +
                              "WHERE u.minute >= $f GROUP BY bk";
            cmd.Parameters.AddWithValue("$off", off);
            cmd.Parameters.AddWithValue("$b", bucketMinutes);
            cmd.Parameters.AddWithValue("$f", from);
            if (appKey != null) cmd.Parameters.AddWithValue("$k", appKey);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long idx = r.GetInt64(0) - firstBucket;
                if (idx < 0 || idx >= count) continue;
                sent[idx] = r.GetInt64(1);
                recv[idx] = r.GetInt64(2);
            }
        }
        return new Timeline(from, bucketMinutes, starts, sent, recv);
    }

    public void ClearUsage() { lock (_lock) Exec("DELETE FROM usage"); }

    public string GetSetting(string key, string fallback)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string ?? fallback;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
            Exec("INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$k", key), ("$v", value));
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
