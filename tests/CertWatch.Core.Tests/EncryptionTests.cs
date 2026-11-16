using System.Text;
using CertWatch.Core.History;
using CertWatch.Core.Trust;
using Microsoft.Data.Sqlite;

namespace CertWatch.Core.Tests;

public class EncryptionTests
{
    private static Observation Sample(string host = "secret-intranet.contoso.com") => new(
        DateTimeOffset.UtcNow, host, 443, "edge", Verdict.Inspected, "Replaced by Island", "Island", "ABCDEF",
        host, "Island Technology, Inc. Intermediate CA", "Island Root", DateTimeOffset.UtcNow.AddDays(7), "Tls13",
        false, ChangeKind.FirstSeen) { RemoteAddress = "100.64.0.32", EgressInterface = "Island Private Access", EgressKind = "Tunnel", EgressRoute = "100.64.0.0/10 via 172.16.99.2" };

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"certwatch-enc-{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static string RawFile(string path)
    {
        SqliteConnection.ClearAllPools();
        var bytes = new List<byte>();
        foreach (var f in new[] { path, path + "-wal" }) if (File.Exists(f)) bytes.AddRange(File.ReadAllBytes(f));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [Fact]
    public void Cipher_round_trips_and_rejects_tampering_and_other_keys()
    {
        var c = new HistoryCipher(HistoryCipher.NewMasterKey());
        var sealedValue = c.Seal("hello");
        Assert.Equal("hello", c.Open(sealedValue));
        Assert.NotEqual(sealedValue, c.Seal("hello")); // random nonce

        var bytes = Convert.FromBase64String(sealedValue);
        bytes[^1] ^= 1;
        Assert.Null(c.Open(Convert.ToBase64String(bytes)));
        Assert.Null(new HistoryCipher(HistoryCipher.NewMasterKey()).Open(sealedValue));
        Assert.Throws<ArgumentException>(() => new HistoryCipher(new byte[16]));
    }

    [Fact]
    public void Site_names_ips_and_issuers_never_reach_the_file_in_clear()
    {
        var path = TempDb();
        try
        {
            using (var store = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey())))
            {
                store.Add(Sample());
                Assert.Equal("100.64.0.32", store.GetLatest("secret-intranet.contoso.com", 443, "edge")!.RemoteAddress);
            }
            var raw = RawFile(path);
            Assert.DoesNotContain("secret-intranet", raw);
            Assert.DoesNotContain("100.64.0.32", raw);
            Assert.DoesNotContain("Island Technology", raw);
            Assert.DoesNotContain("Island Private Access", raw);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Rows_from_another_key_are_unreadable_and_skipped()
    {
        var path = TempDb();
        try
        {
            using (var store = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey()))) store.Add(Sample());
            using var other = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey()));
            Assert.Null(other.GetLatest("secret-intranet.contoso.com", 443, "edge"));
            Assert.Empty(other.GetSiteSummaries("edge"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Plaintext_history_from_older_builds_is_migrated_and_wiped()
    {
        var path = TempDb();
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE observations (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, host TEXT NOT NULL,
                      port INTEGER NOT NULL, browser TEXT NOT NULL, verdict TEXT NOT NULL, reason TEXT NOT NULL, vendor TEXT,
                      leaf_fp TEXT, leaf_subject TEXT, issuer TEXT, root TEXT, not_after TEXT, protocol TEXT, mtls INTEGER NOT NULL,
                      change TEXT NOT NULL, last_seen TEXT, remote_ip TEXT, egress_if TEXT, egress_kind TEXT, egress_route TEXT);
                    INSERT INTO observations (ts, host, port, browser, verdict, reason, issuer, mtls, change, last_seen, remote_ip, egress_if, egress_kind)
                    VALUES ('2026-11-16T17:32:10.0000000+00:00', 'old-secret.example', 443, 'brave', 'Inspected', 'r',
                            'Island Technology, Inc. Intermediate CA', 0, 'FirstSeen', '2026-11-16T17:40:00.0000000+00:00', '100.64.9.9', 'Island Private Access', 'Tunnel');
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var store = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey())))
            {
                var o = store.GetLatest("old-secret.example", 443, "brave");
                Assert.NotNull(o);
                Assert.Equal("Island Technology, Inc. Intermediate CA", o.Issuer);
                Assert.Equal("100.64.9.9", o.RemoteAddress);
                Assert.Equal(DateTimeOffset.Parse("2026-11-16T17:40:00+00:00"), o.LastSeen);
                Assert.NotNull(store.GetSiteSummaries("brave").Single().LastInspected);
            }
            var raw = RawFile(path);
            Assert.DoesNotContain("old-secret", raw);
            Assert.DoesNotContain("100.64.9.9", raw);
        }
        finally { Cleanup(path); }
    }
}
