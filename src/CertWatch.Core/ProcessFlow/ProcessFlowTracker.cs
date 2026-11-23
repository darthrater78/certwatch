using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Browsers;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.Core.ProcessFlow;

/// <summary>An established TCP connection of the targeted process, from the OS connection table.</summary>
public sealed record FlowConnection(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int ProcessId, string? ProcessName = null)
{
    public FlowKey Key => new(Local, LocalPort, Remote, RemotePort);
}

/// <summary>How CertWatch knows the host name on a row.</summary>
public enum HostEvidence
{
    /// <summary>No name is known; a handshake to the address carries no SNI.</summary>
    None,
    /// <summary>A best guess from reverse DNS. Shared addresses can make it wrong.</summary>
    Inferred,
    /// <summary>Read from the process's own ClientHello.</summary>
    Observed,
}

/// <summary>Where the certificate on a row came from.</summary>
public enum CertEvidence
{
    None,
    /// <summary>CertWatch's own handshake to the same address; the process's certificate wasn't visible.</summary>
    OwnHandshake,
    /// <summary>Read from the process's own TLS 1.2 handshake.</summary>
    SeenInHandshake,
    /// <summary>The process received a different certificate from the one CertWatch's own handshake got.</summary>
    DifferentFromSeen,
}

/// <summary>What the process's reaction to the certificate suggests.</summary>
public enum PinningSignal
{
    None,
    /// <summary>The process refused the certificate, but not enough to point at pinning.</summary>
    Rejected,
    /// <summary>It repeatedly refused an inspected certificate this PC trusts, and never accepted one here.</summary>
    Likely,
}

/// <summary>A handshake CertWatch should make for a row.</summary>
public sealed record FlowProbeRequest(FlowDestination Destination, SiteEndpoint Endpoint, IPAddress Address, bool IsHostnameRetry = false);

/// <summary>One place the targeted process connects to: an address, a port and, when known, a host name.</summary>
public sealed class FlowDestination(IPAddress remote, int port, DateTimeOffset now, string? owner = null)
{
    /// <summary>The app this row belongs to when following all processes, so one app's result can't hide another's.</summary>
    public string? Owner { get; } = owner;
    public IPAddress Remote { get; } = remote;
    public int Port { get; } = port;
    public string? Host { get; internal set; }
    public HostEvidence HostEvidence { get; internal set; }
    public int OpenConnections { get; internal set; }
    public DateTimeOffset FirstSeen { get; } = now;
    public DateTimeOffset LastSeen { get; internal set; } = now;
    public ushort? ObservedVersion { get; internal set; }
    public IReadOnlyList<string> Alpn { get; internal set; } = [];
    public bool EncryptedClientHello { get; internal set; }
    public IReadOnlyList<byte[]> ObservedCertificates { get; internal set; } = [];
    public ProbeResult? Probe { get; internal set; }
    public Classification? Classification { get; internal set; }
    public EgressInfo? Egress { get; internal set; }

    internal bool HandshakeSeen { get; set; }
    internal bool NameRequested { get; set; }
    internal bool NameDone { get; set; }
    internal bool ProbeInFlight { get; set; }
    internal DateTimeOffset NextProbe { get; set; } = now;

    /// <summary>Handshakes the process completed and used.</summary>
    public int Accepted { get; internal set; }
    /// <summary>Handshakes it refused, or finished and dropped without sending data.</summary>
    public int Refused { get; internal set; }

    /// <summary>
    /// A behavioural signal, never proof: an app with its own private trust store refuses an inspected
    /// certificate in the same way. A connection it accepted here rules the signal out.
    /// </summary>
    public PinningSignal Pinning
    {
        get
        {
            if (Refused == 0 || Accepted > 0) return PinningSignal.None;
            var inspectedAndTrusted = Classification?.Verdict == Verdict.Inspected && InspectionTrustedHere && !ProcessGotOtherIssuer;
            return Refused >= ProcessFlowTracker.PinningRefusals && inspectedAndTrusted ? PinningSignal.Likely : PinningSignal.Rejected;
        }
    }

