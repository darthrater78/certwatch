using System.Net.Security;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.Core.Tests;

public class AddressParserTests
{
    [Theory]
    [InlineData("news.adatum.com/world?x=1", "news.adatum.com", 443)]
    [InlineData("https://news.adatum.com/", "news.adatum.com", 443)]
    [InlineData("HTTPS://News.Adatum.com", "news.adatum.com", 443)]
    [InlineData("portal.contoso.com:8443/login", "portal.contoso.com", 8443)]
    [InlineData("10.0.0.5/admin", "10.0.0.5", 443)]
    public void Parses_web_addresses(string text, string host, int port)
    {
        Assert.Equal(new SiteEndpoint(host, port), AddressParser.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("edge://settings")]
    [InlineData("chrome://newtab/")]
    [InlineData("about:blank")]
    [InlineData("about:preferences")]
    [InlineData("file:///C:/temp/a.html")]
    [InlineData("http://plain.example.com/")]
    [InlineData("how to check certificates")]
    [InlineData("intranet")]
    public void Ignores_non_https_pages_and_searches(string? text)
    {
        Assert.Null(AddressParser.Parse(text));
    }
}

public class ChangeDetectorTests
{
    private static Observation Prev(Verdict v, string fp) => new(DateTimeOffset.UtcNow, "h", 443, "edge", v, "", null,
        fp, null, null, null, null, null, false, ChangeKind.None);

    [Fact] public void First_visit() => Assert.Equal(ChangeKind.FirstSeen, ChangeDetector.Detect(null, Verdict.NotInspected, "A"));
    [Fact] public void Unchanged() => Assert.Equal(ChangeKind.None, ChangeDetector.Detect(Prev(Verdict.NotInspected, "A"), Verdict.NotInspected, "a"));
    [Fact] public void Renewal() => Assert.Equal(ChangeKind.CertificateChanged, ChangeDetector.Detect(Prev(Verdict.NotInspected, "A"), Verdict.NotInspected, "B"));
    [Fact] public void Inspection_started() => Assert.Equal(ChangeKind.InspectionStarted, ChangeDetector.Detect(Prev(Verdict.NotInspected, "A"), Verdict.Inspected, "B"));
    [Fact] public void Inspection_stopped() => Assert.Equal(ChangeKind.InspectionStopped, ChangeDetector.Detect(Prev(Verdict.Inspected, "B"), Verdict.NotInspected, "A"));
    [Fact] public void Inspected_remint_with_same_issuer_is_not_a_change() =>
        Assert.Equal(ChangeKind.None, ChangeDetector.Detect(Prev(Verdict.Inspected, "A") with { Issuer = "Island CA" }, Verdict.Inspected, "B", "Island CA"));
    [Fact] public void Inspected_with_new_issuer_is_a_change() =>
        Assert.Equal(ChangeKind.CertificateChanged, ChangeDetector.Detect(Prev(Verdict.Inspected, "A") with { Issuer = "Island CA" }, Verdict.Inspected, "B", "Zscaler CA"));
    [Fact] public void Other_transition() => Assert.Equal(ChangeKind.VerdictChanged, ChangeDetector.Detect(Prev(Verdict.Error, ""), Verdict.Inspected, "B"));
}

public class ClassifierTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 PublicRoot = TestCerts.Root("Test Public Root");
    private static readonly InspectionClassifier Classifier = new(new PublicRootSet([PublicRoot]));

    private static ProbeResult Probe(SslPolicyErrors errors, bool mtls = false, params System.Security.Cryptography.X509Certificates.X509Certificate2[] chain) => new()
    {
        Endpoint = new SiteEndpoint("news.adatum.com", 443),
        Timestamp = DateTimeOffset.UtcNow,
        Chain = chain,
        PolicyErrors = errors,
        ClientCertificateRequested = mtls,
    };

