using System.Net;
using CertWatch.Core.Network;

namespace CertWatch.Core.Tests;

public class BrowserPathTests
{
    private static readonly IPAddress TunnelAddress = IPAddress.Parse("100.64.0.78");

    private static BrowserConnection Conn(string ip, int port = 443) => new(IPAddress.Parse(ip), port, 4242);

    [Fact]
    public void Browser_connected_to_the_address_CertWatch_checked_is_the_same_path()
    {
        var conns = new[] { Conn("34.120.1.2"), Conn("100.64.0.78") };
        Assert.Equal(BrowserPathMatch.Same, BrowserPath.Compare(TunnelAddress, 443, EgressKind.Tunnel, conns));
    }

    [Fact]
    public void Ipv4_mapped_addresses_still_match()
    {
        var conns = new[] { Conn("::ffff:100.64.0.78") };
        Assert.Equal(BrowserPathMatch.Same, BrowserPath.Compare(TunnelAddress, 443, EgressKind.Tunnel, conns));
    }

    [Fact]
    public void Tunnel_check_with_the_browser_talking_elsewhere_is_a_different_path()
    {
        // The enterprise browser's own gateway: it connects to the gateway, never to the tunnel address.
        var conns = new[] { Conn("34.120.1.2"), Conn("35.190.8.9") };
        Assert.Equal(BrowserPathMatch.Different, BrowserPath.Compare(TunnelAddress, 443, EgressKind.Tunnel, conns));
    }

    [Fact]
    public void Direct_paths_are_not_judged_because_public_addresses_vary()
    {
        var conns = new[] { Conn("160.79.104.10") };
        Assert.Equal(BrowserPathMatch.Unknown, BrowserPath.Compare(IPAddress.Parse("160.79.104.11"), 443, EgressKind.Direct, conns));
    }

    [Fact]
    public void No_browser_connection_on_the_port_is_unknown_not_different()
    {
        // HTTP/3 runs over UDP, which the connection table can't tie to a remote address.
        var conns = new[] { Conn("100.64.0.78", 8443) };
        Assert.Equal(BrowserPathMatch.Unknown, BrowserPath.Compare(TunnelAddress, 443, EgressKind.Tunnel, conns));
        Assert.Equal(BrowserPathMatch.Unknown, BrowserPath.Compare(TunnelAddress, 443, EgressKind.Tunnel, []));
    }

    [Fact]
    public void Unknown_when_CertWatch_has_no_address()
    {
        Assert.Equal(BrowserPathMatch.Unknown, BrowserPath.Compare(null, 443, EgressKind.Tunnel, [Conn("1.2.3.4")]));
    }

    [Fact]
    public void Matches_on_the_site_port_only()
    {
        var conns = new[] { Conn("100.64.0.78", 8443), Conn("34.120.1.2", 8443) };
        Assert.Equal(BrowserPathMatch.Same, BrowserPath.Compare(TunnelAddress, 8443, EgressKind.Tunnel, conns));
    }
}
