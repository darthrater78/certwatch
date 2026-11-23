using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CertWatch.Core.ProcessFlow;

namespace CertWatch.Core.Tests;

public class TlsHandshakeParserTests
{
    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] U24(int v) => [(byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] V8(byte[] b) => Join([(byte)b.Length], b);
    private static byte[] V16(byte[] b) => Join(U16(b.Length), b);
    private static byte[] V24(byte[] b) => Join(U24(b.Length), b);
    private static byte[] Ext(int type, byte[] data) => Join(U16(type), V16(data));
    private static byte[] Message(byte type, byte[] body) => Join([type], V24(body));
    private static byte[] Record(byte type, byte[] body) => Join([type, 3, 3], V16(body));

    private static byte[] Sni(string host) => Ext(0, V16(Join([0], V16(Encoding.ASCII.GetBytes(host)))));
    private static byte[] Alpn(params string[] names) => Ext(16, V16(Join(names.Select(n => V8(Encoding.ASCII.GetBytes(n))).ToArray())));
    private static byte[] OffersTls13() => Ext(43, V8(Join(U16(0x0304), U16(0x0303))));

    private static byte[] ClientHello(params byte[][] extensions) => Message(1, Join(
        U16(0x0303), new byte[32], V8(new byte[32]), V16(U16(0x1301)), V8([0]), V16(Join(extensions))));

    private static byte[] ServerHello(int cipher, params byte[][] extensions) => Message(2, Join(
        U16(0x0303), new byte[32], V8([]), U16(cipher), [0], V16(Join(extensions))));

    private static byte[] CertificateMessage(params byte[][] der) => Message(11, V24(Join(der.Select(V24).ToArray())));

    private static byte[] SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=flow.example", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return cert.RawData;
    }

    [Fact]
    public void Reads_the_host_alpn_and_tls13_offer_from_a_client_hello()
    {
        var client = Record(22, ClientHello(Sni("Portal.Example.COM"), Alpn("h2", "http/1.1"), OffersTls13()));
        var view = TlsHandshakeParser.Parse(client, []);
        Assert.Equal("portal.example.com", view.Client!.ServerName);
        Assert.Equal(["h2", "http/1.1"], view.Client.Alpn);
        Assert.True(view.Client.OffersTls13);
        Assert.False(view.Client.HasEncryptedClientHello);
    }

    [Fact]
    public void Flags_encrypted_client_hello_because_the_visible_name_is_only_the_front()
    {
        var client = Record(22, ClientHello(Sni("front.example"), Ext(0xFE0D, [1, 2, 3])));
        var view = TlsHandshakeParser.Parse(client, []);
        Assert.Equal("front.example", view.Client!.ServerName);
        Assert.True(view.Client.HasEncryptedClientHello);
    }

    [Theory]
    [InlineData("bad host.example")]
    [InlineData("evil.example\r\nInjected: 1")]
    [InlineData(".leading.example")]
    [InlineData("double..dot.example")]
    public void Rejects_a_server_name_that_is_not_a_plain_dns_name(string host)
    {
        var view = TlsHandshakeParser.Parse(Record(22, ClientHello(Sni(host))), []);
        Assert.NotNull(view.Client);
        Assert.Null(view.Client!.ServerName);
    }

    [Fact]
    public void Reads_a_client_hello_split_across_records()
    {
        var hello = ClientHello(Sni("split.example"));
        var client = Join(Record(22, hello[..20]), Record(22, hello[20..]));
        Assert.Equal("split.example", TlsHandshakeParser.Parse(client, []).Client!.ServerName);
    }

    [Fact]
    public void Tls12_exposes_the_certificate_the_server_actually_sent()
    {
        var der = SelfSigned();
        var server = Record(22, Join(ServerHello(0xC02F), CertificateMessage(der), Message(14, [])));
        var view = TlsHandshakeParser.Parse([], server);
        Assert.Equal(TlsHandshakeParser.Tls12, view.Server!.Version);
        Assert.True(view.CertificateVisible);
        Assert.Equal(der, view.ServerCertificates[0]);
        using var parsed = X509CertificateLoader.LoadCertificate(view.ServerCertificates[0]);
        Assert.Equal("CN=flow.example", parsed.Subject);
    }

    [Fact]
    public void Tls13_reports_the_version_and_no_certificate()
    {
        var server = Join(
            Record(22, ServerHello(0x1301, Ext(43, U16(0x0304)))),
            Record(20, [1]),
            Record(23, RandomNumberGenerator.GetBytes(200)));
        var view = TlsHandshakeParser.Parse([], server);
        Assert.True(view.Server!.IsTls13);
        Assert.Equal(0x1301, view.Server.CipherSuite);
        Assert.False(view.CertificateVisible);
    }

    [Fact]
    public void Encrypted_records_after_change_cipher_spec_are_never_read_as_handshake()
    {
        var fake = CertificateMessage(SelfSigned());
        var server = Join(Record(22, ServerHello(0xC02F)), Record(20, [1]), Record(22, fake));
        Assert.False(TlsHandshakeParser.Parse([], server).CertificateVisible);
    }

    [Fact]
    public void Keeps_at_most_the_certificate_cap()
    {
        var many = Enumerable.Range(0, TlsHandshakeParser.MaxCertificates + 5).Select(_ => new byte[] { 0x30, 0 }).ToArray();
        var server = Record(22, Join(ServerHello(0xC02F), CertificateMessage(many)));
        Assert.Equal(TlsHandshakeParser.MaxCertificates, TlsHandshakeParser.Parse([], server).ServerCertificates.Count);
    }

    [Fact]
    public void Every_truncation_of_a_handshake_is_safe()
    {
        var client = Record(22, ClientHello(Sni("cut.example"), Alpn("h2"), OffersTls13()));
        var server = Record(22, Join(ServerHello(0xC02F), CertificateMessage(SelfSigned()), Message(14, [])));
        for (var i = 0; i <= client.Length; i++) _ = TlsHandshakeParser.Parse(client[..i], []);
        for (var i = 0; i <= server.Length; i++) _ = TlsHandshakeParser.Parse([], server[..i]);
    }

    [Fact]
    public void Random_and_corrupted_input_never_throws()
    {
        var random = new Random(20261123);
        var valid = Record(22, ClientHello(Sni("fuzz.example"), Alpn("h2"), OffersTls13()));
        for (var i = 0; i < 5000; i++)
        {
            var noise = new byte[random.Next(0, 600)];
            random.NextBytes(noise);
            if (noise.Length > 5) { noise[0] = 22; noise[1] = 3; }
            _ = TlsHandshakeParser.Parse(noise, noise);

            var corrupted = (byte[])valid.Clone();
            for (var flips = 0; flips < 4; flips++) corrupted[random.Next(corrupted.Length)] = (byte)random.Next(256);
            _ = TlsHandshakeParser.Parse(corrupted, corrupted);
        }
    }

    [Fact]
    public void Non_tls_traffic_yields_an_empty_view()
    {
        var http = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
        var view = TlsHandshakeParser.Parse(http, http);
        Assert.Null(view.Client);
        Assert.Null(view.Server);
        Assert.False(view.CertificateVisible);
    }
}
