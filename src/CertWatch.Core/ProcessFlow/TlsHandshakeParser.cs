using System.Buffers.Binary;
using System.Text;

namespace CertWatch.Core.ProcessFlow;

/// <summary>What a client's ClientHello said, in the clear.</summary>
/// <param name="ServerName">The SNI host name, or null when absent or not a valid DNS name.</param>
/// <param name="Alpn">Application protocols offered (h2, http/1.1, ...).</param>
/// <param name="OffersTls13">The client listed TLS 1.3 in supported_versions.</param>
/// <param name="HasEncryptedClientHello">
/// The hello carries Encrypted Client Hello, so <paramref name="ServerName"/> is the outer (front) name,
/// not necessarily the site the process is really reaching.
/// </param>
public sealed record ClientHelloInfo(string? ServerName, IReadOnlyList<string> Alpn, bool OffersTls13, bool HasEncryptedClientHello);

/// <summary>The version and cipher suite the server chose.</summary>
public sealed record ServerHelloInfo(ushort Version, ushort CipherSuite)
{
    public bool IsTls13 => Version == TlsHandshakeParser.Tls13;
}

/// <summary>
/// What can be read from one connection's handshake without any keys. Under TLS 1.3 the server's
/// certificate is encrypted, so <see cref="ServerCertificates"/> stays empty and
/// <see cref="CertificateVisible"/> is false.
/// </summary>
public sealed record TlsHandshakeView(ClientHelloInfo? Client, ServerHelloInfo? Server, IReadOnlyList<byte[]> ServerCertificates)
{
    public bool CertificateVisible => ServerCertificates.Count > 0;
}

/// <summary>How a client responded to the server's certificate.</summary>
public enum HandshakeOutcome
{
    Unknown,
    /// <summary>The client finished the handshake and sent application data.</summary>
    Accepted,
    /// <summary>The client sent an alert or closed before finishing the handshake.</summary>
    Rejected,
    /// <summary>The client finished, then closed without sending data, as an application-level pin check does.</summary>
    ClosedAfterHandshake,
}

/// <summary>
/// Reads the cleartext part of a TLS handshake from each direction's bytes. Input is untrusted network
/// data: every length is bounds-checked, totals are capped, and malformed input yields a partial or
/// empty view, never an exception. It holds no keys and decrypts nothing.
/// </summary>
public static class TlsHandshakeParser
{
    public const ushort Tls12 = 0x0303, Tls13 = 0x0304;

    /// <summary>At most this much of each direction is examined.</summary>
    public const int MaxBytes = 64 * 1024;
    /// <summary>At most this many certificates are kept from a chain.</summary>
    public const int MaxCertificates = 10;

    private const byte RecordChangeCipherSpec = 20, RecordAlert = 21, RecordHandshake = 22, RecordApplicationData = 23;
    // An encrypted alert is 19 bytes (2 + content type + 16-byte tag); the smallest Finished is 49.
    private const int MaxEncryptedAlert = 24;
    private const int RecordHeader = 5, MaxRecordLength = (1 << 14) + 256, HandshakeHeader = 4;
    private const byte MsgClientHello = 1, MsgServerHello = 2, MsgCertificate = 11, MsgServerHelloDone = 14;
    private const ushort ExtServerName = 0, ExtAlpn = 16, ExtSupportedVersions = 43, ExtEncryptedClientHello = 0xFE0D;
    private const int MaxHostLength = 253, MaxAlpnEntries = 16;

    public static TlsHandshakeView Parse(ReadOnlySpan<byte> clientToServer, ReadOnlySpan<byte> serverToClient)
    {
        var client = ParseClient(CollectHandshakes(clientToServer));
        var (server, certificates) = ParseServer(CollectHandshakes(serverToClient));
        return new TlsHandshakeView(client, server, certificates);
    }

