using System.Text.Json;
using CertWatch.Core.Trust;
using Microsoft.Data.Sqlite;

namespace CertWatch.Core.History;

/// <summary>
/// SQLite history shared by every browser tab. Encrypted at rest: everything that identifies a site
/// or a connection (host, IPs, certificate details, egress) is sealed by <see cref="HistoryCipher"/>,
/// and rows are found by a keyed hash of the site. Only non-identifying fields stay in the clear
/// (timestamps, browser id, verdict, change kind, mTLS flag, egress kind).
/// A row is added only for a first visit or a change; re-checks with no change update last_seen.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const string Table = "observations_v2";
    private readonly SqliteConnection _db;
    private readonly HistoryCipher _cipher;
    private readonly Lock _lock = new();

    /// <summary>The sealed part of a row.</summary>
    private sealed record Sensitive(
        string Host, int Port, string Reason, string? Vendor, string? LeafFingerprint, string? LeafSubject,
        string? Issuer, string? Root, DateTimeOffset? NotAfter, string? Protocol,
        string? RemoteAddress, string? EgressInterface, string? EgressRoute);

    public HistoryStore(string path, HistoryCipher cipher)
    {
        _cipher = cipher;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        // secure_delete overwrites freed pages, so deleted history doesn't linger in the file.
        Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000; PRAGMA secure_delete=ON;");
        Execute($"""
            CREATE TABLE IF NOT EXISTS {Table} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                site_key TEXT NOT NULL,
                browser TEXT NOT NULL,
                verdict TEXT NOT NULL,
                change TEXT NOT NULL,
                mtls INTEGER NOT NULL,
                egress_kind TEXT,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_obs2_site ON {Table}(site_key, browser, id);
            CREATE INDEX IF NOT EXISTS ix_obs2_browser ON {Table}(browser, id);
            """);
        MigratePlaintextHistory();
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertWatch", "history.db");

    public Observation Add(Observation o)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {Table} (ts, last_seen, site_key, browser, verdict, change, mtls, egress_kind, payload)
                VALUES ($ts, $ts, $site, $browser, $verdict, $change, $mtls, $egressKind, $payload);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", o.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$site", _cipher.SiteKey(o.Host, o.Port));
            cmd.Parameters.AddWithValue("$browser", o.Browser);
            cmd.Parameters.AddWithValue("$verdict", o.Verdict.ToString());
            cmd.Parameters.AddWithValue("$change", o.Change.ToString());
            cmd.Parameters.AddWithValue("$mtls", o.MutualTls ? 1 : 0);
            cmd.Parameters.AddWithValue("$egressKind", (object?)o.EgressKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$payload", _cipher.Seal(JsonSerializer.Serialize(ToSensitive(o))));
            var id = (long)cmd.ExecuteScalar()!;
            return o with { Id = id, LastSeen = o.Timestamp };
        }
    }

    /// <summary>Marks an existing result as confirmed again at <paramref name="seenAt"/>.</summary>
    public void Touch(long id, DateTimeOffset seenAt)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE {Table} SET last_seen=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", seenAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public Observation? GetLatest(string host, int port, string browser)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {Table} WHERE site_key=$k AND browser=$b ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$k", _cipher.SiteKey(host, port));
            cmd.Parameters.AddWithValue("$b", browser);
            return Read(cmd).FirstOrDefault();
        }
    }

    /// <summary>
    /// Every site this browser has visited, once each: its latest result, how many real changes
    /// (not counting the first visit) it has had, and when it was last inspected. Most recently seen first.
    /// </summary>
    public IReadOnlyList<SiteSummary> GetSiteSummaries(string browser)
    {
        lock (_lock)
        {
            var counts = new Dictionary<string, int>();
            using (var countCmd = _db.CreateCommand())
            {
                countCmd.CommandText = $"""
                    SELECT site_key, COUNT(*) FROM {Table}
                    WHERE browser=$b AND change NOT IN ('None', 'FirstSeen')
                    GROUP BY site_key
                    """;
                countCmd.Parameters.AddWithValue("$b", browser);
                using var r = countCmd.ExecuteReader();
                while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            }

            // Most recent inspected result per site (SQLite returns the row holding the MAX).
            var inspected = new Dictionary<string, (DateTimeOffset At, string? By)>();
            using (var inspCmd = _db.CreateCommand())
            {
                inspCmd.CommandText = $"""
                    SELECT site_key, MAX(last_seen), payload FROM {Table}
                    WHERE browser=$b AND verdict='Inspected'
                    GROUP BY site_key
                    """;
                inspCmd.Parameters.AddWithValue("$b", browser);
                using var r = inspCmd.ExecuteReader();
                while (r.Read())
                    inspected[r.GetString(0)] = (DateTimeOffset.Parse(r.GetString(1)), OpenPayload(r.GetString(2))?.Issuer);
            }

            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {Table} WHERE id IN (SELECT MAX(id) FROM {Table} WHERE browser=$b GROUP BY site_key)";
            cmd.Parameters.AddWithValue("$b", browser);
            return Read(cmd)
                .Select(o =>
                {
                    var key = _cipher.SiteKey(o.Host, o.Port);
                    var hasInspection = inspected.TryGetValue(key, out var i);
                    return new SiteSummary(o, counts.GetValueOrDefault(key))
                    {
                        LastInspected = hasInspection ? i.At : null,
                        InspectedBy = hasInspection ? i.By : null,
                    };
                })
                .OrderByDescending(s => s.Latest.LastSeen)
                .ToList();
        }
    }

    /// <summary>First visit and every change for one site, newest first; all browsers when <paramref name="browser"/> is null.</summary>
    public IReadOnlyList<Observation> GetSiteHistory(string host, int port, string? browser = null, int limit = 200)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT * FROM {Table}
                WHERE site_key=$k AND change <> 'None' AND ($b IS NULL OR browser=$b)
                ORDER BY id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$k", _cipher.SiteKey(host, port));
            cmd.Parameters.AddWithValue("$b", (object?)browser ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", limit);
            return Read(cmd).ToList();
        }
    }

    /// <summary>Deletes one site's history for one browser. Returns rows removed.</summary>
    public int DeleteSite(string host, int port, string browser) => Delete(
        $"DELETE FROM {Table} WHERE site_key=$k AND browser=$b",
        ("$k", _cipher.SiteKey(host, port)), ("$b", browser));

    /// <summary>Deletes all history for one browser. Returns rows removed.</summary>
    public int DeleteBrowser(string browser) => Delete($"DELETE FROM {Table} WHERE browser=$b", ("$b", browser));

    /// <summary>Deletes all history for every browser. Returns rows removed.</summary>
    public int DeleteAll() => Delete($"DELETE FROM {Table}");

    private int Delete(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            var removed = cmd.ExecuteNonQuery();
            Execute("PRAGMA wal_checkpoint(TRUNCATE);"); // don't leave deleted rows in the WAL file
            return removed;
        }
    }

    private Sensitive? OpenPayload(string sealedValue) =>
        _cipher.Open(sealedValue) is { } json ? JsonSerializer.Deserialize<Sensitive>(json) : null;

    private static Sensitive ToSensitive(Observation o) => new(
        o.Host, o.Port, o.Reason, o.Vendor, o.LeafFingerprint, o.LeafSubject, o.Issuer, o.Root, o.NotAfter,
        o.Protocol, o.RemoteAddress, o.EgressInterface, o.EgressRoute);

    /// <summary>Rows that can't be decrypted (wrong key, tampered) are skipped.</summary>
    private IEnumerable<Observation> Read(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (OpenPayload(r.GetString(r.GetOrdinal("payload"))) is not { } p) continue;
            var ts = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts")));
            yield return new Observation(
                ts, p.Host, p.Port,
                r.GetString(r.GetOrdinal("browser")),
                Enum.Parse<Verdict>(r.GetString(r.GetOrdinal("verdict"))),
                p.Reason, p.Vendor, p.LeafFingerprint, p.LeafSubject, p.Issuer, p.Root, p.NotAfter, p.Protocol,
                r.GetInt32(r.GetOrdinal("mtls")) != 0,
                Enum.Parse<ChangeKind>(r.GetString(r.GetOrdinal("change"))))
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                LastSeen = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("last_seen"))),
                RemoteAddress = p.RemoteAddress,
                EgressInterface = p.EgressInterface,
                EgressKind = r.IsDBNull(r.GetOrdinal("egress_kind")) ? null : r.GetString(r.GetOrdinal("egress_kind")),
                EgressRoute = p.EgressRoute,
            };
        }
    }

    /// <summary>
    /// Builds before encryption stored history in a plaintext table. Re-encrypt those rows, drop the
    /// table, and rewrite the file so the plaintext pages are gone.
    /// </summary>
    private void MigratePlaintextHistory()
    {
        if (!TableExists("observations")) return;

        var legacy = new List<Observation>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM observations ORDER BY id";
            using var r = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                string? S(string col) => !columns.Contains(col) || r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
                var ts = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts")));
                legacy.Add(new Observation(
                    ts, r.GetString(r.GetOrdinal("host")), r.GetInt32(r.GetOrdinal("port")), r.GetString(r.GetOrdinal("browser")),
                    Enum.Parse<Verdict>(r.GetString(r.GetOrdinal("verdict"))), r.GetString(r.GetOrdinal("reason")),
                    S("vendor"), S("leaf_fp"), S("leaf_subject"), S("issuer"), S("root"),
                    S("not_after") is { } na ? DateTimeOffset.Parse(na) : null, S("protocol"),
                    r.GetInt32(r.GetOrdinal("mtls")) != 0, Enum.Parse<ChangeKind>(r.GetString(r.GetOrdinal("change"))))
                {
                    LastSeen = S("last_seen") is { } ls ? DateTimeOffset.Parse(ls) : ts,
                    RemoteAddress = S("remote_ip"),
                    EgressInterface = S("egress_if"),
                    EgressKind = S("egress_kind"),
                    EgressRoute = S("egress_route"),
                });
            }
        }

        Execute("BEGIN;");
        try
        {
            foreach (var o in legacy)
            {
                var added = Add(o);
                if (o.LastSeen > o.Timestamp) Touch(added.Id, o.LastSeen);
            }
            Execute("DROP TABLE observations;");
            Execute("COMMIT;");
        }
        catch
        {
            Execute("ROLLBACK;");
            throw;
        }
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        Execute("VACUUM;");
    }

    private bool TableExists(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