    /// <summary>The Windows trust store accepts the chain CertWatch received.</summary>
    public bool InspectionTrustedHere => Probe is { } probe && !probe.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);

    /// <summary>
    /// The process's own TLS 1.2 certificate has a different issuer from CertWatch's, so it wasn't
    /// handed the same inspection. Issuers are compared because proxies can mint a new leaf per connection.
    /// </summary>
    public bool ProcessGotOtherIssuer =>
        ObservedCertificates.Count > 0 && Probe?.Leaf is { } leaf
        && (ObservedIssuer is null || !leaf.IssuerName.RawData.AsSpan().SequenceEqual(ObservedIssuer));

    /// <summary>Whether CertWatch can make a handshake here: a routable address on a TLS port or with a real handshake seen.</summary>
    public bool CanCheck => ProcessFlowTracker.IsCheckable(this);

    internal byte[]? ObservedIssuer { get; set; }

    /// <summary>
    /// The server refused CertWatch's handshake and no real hostname is known. Servers hosting many
    /// sites do this: they have certificates, but won't pick one until told which site is wanted.
    /// </summary>
    public bool NeedsHostname => Probe is { Leaf: null, HandshakeFailed: true } && HostEvidence != HostEvidence.Observed;

    /// <summary>The inferred name is one this app uses elsewhere, and this server's certificate is valid for it.</summary>
    public bool HostMatchedFromApp { get; internal set; }

    internal HashSet<string> TriedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CertEvidence CertEvidence
    {
        get
        {
            if (ObservedCertificates.Count > 0)
            {
                var same = Probe?.Leaf is not { } leaf || leaf.RawData.AsSpan().SequenceEqual(ObservedCertificates[0]);
                return same ? CertEvidence.SeenInHandshake : CertEvidence.DifferentFromSeen;
            }
            return Probe?.Leaf is null ? CertEvidence.None : CertEvidence.OwnHandshake;
        }
    }

    /// <summary>A plain sentence saying how far this row can be trusted.</summary>
    public string EvidenceText
    {
        get
        {
            var cert = CertEvidence switch
            {
                CertEvidence.SeenInHandshake => "Certificate read from the process's own TLS 1.2 handshake.",
                CertEvidence.DifferentFromSeen => "The process received a different certificate from the one CertWatch's own handshake got. The process's certificate is shown.",
                CertEvidence.OwnHandshake when ObservedVersion == TlsHandshakeParser.Tls13 => "TLS 1.3 encrypts the certificate, so this is CertWatch's own handshake to the same address and host.",
                CertEvidence.OwnHandshake => "Certificate from CertWatch's own handshake to the same address, not from the process's connection.",
                _ when NeedsHostname => "The server has a certificate but won't send it without a hostname, and CertWatch doesn't know which one this connection used.",
                _ => "No certificate yet.",
            };
            var host = HostEvidence switch
            {
                HostEvidence.Observed when EncryptedClientHello => " The process uses Encrypted Client Hello, so the name shown is only the outer one.",
                HostEvidence.Observed => "",
                HostEvidence.Inferred when HostMatchedFromApp => " The host is one this app uses elsewhere and this server's certificate is valid for it, but this connection may be using another name on the same certificate.",
                HostEvidence.Inferred => " The host is a guess from reverse DNS and may be wrong on a shared address.",
                _ when Probe?.Leaf is not null => " No host name is known, so the server chose which certificate to send.",
                _ => "",
            };
            return cert + host;
        }
    }
}