    /// <summary>A plain, lower-case DNS name, or null when the text isn't one.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > MaxHostLength || !Ascii.IsValid(host)) return null;
        return ValidHost(Encoding.ASCII.GetBytes(host));
    }

    /// <summary>
    /// What the client did after the server's certificate, judged from record types and sizes only
    /// (nothing is decrypted). Under TLS 1.3 an encrypted alert is told from Finished by its length.
    /// </summary>
    public static (HandshakeOutcome Outcome, bool FinishedSent) ReadClientReaction(ReadOnlySpan<byte> client, bool tls13)
    {
        var finished = false;
        while (client.Length >= RecordHeader)
        {
            var type = client[0];
            var length = BinaryPrimitives.ReadUInt16BigEndian(client[3..]);
            if (client[1] != 3 || length == 0 || length > MaxRecordLength) return (HandshakeOutcome.Unknown, finished);
            if (client.Length < RecordHeader + length) break;
            switch (type)
            {
                case RecordAlert:
                    return (finished ? HandshakeOutcome.ClosedAfterHandshake : HandshakeOutcome.Rejected, finished);
                case RecordChangeCipherSpec:
                    if (!tls13) finished = true;
                    break;
                case RecordHandshake:
                    break;
                case RecordApplicationData when !tls13:
                    return (finished ? HandshakeOutcome.Accepted : HandshakeOutcome.Unknown, finished);
                case RecordApplicationData when !finished:
                    if (length <= MaxEncryptedAlert) return (HandshakeOutcome.Rejected, false);
                    finished = true;
                    break;
                case RecordApplicationData:
                    return (length <= MaxEncryptedAlert ? HandshakeOutcome.ClosedAfterHandshake : HandshakeOutcome.Accepted, true);
                default:
                    return (HandshakeOutcome.Unknown, finished);
            }
            client = client[(RecordHeader + length)..];
        }
        return (HandshakeOutcome.Unknown, finished);
    }

    /// <summary>Whether the cleartext handshake is over: a complete run of records reached a non-handshake record.</summary>
    public static bool HandshakeEnded(ReadOnlySpan<byte> stream)
    {
        if (stream.Length > MaxBytes) return true;
        while (stream.Length >= RecordHeader)
        {
            if (stream[0] != RecordHandshake) return true;
            var length = BinaryPrimitives.ReadUInt16BigEndian(stream[3..]);
            if (stream[1] != 3 || length == 0 || length > MaxRecordLength) return true;
            if (stream.Length < RecordHeader + length) return false;
            stream = stream[(RecordHeader + length)..];
        }
        return false;
    }

    /// <summary>
    /// Joins the leading handshake records (a message may span several). Stops at the first other
    /// record type: from ChangeCipherSpec on, what follows is encrypted, even if labelled handshake.
    /// </summary>
    private static byte[] CollectHandshakes(ReadOnlySpan<byte> stream)
    {
        if (stream.Length > MaxBytes) stream = stream[..MaxBytes];
        using var joined = new MemoryStream();
        while (stream.Length >= RecordHeader && stream[0] == RecordHandshake && stream[1] == 3)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(stream[3..]);
            if (length == 0 || length > MaxRecordLength) break;
            // A record cut off by the capture still contributes what arrived.
            var available = Math.Min(length, stream.Length - RecordHeader);
            joined.Write(stream.Slice(RecordHeader, available));
            if (available < length) break;
            stream = stream[(RecordHeader + length)..];
        }
        return joined.ToArray();
    }

    private static bool NextMessage(ref ReadOnlySpan<byte> messages, out byte type, out ReadOnlySpan<byte> body)
    {
        type = 0;
        body = default;
        if (messages.Length < HandshakeHeader) return false;
        var length = (messages[1] << 16) | (messages[2] << 8) | messages[3];
        if (length > messages.Length - HandshakeHeader) return false;
        type = messages[0];
        body = messages.Slice(HandshakeHeader, length);
        messages = messages[(HandshakeHeader + length)..];
        return true;
    }

    private static ClientHelloInfo? ParseClient(ReadOnlySpan<byte> messages)
    {
        while (NextMessage(ref messages, out var type, out var body))
        {
            if (type == MsgClientHello) return ParseClientHello(body);
        }
        return null;
    }

    private static ClientHelloInfo? ParseClientHello(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        if (!r.Skip(2 + 32) || !r.Vector8(out _) || !r.Vector16(out _) || !r.Vector8(out _)) return null;

        string? serverName = null;
        var alpn = new List<string>();
        var tls13 = false;
        var ech = false;
        // A hello with no extensions is legal.
        if (r.Remaining > 0 && r.Vector16(out var extensions))
        {
            var e = new Reader(extensions);
            while (e.U16(out var extType) && e.Vector16(out var data))
            {
                switch (extType)
                {
                    case ExtServerName: serverName ??= ReadServerName(data); break;
                    case ExtAlpn: ReadAlpn(data, alpn); break;
                    case ExtSupportedVersions: tls13 |= ListsTls13(data); break;
                    case ExtEncryptedClientHello: ech = true; break;
                }
            }
        }
        return new ClientHelloInfo(serverName, alpn, tls13, ech);
    }

    private static string? ReadServerName(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        if (!r.Vector16(out var list)) return null;
        var names = new Reader(list);
        while (names.U8(out var nameType) && names.Vector16(out var name))
        {
            if (nameType == 0) return ValidHost(name);
        }
        return null;
    }

    /// <summary>Only a plain DNS name is accepted: this text reaches the UI, the log and a later handshake.</summary>
    private static string? ValidHost(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty || name.Length > MaxHostLength) return null;
        foreach (var b in name)
        {
            var ok = b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_';
            if (!ok) return null;
        }
        if (name[0] == (byte)'.' || name[^1] == (byte)'.' || name.IndexOf(".."u8) >= 0) return null;
        return Encoding.ASCII.GetString(name).ToLowerInvariant();
    }

    private static void ReadAlpn(ReadOnlySpan<byte> data, List<string> into)
    {
        var r = new Reader(data);
        if (!r.Vector16(out var list)) return;
        var protocols = new Reader(list);
        while (into.Count < MaxAlpnEntries && protocols.Vector8(out var protocol))
        {
            if (!protocol.IsEmpty && IsPrintableAscii(protocol)) into.Add(Encoding.ASCII.GetString(protocol));
        }
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> text)
    {
        foreach (var b in text)
        {
            if (b is < 0x21 or > 0x7E) return false;
        }
        return true;
    }

    private static bool ListsTls13(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        if (!r.Vector8(out var list)) return false;
        var versions = new Reader(list);
        while (versions.U16(out var version))
        {
            if (version == Tls13) return true;
        }
        return false;
    }

    private static (ServerHelloInfo?, IReadOnlyList<byte[]>) ParseServer(ReadOnlySpan<byte> messages)
    {
        ServerHelloInfo? server = null;
        IReadOnlyList<byte[]> certificates = [];
        while (NextMessage(ref messages, out var type, out var body))
        {
            if (type == MsgServerHello && server is null)
            {
                // A HelloRetryRequest has this same shape and already fixes the version and cipher suite.
                server = ParseServerHello(body);
                if (server is null or { IsTls13: true }) break;
            }
            else if (type == MsgCertificate && server is not null)
            {
                certificates = ParseCertificates(body);
                break;
            }
            else if (type == MsgServerHelloDone)
            {
                break;
            }
        }
        return (server, certificates);
    }

    private static ServerHelloInfo? ParseServerHello(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        if (!r.U16(out var version) || !r.Skip(32) || !r.Vector8(out _) || !r.U16(out var cipher) || !r.Skip(1)) return null;
        if (r.Remaining > 0 && r.Vector16(out var extensions))
        {
            var e = new Reader(extensions);
            while (e.U16(out var extType) && e.Vector16(out var data))
            {
                if (extType == ExtSupportedVersions && new Reader(data).U16(out var selected)) version = selected;
            }
        }
        return new ServerHelloInfo(version, cipher);
    }

    private static IReadOnlyList<byte[]> ParseCertificates(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        if (!r.Vector24(out var list)) return [];
        var entries = new Reader(list);
        var result = new List<byte[]>();
        while (result.Count < MaxCertificates && entries.Vector24(out var der))
        {
            if (!der.IsEmpty) result.Add(der.ToArray());
        }
        return result;
    }

    /// <summary>A forward-only reader whose every read fails, rather than throws, when data runs out.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public readonly int Remaining => _data.Length;

        public bool Skip(int count)
        {
            if (count > _data.Length) return false;
            _data = _data[count..];
            return true;
        }

        public bool U8(out byte value)
        {
            value = 0;
            if (_data.Length < 1) return false;
            value = _data[0];
            _data = _data[1..];
            return true;
        }

        public bool U16(out ushort value)
        {
            value = 0;
            if (_data.Length < 2) return false;
            value = BinaryPrimitives.ReadUInt16BigEndian(_data);
            _data = _data[2..];
            return true;
        }

        public bool Vector8(out ReadOnlySpan<byte> value)
        {
            value = default;
            return U8(out var length) && Take(length, out value);
        }

        public bool Vector16(out ReadOnlySpan<byte> value)
        {
            value = default;
            return U16(out var length) && Take(length, out value);
        }

        public bool Vector24(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (_data.Length < 3) return false;
            var length = (_data[0] << 16) | (_data[1] << 8) | _data[2];
            _data = _data[3..];
            return Take(length, out value);
        }

        private bool Take(int length, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (length > _data.Length) return false;
            value = _data[..length];
            _data = _data[length..];
            return true;
        }
    }
}
