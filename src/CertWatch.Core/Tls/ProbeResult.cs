using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Browsers;

namespace CertWatch.Core.Tls;

public enum CertificateSource
{
    /// <summary>CertWatch's own TLS connection, taking the same network path the browser usually takes.</summary>
    OwnConnection,
    /// <summary>The exact chain the browser received, read over its debugging port.</summary>
    Browser,
}

/// <summary>What CertWatch saw for a site: from its own TLS connection, or the browser's exact chain.</summary>
public sealed class ProbeResult
{
    public required SiteEndpoint Endpoint { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Leaf first, root (or highest available) last.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];
    public SslPolicyErrors PolicyErrors { get; init; }
    public SslProtocols Protocol { get; init; }
    public TlsCipherSuite? CipherSuite { get; init; }

    /// <summary>The IP address actually connected to (the proxy's, when a proxy was used).</summary>
    public System.Net.IPAddress? RemoteAddress { get; init; }

    /// <summary>Proxy used for the CONNECT tunnel, if the system proxy applied.</summary>
    public Uri? Proxy { get; init; }

    /// <summary>
    /// The server asked for a client certificate during the handshake (mTLS). Heuristic: servers that
    /// only ask on certain paths, after the handshake, are not detected.
    /// </summary>
    public bool ClientCertificateRequested { get; init; }

    public string? Error { get; init; }

    /// <summary>Where <see cref="Chain"/> came from.</summary>
    public CertificateSource Source { get; init; } = CertificateSource.OwnConnection;

    /// <summary>
    /// When <see cref="Source"/> is the browser: the leaf CertWatch's own connection got, for comparison.
    /// A different value means the browser's path and the machine's path disagree.
    /// </summary>
    public string? OwnConnectionLeafFingerprint { get; init; }
    public string? OwnConnectionIssuer { get; init; }

    public bool BrowserAndOwnConnectionDiffer =>
        Source == CertificateSource.Browser && OwnConnectionLeafFingerprint is not null &&
        !string.Equals(OwnConnectionLeafFingerprint, LeafFingerprint, StringComparison.OrdinalIgnoreCase);

    public bool Succeeded => Error is null && Chain.Count > 0;
    public X509Certificate2? Leaf => Chain.Count > 0 ? Chain[0] : null;
    public X509Certificate2? Top => Chain.Count > 0 ? Chain[^1] : null;

    public string? LeafFingerprint => Leaf is null ? null : Sha256Hex(Leaf);

    public static string Sha256Hex(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData));
}
