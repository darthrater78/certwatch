using System.Buffers.Binary;
using System.Net;

namespace CertWatch.Core.ProcessFlow;

/// <summary>One TCP connection, from the local (client) side.</summary>
public readonly record struct FlowKey(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort);

/// <summary>A connection's handshake view changed.</summary>
public sealed record HandshakeUpdate(FlowKey Key, TlsHandshakeView View, HandshakeOutcome Outcome = HandshakeOutcome.Unknown);

/// <summary>
/// Rebuilds the start of TLS connections from raw IPv4 packets. A connection is followed only if its
/// first ClientHello passes <c>isTarget</c>; bytes of any other connection are dropped at once. Followed
/// connections are kept only until the cleartext handshake ends, then their bytes are discarded.
/// Input is untrusted: every length is checked, and flows, buffers and out-of-order data are capped.
/// </summary>
public sealed class HandshakeAssembler(Func<FlowKey, bool> isTarget, TimeProvider? clock = null)
{
    /// <summary>
    /// Finishing and then closing unused counts as a refusal only this soon after the server's reply.
    /// Later it's an idle connection: browsers open spare ones ahead of time and drop them unused.
    /// </summary>
    public static readonly TimeSpan QuickClose = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const int MaxFlows = 512;
    private const int MaxIgnored = 4096, MaxPendingSegments = 16, MaxAwaitBytes = 16 * 1024;
    private const byte FlagFin = 0x01, FlagSyn = 0x02, FlagRst = 0x04;
    private const int ProtocolTcp = 6;

    private readonly Dictionary<PacketFlow, Flow> _flows = [];
    private readonly HashSet<PacketFlow> _ignored = [];

    /// <summary>Connections being followed or remembered as finished (for tests and limits).</summary>
    public int FlowCount => _flows.Count;

    /// <summary>Feeds one IPv4 packet. Returns an update when a followed handshake shows something new.</summary>
    public HandshakeUpdate? Process(ReadOnlySpan<byte> packet)
    {
        if (!TryDecode(packet, out var forward, out var seq, out var ack, out var flags, out var payload)) return null;
        var reverse = new PacketFlow(forward.DestIp, forward.DestPort, forward.SourceIp, forward.SourcePort);

        if ((flags & FlagSyn) != 0)
        {
            Forget(forward, reverse);
            return null;
        }

        HandshakeUpdate? update = null;
        if (!payload.IsEmpty) update = Accept(forward, reverse, seq, ack, payload);
        if ((flags & (FlagFin | FlagRst)) != 0)
        {
            update = ClientClosed(forward) ?? update;
            Forget(forward, reverse);
        }
        return update;
    }

    // Only the client closing says anything about its view of the certificate; a server closing doesn't.
    private HandshakeUpdate? ClientClosed(PacketFlow sender)
    {
        if (!_flows.TryGetValue(sender, out var flow) || !flow.Awaiting) return null;
        var (_, finished) = TlsHandshakeParser.ReadClientReaction(flow.Client.Bytes, flow.IsTls13);
        var outcome = Judge(flow, finished ? HandshakeOutcome.ClosedAfterHandshake : HandshakeOutcome.Rejected);
        var view = flow.LastView!;
        flow.Finish();
        return outcome == HandshakeOutcome.Unknown ? null : new HandshakeUpdate(sender.ToKey(), view, outcome);
    }

    private HandshakeOutcome Judge(Flow flow, HandshakeOutcome outcome) =>
        outcome == HandshakeOutcome.ClosedAfterHandshake && _clock.GetUtcNow() - flow.AwaitingSince > QuickClose
            ? HandshakeOutcome.Unknown
            : outcome;

    private HandshakeUpdate? Accept(PacketFlow forward, PacketFlow reverse, uint seq, uint ack, ReadOnlySpan<byte> payload)
    {
        if (_flows.TryGetValue(forward, out var flow)) return flow.Done ? null : Advance(forward, flow, flow.Client, seq, payload);
        if (_flows.TryGetValue(reverse, out flow)) return flow.Done || flow.Awaiting ? null : Advance(reverse, flow, flow.Server, seq, payload);
        if (_ignored.Contains(forward) || !StartsClientHello(payload)) return null;

        if (!isTarget(forward.ToKey()))
        {
            if (_ignored.Count >= MaxIgnored) _ignored.Clear();
            _ignored.Add(forward);
            return null;
        }
        if (_flows.Count >= MaxFlows && !EvictFinished()) return null;
        // The hello's acknowledgement number is where the server's first byte will be.
        flow = new Flow(seq, ack);
        _flows[forward] = flow;
        return Advance(forward, flow, flow.Client, seq, payload);
    }

    private HandshakeUpdate? Advance(PacketFlow id, Flow flow, Direction direction, uint seq, ReadOnlySpan<byte> payload)
    {
        if (!direction.Add(seq, payload)) return null;
        if (flow.Awaiting) return ResolveReaction(id, flow);

        var client = flow.Client.Bytes;
        var server = flow.Server.Bytes;
        var view = TlsHandshakeParser.Parse(client, server);
        flow.LastView = view;

        var state = (view.Client is not null, view.Server is not null, view.ServerCertificates.Count);
        var changed = state != flow.LastState;
        flow.LastState = state;

        var ended = view.Server is { IsTls13: true } || view.CertificateVisible || TlsHandshakeParser.HandshakeEnded(server);
        if (client.Length >= TlsHandshakeParser.MaxBytes || (ended && view.Server is null)) flow.Finish();
        else if (ended)
        {
            flow.AwaitReaction(view.Server!.IsTls13, _clock.GetUtcNow());
            if (ResolveReaction(id, flow) is { } resolved) return resolved;
        }
        return changed ? new HandshakeUpdate(id.ToKey(), view) : null;
    }