    [Fact]
    public void Public_chain_is_not_inspected()
    {
        var inter = TestCerts.Issue(PublicRoot, "Test Public CA1", isCa: true);
        var leaf = TestCerts.Issue(inter, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var c = Classifier.Classify(Probe(SslPolicyErrors.None, false, leaf, inter, PublicRoot));
        Assert.Equal(Verdict.NotInspected, c.Verdict);
        Assert.True(c.RootIsPublic);
        Assert.Equal("Test Public CA1", c.IssuerName);
    }

    [Fact]
    public void Partial_public_chain_without_root_is_not_inspected()
    {
        var inter = TestCerts.Issue(PublicRoot, "Test Public CA1", isCa: true);
        var leaf = TestCerts.Issue(inter, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var c = Classifier.Classify(Probe(SslPolicyErrors.RemoteCertificateChainErrors, false, leaf, inter));
        Assert.Equal(Verdict.NotInspected, c.Verdict);
    }

    [Fact]
    public void Public_root_reached_through_cross_signed_copy_is_not_inspected()
    {
        // Like YouTube on Windows: WE2 -> GTS Root R4 (cross-signed by retired GlobalSign Root CA) -> GlobalSign Root CA.
        var retiredRoot = TestCerts.Root("Retired Old Root CA");
        var crossSigned = TestCerts.CrossSign(PublicRoot, retiredRoot);
        var inter = TestCerts.Issue(PublicRoot, "Test Public CA1", isCa: true);
        var leaf = TestCerts.Issue(inter, "www.youtube.com", isCa: false, ["www.youtube.com"]);
        var c = Classifier.Classify(Probe(SslPolicyErrors.None, false, leaf, inter, crossSigned, retiredRoot));
        Assert.Equal(Verdict.NotInspected, c.Verdict);
        Assert.Equal("Test Public Root", c.RootName);
    }

    [Fact]
    public void Publicly_logged_leaf_from_unknown_root_is_not_inspected()
    {
        var root = TestCerts.Root("Some Public Root Missing From Bundle");
        var leaf = TestCerts.Issue(root, "news.adatum.com", isCa: false, ["news.adatum.com"], withSct: true);
        var c = Classifier.Classify(Probe(SslPolicyErrors.None, false, leaf, root));
        Assert.Equal(Verdict.NotInspected, c.Verdict);
        Assert.Contains("Certificate Transparency", c.Reason);
    }

    [Fact]
    public void Trusted_private_root_is_inspected_and_vendor_named()
    {
        var root = TestCerts.Root("Zscaler Root CA");
        var inter = TestCerts.Issue(root, "Zscaler Intermediate Root CA", isCa: true);
        var leaf = TestCerts.Issue(inter, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var c = Classifier.Classify(Probe(SslPolicyErrors.None, false, leaf, inter, root));
        Assert.Equal(Verdict.Inspected, c.Verdict);
        Assert.Equal("Zscaler", c.Vendor);
        Assert.Equal("Zscaler Root CA", c.RootName);
    }

    [Fact]
    public void Trusted_unknown_private_root_is_inspected_without_vendor()
    {
        var root = TestCerts.Root("Contoso ZTNA Root CA");
        var leaf = TestCerts.Issue(root, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var c = Classifier.Classify(Probe(SslPolicyErrors.None, false, leaf, root));
        Assert.Equal(Verdict.Inspected, c.Verdict);
        Assert.Null(c.Vendor);
    }

    [Fact]
    public void Untrusted_self_signed_is_untrusted()
    {
        var self = TestCerts.Root("intranet.local");
        var c = Classifier.Classify(Probe(SslPolicyErrors.RemoteCertificateChainErrors, false, self));
        Assert.Equal(Verdict.Untrusted, c.Verdict);
        Assert.Equal("Self-signed certificate", c.Reason);
    }

    [Fact]
    public void Mtls_flag_is_carried()
    {
        var inter = TestCerts.Issue(PublicRoot, "Test Public CA1", isCa: true);
        var leaf = TestCerts.Issue(inter, "api.contoso.com", isCa: false, ["api.contoso.com"]);
        Assert.True(Classifier.Classify(Probe(SslPolicyErrors.None, true, leaf, inter)).MutualTls);
    }

    [Fact]
    public void Failed_probe_is_error()
    {
        var c = Classifier.Classify(new ProbeResult { Endpoint = new("x.example", 443), Timestamp = DateTimeOffset.UtcNow, Error = "Timed out" });
        Assert.Equal(Verdict.Error, c.Verdict);
    }
}

public class PublicRootSetTests
{
    [Fact]
    public void Embedded_bundle_loads()
    {
        Assert.True(PublicRootSet.LoadEmbedded().Count > 100);
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Round_trips_and_filters_by_browser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certwatch-test-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new HistoryStore(path, new HistoryCipher(HistoryCipher.NewMasterKey()));
            var o = new Observation(DateTimeOffset.UtcNow, "news.adatum.com", 443, "edge", Verdict.Inspected, "r",
                "Zscaler", "FP", "news.adatum.com", "Zscaler Intermediate", "Zscaler Root", DateTimeOffset.UtcNow.AddDays(7),
                "Tls13", true, ChangeKind.InspectionStarted);
            store.Add(o);
            store.Add(o with { Browser = "island", Verdict = Verdict.NotInspected, Change = ChangeKind.FirstSeen });

            var latestEdge = store.GetLatest("news.adatum.com", 443, "edge");
            Assert.NotNull(latestEdge);
            Assert.Equal(Verdict.Inspected, latestEdge.Verdict);
            Assert.True(latestEdge.MutualTls);
            Assert.Equal(ChangeKind.InspectionStarted, latestEdge.Change);
            Assert.Equal(2, store.GetSiteHistory("news.adatum.com", 443).Count);
            Assert.Single(store.GetSiteHistory("news.adatum.com", 443, "island"));

            // Re-checks with no change don't add rows; they move last_seen.
            var later = DateTimeOffset.UtcNow.AddMinutes(5);
            store.Touch(latestEdge.Id, later);
            Assert.Equal(later, store.GetLatest("news.adatum.com", 443, "edge")!.LastSeen, TimeSpan.FromSeconds(1));

            // One summary per site, with the change count excluding the first visit.
            store.Add(o with { Host = "claude.ai", Change = ChangeKind.FirstSeen });
            store.Add(o with { Host = "claude.ai", Verdict = Verdict.NotInspected, Change = ChangeKind.InspectionStopped });
            var summaries = store.GetSiteSummaries("edge");
            Assert.Equal(2, summaries.Count);
            var claude = summaries.Single(x => x.Latest.Host == "claude.ai");
            Assert.Equal(1, claude.ChangeCount);
            Assert.Equal(Verdict.NotInspected, claude.Latest.Verdict);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}
