using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Browsers;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Cdp;

/// <summary>
/// Probe used by every browser tab. Always makes CertWatch's own connection (for the IP, egress route,
/// protocol and mTLS). When the browser was launched for the demo with a debugging port and has the
/// site open, the verdict uses the exact chain the browser received instead.
/// </summary>
public sealed class BrowserCertProbe(ICertProbe ownConnection) : ICertProbe
{
    private volatile ICdpClient? _cdp;

    /// <summary>The demo browser's DevTools connection, or null when not in exact mode.</summary>
    public ICdpClient? Cdp
    {
        get => _cdp;
        set => _cdp = value;
    }

    public async Task<ProbeResult> ProbeAsync(SiteEndpoint endpoint, CancellationToken ct = default)
    {
        var own = await ownConnection.ProbeAsync(endpoint, ct);
        if (_cdp is not { } cdp) return own;

        try
        {
            var page = (await cdp.ListTargetsAsync(ct)).FirstOrDefault(t => t.IsPage && Matches(t.Url, endpoint));
            if (page is null) return own;

            var origin = endpoint.Port == 443 ? $"https://{endpoint.Host}" : $"https://{endpoint.Host}:{endpoint.Port}";
            var der = await cdp.GetCertificateAsync(page, origin, ct);
            if (der.Count == 0) return own;

            var received = der.Select(d => X509CertificateLoader.LoadCertificate(d)).ToList();
            var (chain, errors) = Evaluate(received, endpoint.Host);

            return new ProbeResult
            {
                Endpoint = own.Endpoint,
                Timestamp = own.Timestamp,
                Chain = chain,
                PolicyErrors = errors,
                Protocol = own.Protocol,
                CipherSuite = own.CipherSuite,
                RemoteAddress = own.RemoteAddress,
                Proxy = own.Proxy,
                ClientCertificateRequested = own.ClientCertificateRequested,
                Error = null,
                Source = CertificateSource.Browser,
                OwnConnectionLeafFingerprint = own.LeafFingerprint,
                OwnConnectionIssuer = own.Leaf?.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Debugging endpoint gone or busy: fall back to the own-connection result.
            return own;
        }
    }

    /// <summary>True when a tab's URL is on this host and port.</summary>
    public static bool Matches(string url, SiteEndpoint endpoint) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        string.Equals(u.IdnHost, endpoint.Host, StringComparison.OrdinalIgnoreCase) && u.Port == endpoint.Port;

    /// <summary>
    /// Validates the browser's chain against this machine's trust store the same way a TLS handshake
    /// would, and returns the built chain (which includes the root when the store has it).
    /// </summary>
    private static (IReadOnlyList<X509Certificate2> Chain, SslPolicyErrors Errors) Evaluate(List<X509Certificate2> received, string host)
    {
        var leaf = received[0];
        var errors = SslPolicyErrors.None;
        if (!leaf.MatchesHostname(host)) errors |= SslPolicyErrors.RemoteCertificateNameMismatch;

        using var x509 = new X509Chain();
        x509.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        x509.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        foreach (var c in received.Skip(1)) x509.ChainPolicy.ExtraStore.Add(c);

        var ok = x509.Build(leaf);
        if (!ok) errors |= SslPolicyErrors.RemoteCertificateChainErrors;

        var built = x509.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData)).ToList();
        return (ok && built.Count > 0 ? built : received, errors);
    }
}