    // After the server's flight only the client's next records are kept, until they show its decision.
    private HandshakeUpdate? ResolveReaction(PacketFlow id, Flow flow)
    {
        var (read, _) = TlsHandshakeParser.ReadClientReaction(flow.Client.Bytes, flow.IsTls13);
        if (read == HandshakeOutcome.Unknown)
        {
            if (flow.Client.Bytes.Length >= MaxAwaitBytes) flow.Finish();
            return null;
        }
        var outcome = Judge(flow, read);
        var view = flow.LastView!;
        flow.Finish();
        return outcome == HandshakeOutcome.Unknown ? null : new HandshakeUpdate(id.ToKey(), view, outcome);
    }

    private bool EvictFinished()
    {
        var evictable = _flows.Where(f => f.Value.Done).Select(f => f.Key).ToList();
        if (evictable.Count == 0) evictable = _flows.Where(f => f.Value.Awaiting).Select(f => f.Key).ToList();
        foreach (var key in evictable) _flows.Remove(key);
        return evictable.Count > 0;
    }

    private void Forget(PacketFlow forward, PacketFlow reverse)
    {
        _flows.Remove(forward);
        _flows.Remove(reverse);
        _ignored.Remove(forward);
        _ignored.Remove(reverse);
    }

    private static bool StartsClientHello(ReadOnlySpan<byte> p) => p.Length >= 6 && p[0] == 22 && p[1] == 3 && p[5] == 1;

    private static bool TryDecode(ReadOnlySpan<byte> packet, out PacketFlow flow, out uint seq, out uint ack, out byte flags, out ReadOnlySpan<byte> payload)
    {
        flow = default;
        seq = ack = 0;
        flags = 0;
        payload = default;
        if (packet.Length < 20 || packet[0] >> 4 != 4 || packet[9] != ProtocolTcp) return false;

        var headerLength = (packet[0] & 0x0F) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        // Fragments can't be read as TCP: more-fragments set, or a non-zero offset.
        if ((BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) & 0x3FFF) != 0) return false;
        if (headerLength < 20 || totalLength < headerLength + 20 || totalLength > packet.Length) return false;

        var tcp = packet[headerLength..totalLength];
        var dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcp.Length) return false;

        flow = new PacketFlow(
            BinaryPrimitives.ReadUInt32BigEndian(packet[12..]), BinaryPrimitives.ReadUInt16BigEndian(tcp),
            BinaryPrimitives.ReadUInt32BigEndian(packet[16..]), BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]));
        seq = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
        ack = BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]);
        flags = tcp[13];
        payload = tcp[dataOffset..];
        return true;
    }

    /// <summary>Allocation-free identity for a packet's direction; the first end is the sender.</summary>
    private readonly record struct PacketFlow(uint SourceIp, ushort SourcePort, uint DestIp, ushort DestPort)
    {
        public FlowKey ToKey() => new(ToAddress(SourceIp), SourcePort, ToAddress(DestIp), DestPort);

        private static IPAddress ToAddress(uint ip)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
            return new IPAddress(bytes);
        }
    }

    private sealed class Flow(uint clientSeq, uint serverSeq)
    {
        public Direction Client { get; } = new(clientSeq);
        public Direction Server { get; } = new(serverSeq);
        public (bool, bool, int) LastState { get; set; }
        public bool Done { get; private set; }
        public bool Awaiting { get; private set; }
        public bool IsTls13 { get; private set; }
        public TlsHandshakeView? LastView { get; set; }

        public DateTimeOffset AwaitingSince { get; private set; }

        public void AwaitReaction(bool tls13, DateTimeOffset now)
        {
            Awaiting = true;
            AwaitingSince = now;
            IsTls13 = tls13;
            Server.Clear();
        }

        public void Finish()
        {
            Done = true;
            Awaiting = false;
            Client.Clear();
            Server.Clear();
        }
    }

    private sealed class Direction(uint next)
    {
        private uint _next = next;
        private MemoryStream _data = new();
        private readonly List<(uint Seq, byte[] Data)> _pending = [];

        public ReadOnlySpan<byte> Bytes => _data.GetBuffer().AsSpan(0, (int)_data.Length);

        public void Clear()
        {
            _data = new MemoryStream();
            _pending.Clear();
        }

        /// <summary>Takes a segment; true when the in-order bytes grew.</summary>
        public bool Add(uint seq, ReadOnlySpan<byte> payload)
        {
            var gap = (int)(seq - _next);
            if (gap > 0)
            {
                if (gap < TlsHandshakeParser.MaxBytes && _pending.Count < MaxPendingSegments && _pending.All(p => p.Seq != seq))
                    _pending.Add((seq, payload.ToArray()));
                return false;
            }
            var grew = Append(seq, payload);
            for (var i = FindReady(); i >= 0; i = FindReady())
            {
                var (pendingSeq, data) = _pending[i];
                _pending.RemoveAt(i);
                grew |= Append(pendingSeq, data);
            }
            return grew;
        }

        private int FindReady() => _pending.FindIndex(p => (int)(p.Seq - _next) <= 0);

        // Retransmitted bytes are skipped; only what's new is kept, up to the cap.
        private bool Append(uint seq, ReadOnlySpan<byte> payload)
        {
            var overlap = (int)(_next - seq);
            if (overlap < 0 || overlap >= payload.Length) return false;
            var fresh = payload[overlap..];
            _next += (uint)fresh.Length;
            var room = TlsHandshakeParser.MaxBytes + 1 - (int)_data.Length;
            if (room <= 0) return false;
            _data.Write(fresh[..Math.Min(room, fresh.Length)]);
            return true;
        }
    }
}
