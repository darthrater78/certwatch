using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using CertWatch.Core.Network;

namespace CertWatch.App.Tracking;

/// <summary>
/// Reads Windows' TCP connection table (GetExtendedTcpTable, the data behind netstat -ano) to see
/// which remote addresses the browser's processes are connected to. Read-only: it lists
/// connections, never opens, closes or touches one, and needs no elevation.
/// </summary>
internal static partial class TcpConnections
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (6 x uint).
    private const int Row4Size = 24;
    // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid.
    private const int Row6Size = 56;
    private const int MaxTableBytes = 16 * 1024 * 1024;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(byte[]? table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    /// <summary>Established connections owned by any of <paramref name="processIds"/>, IPv4 and IPv6.</summary>
    public static List<BrowserConnection> EstablishedFor(IReadOnlySet<int> processIds)
    {
        var result = new List<BrowserConnection>();
        if (processIds.Count == 0) return result;
        if (Read(AF_INET) is { } v4) Parse(v4, Row4Size, ParseRow4, processIds, result);
        if (Read(AF_INET6) is { } v6) Parse(v6, Row6Size, ParseRow6, processIds, result);
        return result;
    }

    private static byte[]? Read(int family)
    {
        var size = 0;
        // The table can grow between the size query and the read; retry a few times.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? null : new byte[size];
            var rc = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            if (rc == 0 && buffer is not null) return buffer;
            if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0 || size > MaxTableBytes) return null;
            size += 4096;
        }
        return null;
    }

    private delegate (uint State, IPAddress Remote, int Port, int Pid) RowParser(ReadOnlySpan<byte> row);

    private static void Parse(byte[] table, int rowSize, RowParser parse, IReadOnlySet<int> pids, List<BrowserConnection> into)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        // Rows start after the 4-byte count; never read past what Windows returned.
        var max = (table.Length - 4) / rowSize;
        for (var i = 0; i < Math.Min(count, (uint)max); i++)
        {
            var (state, remote, port, pid) = parse(table.AsSpan(4 + i * rowSize, rowSize));
            if (state == MIB_TCP_STATE_ESTAB && pids.Contains(pid)) into.Add(new BrowserConnection(remote, port, pid));
        }
    }

    private static (uint, IPAddress, int, int) ParseRow4(ReadOnlySpan<byte> r) => (
        BinaryPrimitives.ReadUInt32LittleEndian(r),
        new IPAddress(r.Slice(12, 4)),
        Port(r.Slice(16, 4)),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(20, 4)));

    private static (uint, IPAddress, int, int) ParseRow6(ReadOnlySpan<byte> r) => (
        BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(48, 4)),
        new IPAddress(r.Slice(24, 16), BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(40, 4))),
        Port(r.Slice(44, 4)),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(52, 4)));

    // Ports are in network byte order in the low 16 bits of a DWORD.
    private static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];
}
