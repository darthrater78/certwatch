using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CertWatch.Core.Network;

namespace CertWatch.App.Tracking;

/// <summary>
/// Asks Windows which route and interface it would use to reach an address (GetBestRoute2, the
/// same lookup the TCP/IP stack does), then names the interface by its friendly name.
/// </summary>
internal sealed partial class WindowsRouteLookup : IRouteLookup
{
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;
    private const int SockaddrInetSize = 28;
    // MIB_IPFORWARD_ROW2 is 104 bytes; leave headroom.
    private const int RouteRowSize = 128;

    // MIB_IPFORWARD_ROW2 layout (8-byte aligned):
    //   0  NET_LUID InterfaceLuid (8)      8  NET_IFINDEX InterfaceIndex (4)
    //  12  IP_ADDRESS_PREFIX DestinationPrefix: SOCKADDR_INET (28) + UINT8 PrefixLength at 40, padded to 44
    //  44  SOCKADDR_INET NextHop (28)
    private const int OffLuid = 0;
    private const int OffIfIndex = 8;
    private const int OffPrefix = 12;
    private const int OffPrefixLength = 40;
    private const int OffNextHop = 44;
    private const int IfMaxStringSize = 256;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress,
        byte[] destinationAddress, uint addressSortOptions, byte[] bestRoute, byte[] bestSourceAddress);

    // The interface's alias is its friendly name ("Wi-Fi", "Ethernet 3", or whatever the ZTNA client named it).
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint ConvertInterfaceLuidToAlias(ref ulong interfaceLuid, [Out] char[] interfaceAlias, UIntPtr length);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceLuidToGuid(ref ulong interfaceLuid, out Guid interfaceGuid);

    public RouteFacts? Lookup(IPAddress destination)
    {
        var dest = ToSockaddrInet(destination);
        var row = new byte[RouteRowSize];
        var source = new byte[SockaddrInetSize];
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, dest, 0, row, source) != 0) return null;

        var luid = BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(OffLuid));
        var ifIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(OffIfIndex));
        var prefix = FromSockaddrInet(row.AsSpan(OffPrefix, SockaddrInetSize)) ?? IPAddress.Any;
        var prefixLength = row[OffPrefixLength];
        var nextHop = FromSockaddrInet(row.AsSpan(OffNextHop, SockaddrInetSize));
        if (nextHop is not null && (nextHop.Equals(IPAddress.Any) || nextHop.Equals(IPAddress.IPv6Any))) nextHop = null;

        var nic = FindInterface(luid, ifIndex, destination.AddressFamily);
        var friendly = FriendlyName(luid) ?? nic?.Name ?? $"Interface {ifIndex}";
        var description = nic?.Description ?? "";
        var type = nic?.NetworkInterfaceType.ToString() ?? "Unknown";
        var hasGateway = nic is not null && HasGateway(nic);

        return new RouteFacts(destination, ifIndex, friendly, description, type, prefix, prefixLength, nextHop, hasGateway);
    }

    private static string? FriendlyName(ulong luid)
    {
        var buffer = new char[IfMaxStringSize + 1];
        if (ConvertInterfaceLuidToAlias(ref luid, buffer, (UIntPtr)buffer.Length) != 0) return null;
        var name = new string(buffer).TrimEnd('\0');
        var end = name.IndexOf('\0');
        return end >= 0 ? name[..end] : name;
    }

    /// <summary>Matches by adapter GUID (reliable), falling back to interface index.</summary>
    private static NetworkInterface? FindInterface(ulong luid, int index, AddressFamily family)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        if (ConvertInterfaceLuidToGuid(ref luid, out var guid) == 0)
        {
            var byGuid = all.FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == guid);
            if (byGuid is not null) return byGuid;
        }

        foreach (var nic in all)
        {
            try
            {
                var props = nic.GetIPProperties();
                var nicIndex = family == AddressFamily.InterNetworkV6 ? props.GetIPv6Properties()?.Index : props.GetIPv4Properties()?.Index;
                if (nicIndex == index) return nic;
            }
            catch (NetworkInformationException) { }
        }
        return null;
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static byte[] ToSockaddrInet(IPAddress address)
    {
        var buf = new byte[SockaddrInetSize];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf, AF_INET);
            address.GetAddressBytes().CopyTo(buf, 4);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf, AF_INET6);
            address.GetAddressBytes().CopyTo(buf, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), (uint)address.ScopeId);
        }
        return buf;
    }

    private static IPAddress? FromSockaddrInet(ReadOnlySpan<byte> s)
    {
        var family = BinaryPrimitives.ReadUInt16LittleEndian(s);
        return family switch
        {
            AF_INET => new IPAddress(s.Slice(4, 4)),
            AF_INET6 => new IPAddress(s.Slice(8, 16)),
            _ => null,
        };
    }
}
