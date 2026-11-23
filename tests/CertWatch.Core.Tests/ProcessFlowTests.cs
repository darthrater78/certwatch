using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CertWatch.Core.Browsers;
using CertWatch.Core.ProcessFlow;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Tests;

public class ProcessFlowTests
{
    private static readonly IPAddress Local = IPAddress.Parse("10.0.0.5"), Remote = IPAddress.Parse("203.0.113.9");
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private const int LocalPort = 50000;
    private const byte Ack = 0x10, Syn = 0x02, Fin = 0x01;

    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] V8(byte[] b) => Join([(byte)b.Length], b);
    private static byte[] V16(byte[] b) => Join(U16(b.Length), b);
    private static byte[] V24(byte[] b) => Join([(byte)(b.Length >> 16), (byte)(b.Length >> 8), (byte)b.Length], b);
    private static byte[] Ext(int type, byte[] data) => Join(U16(type), V16(data));
    private static byte[] Message(byte type, byte[] body) => Join([type], V24(body));
    private static byte[] Record(byte[] body) => Join([22, 3, 3], V16(body));

    private static byte[] Hello(string host) => Record(Message(1, Join(U16(0x0303), new byte[32], V8([]), V16(U16(0x1301)), V8([0]),
        V16(Ext(0, V16(Join([0], V16(Encoding.ASCII.GetBytes(host)))))))));

    private static byte[] ServerHello13() => Record(Message(2, Join(U16(0x0303), new byte[32], V8([]), U16(0x1301), [0], V16(Ext(43, U16(0x0304))))));

    private static byte[] ServerHello12AndCert(byte[] der) => Record(Join(
        Message(2, Join(U16(0x0303), new byte[32], V8([]), U16(0xC02F), [0], V16([]))),
        Message(11, V24(V24(der)))));

    private static byte[] Packet(IPAddress src, int sport, IPAddress dst, int dport, uint seq, uint ack, byte flags, byte[] payload)
    {
        var p = new byte[40 + payload.Length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[8] = 64;
        p[9] = 6;
        src.GetAddressBytes().CopyTo(p, 12);
        dst.GetAddressBytes().CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), (ushort)dport);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(24), seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(28), ack);
        p[32] = 5 << 4;
        p[33] = flags;
        payload.CopyTo(p, 40);
        return p;
    }

    private static byte[] Out(uint seq, uint ack, byte[] data, byte flags = Ack) => Packet(Local, LocalPort, Remote, 443, seq, ack, flags, data);
    private static byte[] In(uint seq, uint ack, byte[] data, byte flags = Ack) => Packet(Remote, 443, Local, LocalPort, seq, ack, flags, data);

    private static X509Certificate2 Cert(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static ProbeResult Probe(string host, X509Certificate2 cert) => new()
    {
        Endpoint = new SiteEndpoint(host, 443), Timestamp = T0, Chain = [cert], RemoteAddress = Remote,
    };

    private static FlowConnection Conn(string remote = "203.0.113.9", int port = 443, int localPort = LocalPort) =>
        new(Local, localPort, IPAddress.Parse(remote), port, 4120);

    private static FlowKey Key => new(Local, LocalPort, Remote, 443);

    private static TlsHandshakeView View(string host) => new(new ClientHelloInfo(host, [], true, false), null, []);

    [Fact]
    public void Assembles_a_tls13_handshake_for_a_targeted_connection()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var first = assembler.Process(Out(100, 900, Hello("app.example")));
        Assert.Equal("app.example", first!.View.Client!.ServerName);
        Assert.Equal(Key, first.Key);
        var second = assembler.Process(In(900, 200, ServerHello13()));
        Assert.True(second!.View.Server!.IsTls13);
    }

    [Fact]
    public void Connections_of_other_processes_are_never_kept()
    {
        var asked = 0;
        var assembler = new HandshakeAssembler(_ => { asked++; return false; });
        Assert.Null(assembler.Process(Out(100, 900, Hello("other.example"))));
        Assert.Null(assembler.Process(Out(100, 900, Hello("other.example"))));
        Assert.Equal(0, assembler.FlowCount);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_hello_split_and_reordered_across_packets_is_rebuilt()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var hello = Hello("reordered.example");
        Assert.Null(assembler.Process(Out(100, 900, hello[..12]))?.View.Client);
        var update = assembler.Process(Out(100 + 12, 900, hello[12..]));
        Assert.Equal("reordered.example", update!.View.Client!.ServerName);

        var server = ServerHello13();
        Assert.Null(assembler.Process(In(900 + 20, 200, server[20..])));
        Assert.True(assembler.Process(In(900, 200, server[..20]))!.View.Server!.IsTls13);
    }

    [Fact]
    public void Sequence_numbers_that_wrap_are_followed()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var hello = Hello("wrap.example");
        var start = uint.MaxValue - 5;
        assembler.Process(Out(start, 900, hello[..10]));
        var update = assembler.Process(Out(unchecked(start + 10), 900, hello[10..]));
        Assert.Equal("wrap.example", update!.View.Client!.ServerName);
    }

    [Fact]
    public void A_retransmitted_segment_changes_nothing()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var hello = Hello("again.example");
        assembler.Process(Out(100, 900, hello));
        Assert.Null(assembler.Process(Out(100, 900, hello)));
    }

    [Fact]
    public void A_closed_connection_is_forgotten_so_a_reused_port_starts_clean()
    {
        var assembler = new HandshakeAssembler(_ => true);
        assembler.Process(Out(100, 900, Hello("first.example")));
        assembler.Process(In(900, 200, [], Fin | Ack));
        Assert.Equal(0, assembler.FlowCount);
        assembler.Process(Out(5000, 7000, [], Syn));
        Assert.Equal("second.example", assembler.Process(Out(5001, 7001, Hello("second.example")))!.View.Client!.ServerName);
    }

    [Fact]
    public void Flows_are_capped()
    {
        var assembler = new HandshakeAssembler(_ => true);
        for (var port = 1; port <= HandshakeAssembler.MaxFlows + 50; port++)
            assembler.Process(Packet(Local, port, Remote, 443, 1, 1, Ack, Hello("cap.example")));
        Assert.Equal(HandshakeAssembler.MaxFlows, assembler.FlowCount);
    }

    [Fact]
    public void Random_and_corrupted_packets_never_throw()
    {
        var random = new Random(20261123);
        var assembler = new HandshakeAssembler(_ => true);
        var valid = Out(100, 900, Hello("fuzz.example"));
        for (var i = 0; i < 5000; i++)
        {
            var noise = new byte[random.Next(0, 200)];
            random.NextBytes(noise);
            if (noise.Length > 0) noise[0] = 0x45;
            assembler.Process(noise);
            var corrupted = (byte[])valid.Clone();
            for (var flips = 0; flips < 4; flips++) corrupted[random.Next(corrupted.Length)] = (byte)random.Next(256);
            assembler.Process(corrupted);
        }
    }

    [Fact]
    public void Non_tls_ports_never_get_a_handshake()
    {
        var tracker = new ProcessFlowTracker();
        tracker.Update([Conn(port: 1433)], T0);
        Assert.Empty(tracker.TakeNameLookups(4));
        Assert.Empty(tracker.TakeProbes(T0, 4));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void Loopback_and_link_local_addresses_never_get_a_handshake(string remote)
    {
        var tracker = new ProcessFlowTracker();
        tracker.Update([Conn(remote)], T0);
        Assert.Empty(tracker.TakeNameLookups(4));
        Assert.Empty(tracker.TakeProbes(T0, 4));
    }

    [Fact]
    public void A_seen_handshake_on_an_unusual_port_can_be_checked()
    {
        var tracker = new ProcessFlowTracker();
        var conn = Conn(port: 9443);
        tracker.Apply(conn.Key, View("odd.example"), T0);
        tracker.Update([conn], T0);
        var request = Assert.Single(tracker.TakeProbes(T0, 4));
        Assert.Equal(new SiteEndpoint("odd.example", 9443), request.Endpoint);
    }

    [Fact]
    public void An_inferred_name_is_labeled_and_an_observed_name_replaces_the_unnamed_row()
    {
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Update([conn], T0);
        var row = Assert.Single(tracker.TakeNameLookups(4));
        tracker.ApplyInferredName(row, "ptr.example", T0);
        Assert.Equal(HostEvidence.Inferred, row.HostEvidence);

        tracker.Apply(conn.Key, View("real.example"), T0);
        tracker.Update([conn], T0);
        var named = Assert.Single(tracker.Destinations);
        Assert.Equal("real.example", named.Host);
        Assert.Equal(HostEvidence.Observed, named.HostEvidence);
    }

    [Fact]
    public void An_invalid_reverse_dns_name_is_dropped()
    {
        var tracker = new ProcessFlowTracker();
        tracker.Update([Conn()], T0);
        var row = Assert.Single(tracker.TakeNameLookups(4));
        tracker.ApplyInferredName(row, "bad name\r\n.example", T0);
        Assert.Null(row.Host);
        Assert.Equal(Remote.ToString(), Assert.Single(tracker.TakeProbes(T0, 4)).Endpoint.Host);
    }

    [Fact]
    public void Two_hosts_on_one_address_get_their_own_rows()
    {
        var tracker = new ProcessFlowTracker();
        var a = Conn(localPort: 50001);
        var b = Conn(localPort: 50002);
        tracker.Apply(a.Key, View("a.example"), T0);
        tracker.Apply(b.Key, View("b.example"), T0);
        tracker.Update([a, b], T0);
        Assert.Equal(["a.example", "b.example"], tracker.Destinations.Select(d => d.Host!).Order());
    }

    [Fact]
    public void A_probe_is_claimed_once_and_rechecked_after_the_interval()
    {
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Apply(conn.Key, View("app.example"), T0);
        tracker.Update([conn], T0);
        var request = Assert.Single(tracker.TakeProbes(T0, 4));
        Assert.Empty(tracker.TakeProbes(T0, 4));
        using var cert = Cert("app.example");
        tracker.CompleteProbe(request.Destination, Probe("app.example", cert), null, null, T0);
        Assert.Empty(tracker.TakeProbes(T0 + TimeSpan.FromMinutes(4), 4));
        Assert.Single(tracker.TakeProbes(T0 + ProcessFlowTracker.RecheckAfter, 4));
    }

    [Fact]
    public void A_failed_recheck_keeps_the_last_certificate()
    {
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Apply(conn.Key, View("app.example"), T0);
        tracker.Update([conn], T0);
        var row = Assert.Single(tracker.TakeProbes(T0, 4)).Destination;
        using var cert = Cert("app.example");
        tracker.CompleteProbe(row, Probe("app.example", cert), null, null, T0);
        tracker.CompleteProbe(row, new ProbeResult { Endpoint = new SiteEndpoint("app.example", 443), Timestamp = T0, Error = "Timed out" }, null, null, T0);
        Assert.Equal(cert.RawData, row.Probe!.Leaf!.RawData);
    }

    [Fact]
    public void Compares_the_certificate_the_process_saw_with_certwatchs_own()
    {
        using var real = Cert("app.example");
        using var other = Cert("proxy.example");
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Apply(conn.Key, new TlsHandshakeView(new ClientHelloInfo("app.example", [], false, false), new ServerHelloInfo(0x0303, 0xC02F), [real.RawData]), T0);
        tracker.Update([conn], T0);
        var row = Assert.Single(tracker.TakeProbes(T0, 4)).Destination;
        Assert.Equal(CertEvidence.SeenInHandshake, row.CertEvidence);

        tracker.CompleteProbe(row, Probe("app.example", real), null, null, T0);
        Assert.Equal(CertEvidence.SeenInHandshake, row.CertEvidence);
        tracker.CompleteProbe(row, Probe("app.example", other), null, null, T0);
        Assert.Equal(CertEvidence.DifferentFromSeen, row.CertEvidence);
    }

    [Fact]
    public void Tls13_evidence_says_the_certificate_is_certwatchs_own_handshake()
    {
        using var cert = Cert("app.example");
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Apply(conn.Key, new TlsHandshakeView(new ClientHelloInfo("app.example", [], true, false), new ServerHelloInfo(0x0304, 0x1301), []), T0);
        tracker.Update([conn], T0);
        var row = Assert.Single(tracker.TakeProbes(T0, 4)).Destination;
        tracker.CompleteProbe(row, Probe("app.example", cert), null, null, T0);
        Assert.Equal(CertEvidence.OwnHandshake, row.CertEvidence);
        Assert.Contains("TLS 1.3", row.EvidenceText);
    }

    private static byte[] Rec(byte type, int length) => Join([type, 3, 3], V16(new byte[length]));

    // Runs a TLS 1.3 handshake up to the server's reply; returns the client's next sequence number.
    private static uint Start13(HandshakeAssembler assembler)
    {
        var hello = Hello("pin.example");
        assembler.Process(Out(100, 900, hello));
        assembler.Process(In(900, 100 + (uint)hello.Length, ServerHello13()));
        return 100 + (uint)hello.Length;
    }

    [Fact]
    public void Tls13_short_encrypted_record_is_a_rejection()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var seq = Start13(assembler);
        Assert.Equal(HandshakeOutcome.Rejected, assembler.Process(Out(seq, 1000, Rec(23, 19)))!.Outcome);
    }

    [Fact]
    public void Tls13_finished_then_data_is_accepted()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var seq = Start13(assembler);
        var finished = Rec(23, 53);
        Assert.Null(assembler.Process(Out(seq, 1000, finished)));
        var update = assembler.Process(Out(seq + (uint)finished.Length, 1000, Rec(23, 300)));
        Assert.Equal(HandshakeOutcome.Accepted, update!.Outcome);
    }

    [Fact]
    public void Finishing_then_closing_without_data_is_reported()
    {
        var assembler = new HandshakeAssembler(_ => true);
        var seq = Start13(assembler);
        var finished = Rec(23, 53);
        assembler.Process(Out(seq, 1000, finished));
        var update = assembler.Process(Out(seq + (uint)finished.Length, 1000, [], Fin | Ack));
        Assert.Equal(HandshakeOutcome.ClosedAfterHandshake, update!.Outcome);
    }

    [Fact]
    public void Tls12_alert_is_a_rejection_and_change_cipher_spec_then_data_is_accepted()
    {
        using var cert = Cert("pin.example");
        var server = Join(ServerHello12AndCert(cert.RawData));
        var hello = Hello("pin.example");
        var seq = 100 + (uint)hello.Length;

        var rejected = new HandshakeAssembler(_ => true);
        rejected.Process(Out(100, 900, hello));
        rejected.Process(In(900, seq, server));
        Assert.Equal(HandshakeOutcome.Rejected, rejected.Process(Out(seq, 2000, Rec(21, 2)))!.Outcome);

        var accepted = new HandshakeAssembler(_ => true);
        accepted.Process(Out(100, 900, hello));
        accepted.Process(In(900, seq, server));
        var reply = Join(Rec(22, 70), Rec(20, 1), Rec(22, 40), Rec(23, 200));
        Assert.Equal(HandshakeOutcome.Accepted, accepted.Process(Out(seq, 2000, reply))!.Outcome);
    }

    [Fact]
    public void A_server_closing_is_not_counted_against_the_client()
    {
        var assembler = new HandshakeAssembler(_ => true);
        Start13(assembler);
        Assert.Null(assembler.Process(In(2000, 500, [], Fin | Ack)));
    }

    private static (ProcessFlowTracker, FlowDestination) PinRow(CertWatch.Core.Trust.Verdict verdict, System.Net.Security.SslPolicyErrors errors, X509Certificate2 cert)
    {
        var tracker = new ProcessFlowTracker();
        var conn = Conn();
        tracker.Apply(conn.Key, View("pin.example"), T0);
        tracker.Update([conn], T0);
        var row = Assert.Single(tracker.TakeProbes(T0, 4)).Destination;
        var probe = new ProbeResult { Endpoint = new SiteEndpoint("pin.example", 443), Timestamp = T0, Chain = [cert], RemoteAddress = Remote, PolicyErrors = errors };
        tracker.CompleteProbe(row, probe, new CertWatch.Core.Trust.Classification(verdict, "test", null, "CA", "Root", false, false), null, T0);
        return (tracker, row);
    }

    [Fact]
    public void Repeated_refusal_of_a_trusted_inspected_certificate_reads_pinning_likely()
    {
        using var cert = Cert("pin.example");
        var (tracker, row) = PinRow(CertWatch.Core.Trust.Verdict.Inspected, System.Net.Security.SslPolicyErrors.None, cert);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Rejected);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Rejected);
        Assert.Equal(PinningSignal.Rejected, row.Pinning);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.ClosedAfterHandshake);
        Assert.Equal(PinningSignal.Likely, row.Pinning);
        Assert.Contains("own trust store", row.EvidenceText);
    }

    [Fact]
    public void An_accepted_connection_rules_pinning_out()
    {
        using var cert = Cert("pin.example");
        var (tracker, row) = PinRow(CertWatch.Core.Trust.Verdict.Inspected, System.Net.Security.SslPolicyErrors.None, cert);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Rejected);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Rejected);
        tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Accepted);
        Assert.Equal(PinningSignal.None, row.Pinning);
    }

    [Theory]
    [InlineData(CertWatch.Core.Trust.Verdict.NotInspected, System.Net.Security.SslPolicyErrors.None)]
    [InlineData(CertWatch.Core.Trust.Verdict.Inspected, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)]
    public void Refusing_an_uninspected_or_untrusted_certificate_is_not_called_pinning(CertWatch.Core.Trust.Verdict verdict, System.Net.Security.SslPolicyErrors errors)
    {
        using var cert = Cert("pin.example");
        var (tracker, row) = PinRow(verdict, errors, cert);
        for (var i = 0; i < ProcessFlowTracker.PinningRefusals + 2; i++) tracker.Apply(Key, View("pin.example"), T0, HandshakeOutcome.Rejected);
        Assert.Equal(PinningSignal.Rejected, row.Pinning);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void An_idle_spare_connection_closed_later_is_not_a_refusal()
    {
        var clock = new ManualClock();
        var assembler = new HandshakeAssembler(_ => true, clock);
        var seq = Start13(assembler);
        var finished = Rec(23, 53);
        assembler.Process(Out(seq, 1000, finished));
        clock.Now += HandshakeAssembler.QuickClose + TimeSpan.FromSeconds(1);
        Assert.Null(assembler.Process(Out(seq + (uint)finished.Length, 1000, [], Fin | Ack)));
    }

    [Fact]
    public void Destinations_are_capped_and_closed_rows_go_first()
    {
        var tracker = new ProcessFlowTracker();
        tracker.Update([Conn("198.51.100.1")], T0);
        tracker.Update([], T0);
        var open = Enumerable.Range(0, ProcessFlowTracker.MaxDestinations + 10)
            .Select(i => Conn($"203.0.{i / 250}.{i % 250 + 1}")).ToList();
        tracker.Update(open, T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(ProcessFlowTracker.MaxDestinations, tracker.Destinations.Count);
        Assert.DoesNotContain(tracker.Destinations, d => d.Remote.Equals(IPAddress.Parse("198.51.100.1")));
    }
}
