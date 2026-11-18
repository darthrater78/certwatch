using System.Net;

namespace CertWatch.Core.Network;

/// <summary>Whether the browser's own traffic to the site takes the path CertWatch's check took.</summary>
public enum BrowserPathMatch
{
    /// <summary>Can't tell (no browser connection seen, a direct path where CDN addresses vary, HTTP/3 over UDP).</summary>
    Unknown,
    /// <summary>The browser is connected to the same address CertWatch checked.</summary>
    Same,
    /// <summary>
    /// CertWatch went through a tunnel to a per-site address, and the browser isn't connected to it:
    /// the browser has its own path (an enterprise browser's own gateway, say). CertWatch's verdict
    /// then describes its own connection, not the browser's.
    /// </summary>
    Different,
}

/// <summary>An established TCP connection owned by one of the browser's processes (read from the OS connection table).</summary>
public sealed record BrowserConnection(IPAddress Remote, int RemotePort, int ProcessId);

/// <summary>The comparison, plus the adapters the browser's connections to that port leave by (for the card).</summary>
public sealed record BrowserPathResult(BrowserPathMatch Match, IReadOnlyList<string> BrowserInterfaces)
{
    public static readonly BrowserPathResult Unknown = new(BrowserPathMatch.Unknown, []);
}

/// <summary>
/// Compares CertWatch's own connection with the browser's connections. Read-only: the inputs come
/// from the OS connection table; nothing is sent to or asked of the browser.
/// </summary>
public static class BrowserPath
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Same: a browser connection to <paramref name="checkedAddress"/> on <paramref name="port"/>.</item>
    /// <item>Different: CertWatch's check went through a tunnel, and the browser has connections on that
    /// port but none to that address. ZTNA clients hand each site its own tunnel address, so a browser
    /// using the tunnel would be connected to it.</item>
    /// <item>Unknown otherwise. A direct path isn't judged: a site's public addresses vary between
    /// lookups, so a missing match proves nothing there.</item>
    /// </list>
    /// </summary>
    public static BrowserPathMatch Compare(IPAddress? checkedAddress, int port, EgressKind? egress, IReadOnlyCollection<BrowserConnection> browserConnections)
    {
        if (checkedAddress is null) return BrowserPathMatch.Unknown;
        var target = Normalize(checkedAddress);
        var onPort = browserConnections.Where(c => c.RemotePort == port).ToList();
        if (onPort.Any(c => Normalize(c.Remote).Equals(target))) return BrowserPathMatch.Same;
        if (egress == EgressKind.Tunnel && onPort.Count > 0) return BrowserPathMatch.Different;
        return BrowserPathMatch.Unknown;
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
