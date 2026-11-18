using System.Net;
using System.Net.Security;
using CertWatch.Core.Browsers;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Tests;

public class DirectPathTests
{
    private static readonly IPAddress Tunnel = IPAddress.Parse("100.64.0.78");

    private static BrowserConnection Conn(string ip, int port = 443) => new(IPAddress.Parse(ip), port, 11708);

    // 100.64.0.0/10 leaves by the ZTNA tunnel; everything else goes direct.
    private static EgressKind EgressOf(IPAddress a) =>
        a.ToString().StartsWith("100.64.", StringComparison.Ordinal) ? EgressKind.Tunnel : EgressKind.Direct;

    [Fact]
    public void Enterprise_browser_example_tries_direct_addresses_busiest_first_and_skips_the_tunnel()
    {
        // From a test machine: an enterprise browser on a site through its own gateway, plus its own services.
        var conns = new[]
        {
            Conn("100.64.0.78"), Conn("100.64.0.19"),
            Conn("160.79.104.10"), Conn("160.79.104.10"), Conn("160.79.104.10"),
            Conn("208.122.44.245"), Conn("208.122.44.245"), Conn("208.122.44.245"), Conn("208.122.44.245"),
            Conn("13.226.209.37"),
        };
        var candidates = DirectPath.Candidates(conns, 443, Tunnel, EgressOf, lastFound: null);
        Assert.Equal(["208.122.44.245", "160.79.104.10", "13.226.209.37"], candidates.Select(a => a.ToString()));
    }

    [Fact]
    public void The_address_that_served_the_site_last_time_comes_first()
    {
        var conns = new[] { Conn("208.122.44.245"), Conn("208.122.44.245"), Conn("160.79.104.10") };
        var candidates = DirectPath.Candidates(conns, 443, Tunnel, EgressOf, IPAddress.Parse("160.79.104.10"));
        Assert.Equal("160.79.104.10", candidates[0].ToString());
    }

    [Fact]
    public void At_most_max_addresses_and_only_on_the_site_port()
    {
        var conns = Enumerable.Range(1, 20).Select(i => Conn($"203.0.113.{i}")).Append(Conn("198.51.100.1", 8443)).ToList();
        var candidates = DirectPath.Candidates(conns, 443, Tunnel, EgressOf, null);
        Assert.Equal(DirectPath.MaxAddresses, candidates.Count);
        Assert.DoesNotContain(IPAddress.Parse("198.51.100.1"), candidates);
    }

    [Fact]
    public void A_server_for_the_site_presents_a_certificate_for_its_name()
    {
        var root = TestCerts.Root("Test Root");
        var leaf = TestCerts.Issue(root, "claude.ai", isCa: false, ["claude.ai"]);
        var site = new SiteEndpoint("claude.ai", 443);
        ProbeResult Probe(SslPolicyErrors errors, bool withCert = true) => new()
        {
            Endpoint = site, Timestamp = DateTimeOffset.UtcNow, Chain = withCert ? [leaf, root] : [], PolicyErrors = errors,
        };

        Assert.True(DirectPath.ServesSite(Probe(SslPolicyErrors.None)));
        Assert.True(DirectPath.ServesSite(Probe(SslPolicyErrors.RemoteCertificateChainErrors))); // untrusted, but it's the site's
        Assert.False(DirectPath.ServesSite(Probe(SslPolicyErrors.RemoteCertificateNameMismatch))); // a CDN's default certificate
        Assert.False(DirectPath.ServesSite(Probe(SslPolicyErrors.None, withCert: false)));
    }
}
