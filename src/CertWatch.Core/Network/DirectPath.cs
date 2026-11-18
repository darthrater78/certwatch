using System.Net;
using System.Net.Security;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Network;

/// <summary>How the search for an enterprise browser's own path to the site ended.</summary>
public enum DirectPathOutcome
{
    /// <summary>One of the browser's direct addresses served the site; <see cref="DirectPathResult.Check"/> is its result.</summary>
    Found,
    /// <summary>The browser's address served the site, but CertWatch's handshake was inspected on the way anyway (the agent catches its traffic whatever the address).</summary>
    Captured,
    /// <summary>None of the browser's direct connections served the site (or there were none).</summary>
    NotFound,
}

/// <summary>The browser's own path to a site, checked by CertWatch at the address the browser is connected to.</summary>
public sealed record DirectPathResult(DirectPathOutcome Outcome, SiteCheck? Check, int AddressesTried)
{
    public IPAddress? Address => Check?.Probe.RemoteAddress;
}

/// <summary>
/// Picks which of the browser's connections to follow, and recognises the one that serves the site.
/// Pure logic; the connection table and the handshakes are the caller's.
/// </summary>
public static class DirectPath
{
    /// <summary>At most this many addresses are tried per check, so the site's name goes to few unrelated servers.</summary>
    public const int MaxAddresses = 8;

    /// <summary>
    /// The browser's connections on the site's port that leave by a non-tunnel route, excluding the
    /// address CertWatch already checked. The address that served the site last time comes first, then
    /// the busiest (an open page usually holds several connections to its own server).
    /// </summary>
    public static IReadOnlyList<IPAddress> Candidates(IEnumerable<BrowserConnection> connections, int port, IPAddress? checkedAddress,
        Func<IPAddress, EgressKind> egressOf, IPAddress? lastFound, int max = MaxAddresses)
    {
        var ranked = connections
            .Where(c => c.RemotePort == port)
            .Select(c => Normalize(c.Remote))
            .Where(a => checkedAddress is null || !a.Equals(Normalize(checkedAddress)))
            .GroupBy(a => a)
            .OrderByDescending(g => lastFound is not null && g.Key.Equals(lastFound))
            .ThenByDescending(g => g.Count())
            .Select(g => g.Key)
            .Where(a => egressOf(a) != EgressKind.Tunnel)
            .Take(max)
            .ToList();
        return ranked;
    }

    /// <summary>The handshake reached a server for the site: a certificate came back and it's issued for that name.</summary>
    public static bool ServesSite(ProbeResult probe) =>
        probe.Leaf is not null && !probe.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