/// <summary>
/// Merges the targeted process's connections, any handshakes read from them, and CertWatch's own
/// handshakes into one row per destination. Pure logic and not thread-safe: the caller holds a lock
/// and does the I/O. Handshakes are only suggested to routable addresses, on TLS ports or where a
/// real TLS handshake was seen, so a non-TLS service never receives an unsolicited ClientHello.
/// </summary>
public sealed class ProcessFlowTracker
{
    public const int MaxDestinations = 256;
    /// <summary>Refusals needed before a row reads "Pinning likely", so one dropped connection can't.</summary>
    public const int PinningRefusals = 3;
    /// <summary>At most this many of an app's hostnames are tried per address, so few names go to a server that doesn't host them.</summary>
    public const int MaxHostnameTries = 8;
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5), RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly int[] TlsPorts = [443, 8443];

    private readonly Dictionary<(IPAddress Remote, int Port, string? Host, string? Owner), FlowDestination> _rows = [];
    private readonly Dictionary<FlowKey, string> _hostByFlow = [];

    public IReadOnlyCollection<FlowDestination> Destinations => _rows.Values;

    /// <summary>
    /// Empties the list and its accepted and refused counts. Connections still open come back on the
    /// next read, keeping any host name already read from their handshake.
    /// </summary>
    public void Clear() => _rows.Clear();

    /// <summary>Applies a fresh read of the connection table.</summary>
    public void Update(IEnumerable<FlowConnection> connections, DateTimeOffset now)
    {
        foreach (var row in _rows.Values) row.OpenConnections = 0;
        var live = new HashSet<FlowKey>();
        foreach (var c in connections)
        {
            var key = Normalize(c.Key);
            live.Add(key);
            var host = _hostByFlow.GetValueOrDefault(key);
            if (GetOrAdd(key.Remote, key.RemotePort, host, now, c.ProcessName) is not { } row) continue;
            row.OpenConnections++;
            row.LastSeen = now;
        }
        foreach (var gone in _hostByFlow.Keys.Where(k => !live.Contains(k)).ToList()) _hostByFlow.Remove(gone);
        // A connection that moved to a named row leaves nothing behind on the unnamed one.
        foreach (var stale in _rows.Where(r => r.Key.Host is null && r.Value.OpenConnections == 0 && HasNamedRow(r.Key.Remote, r.Key.Port, r.Key.Owner)).Select(r => r.Key).ToList())
            _rows.Remove(stale);
    }

    /// <summary>Applies what a connection's own handshake showed.</summary>
    public void Apply(FlowKey connection, TlsHandshakeView view, DateTimeOffset now, HandshakeOutcome outcome = HandshakeOutcome.Unknown, string? owner = null)
    {
        var key = Normalize(connection);
        var host = view.Client?.ServerName;
        if (host is not null) _hostByFlow[key] = host;
        else host = _hostByFlow.GetValueOrDefault(key);
        if (GetOrAdd(key.Remote, key.RemotePort, host, now, owner) is not { } row) return;

        row.HandshakeSeen = true;
        row.LastSeen = now;
        if (host is not null && row.HostEvidence != HostEvidence.Observed)
        {
            row.Host = host;
            row.HostEvidence = HostEvidence.Observed;
            row.NextProbe = now;
        }
        if (view.Client is { } client)
        {
            row.Alpn = client.Alpn;
            row.EncryptedClientHello = client.HasEncryptedClientHello;
        }
        if (view.Server is { } server) row.ObservedVersion = server.Version;
        if (view.CertificateVisible)
        {
            row.ObservedCertificates = view.ServerCertificates;
            row.ObservedIssuer = IssuerOf(view.ServerCertificates[0]);
        }
        if (outcome == HandshakeOutcome.Accepted) row.Accepted++;
        else if (outcome is HandshakeOutcome.Rejected or HandshakeOutcome.ClosedAfterHandshake) row.Refused++;
    }

    /// <summary>Unnamed rows waiting for a reverse-DNS guess. Each is handed out once.</summary>
    public IReadOnlyList<FlowDestination> TakeNameLookups(int max)
    {
        var due = _rows.Values.Where(r => r.Host is null && !r.NameRequested && r.OpenConnections > 0 && IsCheckable(r)).Take(max).ToList();
        foreach (var row in due) row.NameRequested = true;
        return due;
    }

    /// <summary>Records a reverse-DNS result. A name read from a real handshake is never replaced.</summary>
    public void ApplyInferredName(FlowDestination row, string? name, DateTimeOffset now)
    {
        row.NameDone = true;
        row.NextProbe = now;
        if (row.HostEvidence == HostEvidence.Observed || TlsHandshakeParser.NormalizeHost(name) is not { } host) return;
        row.Host = host;
        row.HostEvidence = HostEvidence.Inferred;
    }

    /// <summary>Handshakes that are due. Each stays claimed until <see cref="CompleteProbe"/>.</summary>
    public IReadOnlyList<FlowProbeRequest> TakeProbes(DateTimeOffset now, int max)
    {
        var due = _rows.Values
            .Where(r => r.OpenConnections > 0 && !r.ProbeInFlight && now >= r.NextProbe && (r.Host is not null || r.NameDone) && IsCheckable(r))
            .OrderBy(r => r.Probe is not null)
            .Take(max)
            .ToList();
        foreach (var row in due) row.ProbeInFlight = true;
        return due.Select(r => new FlowProbeRequest(r, new SiteEndpoint(r.Host ?? r.Remote.ToString(), r.Port), r.Remote)).ToList();
    }

    /// <summary>
    /// For servers that refused a handshake with no usable name: the next hostname to try, chosen only
    /// from names the same app was seen sending on the same port. Each is tried once per address.
    /// </summary>
    public IReadOnlyList<FlowProbeRequest> TakeHostnameRetries(int max)
    {
        var due = new List<FlowProbeRequest>();
        foreach (var row in _rows.Values.Where(r => r.OpenConnections > 0 && !r.ProbeInFlight && r.NeedsHostname && IsCheckable(r)))
        {
            if (due.Count >= max) break;
            if (row.TriedHosts.Count >= MaxHostnameTries || NextHostFor(row) is not { } host) continue;
            row.TriedHosts.Add(host);
            row.ProbeInFlight = true;
            due.Add(new FlowProbeRequest(row, new SiteEndpoint(host, row.Port), row.Remote, IsHostnameRetry: true));
        }
        return due;
    }

    // An Encrypted Client Hello's visible name is only the front, so it isn't offered to other servers.
    private string? NextHostFor(FlowDestination row) => _rows.Values
        .Where(c => c.HostEvidence == HostEvidence.Observed && !c.EncryptedClientHello && c.Port == row.Port && c.Owner == row.Owner
                    && c.Host is not null && !row.TriedHosts.Contains(c.Host))
        .OrderByDescending(c => c.OpenConnections)
        .ThenBy(c => c.Host, StringComparer.Ordinal)
        .Select(c => c.Host)
        .FirstOrDefault();

    /// <summary>Accepts a retried name only if the server's certificate is valid for exactly that name.</summary>
    public void CompleteHostnameRetry(FlowProbeRequest request, ProbeResult? result, Classification? classification, EgressInfo? egress, DateTimeOffset now)
    {
        var row = request.Destination;
        row.ProbeInFlight = false;
        if (result is not { Succeeded: true } || !DirectPath.ServesSite(result)) return;
        row.Host = request.Endpoint.Host;
        row.HostEvidence = HostEvidence.Inferred;
        row.HostMatchedFromApp = true;
        row.Probe = result;
        row.Classification = classification;
        row.Egress = egress;
        row.NextProbe = now + RecheckAfter;
    }

    /// <summary>Stores a finished handshake; a null result means it couldn't run.</summary>
    public void CompleteProbe(FlowDestination row, ProbeResult? result, Classification? classification, EgressInfo? egress, DateTimeOffset now)
    {
        row.ProbeInFlight = false;
        var succeeded = result?.Succeeded == true;
        row.NextProbe = now + (succeeded ? RecheckAfter : RetryAfter);
        // A failed re-check keeps the last certificate rather than blanking the row.
        if (result is null || (!succeeded && row.Probe?.Succeeded == true)) return;
        row.Probe = result;
        row.Classification = classification;
        row.Egress = egress;
    }

    // The bytes came off the network: a malformed certificate simply has no known issuer.
    private static byte[]? IssuerOf(byte[] der)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            return cert.IssuerName.RawData;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private bool HasNamedRow(IPAddress remote, int port, string? owner) =>
        _rows.Keys.Any(k => k.Host is not null && k.Port == port && k.Owner == owner && k.Remote.Equals(remote));

    private FlowDestination? GetOrAdd(IPAddress remote, int port, string? host, DateTimeOffset now, string? owner)
    {
        var key = (remote, port, host, owner);
        if (_rows.TryGetValue(key, out var row)) return row;
        if (_rows.Count >= MaxDestinations && !EvictOldestClosed()) return null;
        row = new FlowDestination(remote, port, now, owner);
        if (host is not null)
        {
            // Only names read from a real handshake are row keys, including after the list is cleared.
            row.Host = host;
            row.HostEvidence = HostEvidence.Observed;
            row.HandshakeSeen = true;
        }
        _rows[key] = row;
        return row;
    }

    private bool EvictOldestClosed()
    {
        var closed = _rows.Where(r => r.Value.OpenConnections == 0 && !r.Value.ProbeInFlight).OrderBy(r => r.Value.LastSeen).Take(1).ToList();
        foreach (var r in closed) _rows.Remove(r.Key);
        return closed.Count > 0;
    }

    internal static bool IsCheckable(FlowDestination row) =>
        IsRoutable(row.Remote) && (row.HandshakeSeen || TlsPorts.Contains(row.Port));

    private static bool IsRoutable(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return !a.IsIPv6LinkLocal && !a.IsIPv6Multicast;
        var b = a.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254) && b[0] < 224;
    }

    private static FlowKey Normalize(FlowKey k) => new(Normalize(k.Local), k.LocalPort, Normalize(k.Remote), k.RemotePort);
    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
