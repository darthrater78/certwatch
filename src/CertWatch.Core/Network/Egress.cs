using System.Net;
using System.Net.Sockets;

namespace CertWatch.Core.Network;

/// <summary>Raw facts about how traffic to one destination leaves the machine, from the OS routing table.</summary>
public sealed record RouteFacts(
    IPAddress Destination,
    int InterfaceIndex,
    /// <summary>Friendly name (the connection name shown in Windows' network settings).</summary>
    string InterfaceName,
    /// <summary>Adapter description (the driver's name for the device).</summary>
    string InterfaceDescription,
    string InterfaceType,
    IPAddress RoutePrefix,
    int PrefixLength,
    /// <summary>Null when the route is on-link (no gateway).</summary>
    IPAddress? NextHop,
    /// <summary>Whether the interface has any default gateway configured.</summary>
    bool InterfaceHasGateway);

public enum EgressKind { Tunnel, Direct, Unknown }

public sealed record EgressInfo(RouteFacts Facts, EgressKind Kind, string Reason)
{
    /// <summary>The adapter's friendly name as Windows shows it (e.g. "Wi-Fi", "Island").</summary>
    public string InterfaceLabel => Facts.InterfaceName;

    public string RouteLabel => $"{Facts.RoutePrefix}/{Facts.PrefixLength} → " +
        (Facts.NextHop is null ? "no gateway (on-link)" : $"via {Facts.NextHop}");
}

/// <summary>Looks up the route the OS would use to reach a destination. Platform-specific.</summary>
public interface IRouteLookup
{
    RouteFacts? Lookup(IPAddress destination);
}

/// <summary>
/// Decides whether traffic to a site egresses through a ZTNA/VPN tunnel or directly. ZTNA clients
/// typically install a host route (/32 or /128) per destination on a virtual adapter with no gateway;
/// direct traffic follows the default route via the LAN gateway.
/// </summary>
public static class EgressClassifier
{
    private static readonly (string Pattern, string Product)[] TunnelAdapters =
    [
        ("Island", "Island"),
        ("Zscaler", "Zscaler"),
        ("Netskope", "Netskope"),
        ("Cloudflare", "Cloudflare WARP"),
        ("WARP", "Cloudflare WARP"),
        ("PANGP", "Palo Alto GlobalProtect"),
        ("GlobalProtect", "Palo Alto GlobalProtect"),
        ("AnyConnect", "Cisco Secure Client"),
        ("Cisco Secure Client", "Cisco Secure Client"),
        ("Fortinet", "FortiClient"),
        ("FortiClient", "FortiClient"),
        ("Check Point", "Check Point"),
        ("Forcepoint", "Forcepoint"),
        ("Cato", "Cato Networks"),
        ("Twingate", "Twingate"),
        ("Tailscale", "Tailscale"),
        ("WireGuard", "WireGuard"),
        ("Wintun", "Wintun tunnel"),
        ("TAP-Windows", "OpenVPN TAP"),
        ("OpenVPN", "OpenVPN"),
    ];

    public static EgressInfo Classify(RouteFacts f)
    {
        var hostRoute = f.PrefixLength == (f.Destination.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32);
        var onLink = f.NextHop is null;
        var via = onLink ? "no gateway" : $"via {f.NextHop}";

        var product = TunnelAdapters.FirstOrDefault(t =>
            f.InterfaceDescription.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase) ||
            f.InterfaceName.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase)).Product;
        var named = product is null ? "" : $" ({product})";

        // Classic ZTNA: a host route per destination, on-link, on the virtual adapter.
        if (hostRoute && onLink)
            return new(f, EgressKind.Tunnel, $"Host route /{f.PrefixLength} with no gateway: ZTNA tunnel{named}");

        // ZTNA adapters have no default gateway of their own but carry specific routes into the
        // tunnel, e.g. Island Private Access: 100.64.0.0/10 via 172.16.99.2 on a /32 adapter.
        // (An on-link subnet on a gateway-less adapter is more likely a local virtual network such as
        // Hyper-V or WSL, so that case isn't counted here.)
        if (!f.InterfaceHasGateway && f.PrefixLength > 0 && (!onLink || hostRoute))
            return new(f, EgressKind.Tunnel,
                $"Route {f.RoutePrefix}/{f.PrefixLength} {via} on an adapter with no default gateway: ZTNA steering{named}");

        if (product is not null)
            return new(f, EgressKind.Tunnel, $"{product} adapter");

        if (f.InterfaceType is "Tunnel" or "Ppp")
            return new(f, EgressKind.Tunnel, $"{f.InterfaceType} interface");

        if (f.PrefixLength == 0)
            return new(f, EgressKind.Direct, onLink ? "Default route (on-link)" : $"Default route via {f.NextHop}");

        return new(f, EgressKind.Direct, onLink ? $"Local network /{f.PrefixLength}" : $"Route /{f.PrefixLength} via {f.NextHop}");
    }
}
