using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CertWatch.Core.Browsers;

namespace CertWatch.Core.Tls;

public interface ICertProbe
{
    Task<ProbeResult> ProbeAsync(SiteEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// The same handshake (SNI = the site), but to one given address, with no proxy: used to follow the
    /// address a browser is actually connected to, when CertWatch's own lookup would go elsewhere.
    /// </summary>
    Task<ProbeResult> ProbeAtAsync(SiteEndpoint endpoint, IPAddress address, CancellationToken ct = default);
}

/// <summary>
/// Opens its own TLS connection to a site, through the system proxy when one applies, so it takes the
/// same network path most browsers take. Never sends application data; certificate validation never
/// blocks the handshake (we are observing, not trusting).
/// </summary>
public sealed class CertProbe : ICertProbe
{
    private readonly TimeSpan _timeout;
    private readonly IWebProxy? _proxy;

    public CertProbe(TimeSpan? timeout = null, IWebProxy? proxy = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _proxy = proxy ?? HttpClient.DefaultProxy;
    }

    public Task<ProbeResult> ProbeAsync(SiteEndpoint endpoint, CancellationToken ct = default) => ProbeCoreAsync(endpoint, null, ct);

    public Task<ProbeResult> ProbeAtAsync(SiteEndpoint endpoint, IPAddress address, CancellationToken ct = default) => ProbeCoreAsync(endpoint, address, ct);

    private async Task<ProbeResult> ProbeCoreAsync(SiteEndpoint endpoint, IPAddress? address, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;

        var chain = new List<X509Certificate2>();
        var policyErrors = SslPolicyErrors.None;
        var clientCertRequested = false;
        // A fixed address is the browser's own connection target: connect to it directly, never via a proxy.
        var proxyUri = address is null ? ResolveProxy(endpoint) : null;
        IPAddress? remote = null;

        try
        {
            using var tcp = new TcpClient();
            if (address is not null)
            {
                await tcp.ConnectAsync(address, endpoint.Port, token);
            }
            else if (proxyUri is null)
            {
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, token);
            }
            else
            {
                await tcp.ConnectAsync(proxyUri.Host, proxyUri.Port, token);
                await OpenConnectTunnelAsync(tcp.GetStream(), endpoint, token);
            }

            if (tcp.Client.RemoteEndPoint is IPEndPoint ep)
                remote = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;

            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Host,
                // TLS 1.2 floor. Sites offering only TLS 1.0/1.1 show as "couldn't check".
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, x509Chain, errors) =>
                {
                    policyErrors = errors;
                    CaptureChain(cert, x509Chain, chain);
                    return true;
                },
                LocalCertificateSelectionCallback = (_, _, _, remoteCert, acceptableIssuers) =>
                {
                    // Some platforms call this before the handshake with no server certificate yet;
                    // only a call after the server's certificate (or with issuer hints) is a real request.
                    if (remoteCert is not null || acceptableIssuers.Length > 0) clientCertRequested = true;
                    return null!; // We have no client certificate; the server decides what happens next.
                },
            };

            try
            {
                await ssl.AuthenticateAsClientAsync(options, token);
            }
            catch (Exception ex) when (chain.Count > 0 && ex is not OperationCanceledException)
            {
                // The server certificate was received but the handshake failed afterwards, typically
                // because the server required a client certificate we could not present.
                return Build(endpoint, chain, policyErrors, proxyUri, clientCertRequested, ssl: null,
                    error: clientCertRequested ? null : $"Handshake failed after certificate: {ex.Message}", remote);
            }

            return Build(endpoint, chain, policyErrors, proxyUri, clientCertRequested, ssl, error: null, remote);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Build(endpoint, chain, policyErrors, proxyUri, clientCertRequested, null, "Timed out", remote);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return Build(endpoint, chain, policyErrors, proxyUri, clientCertRequested, null, ex.Message, remote);
        }
    }

    private Uri? ResolveProxy(SiteEndpoint endpoint)
    {
        if (_proxy is null) return null;
        var target = new Uri($"https://{endpoint.Host}:{endpoint.Port}/");
        if (_proxy.IsBypassed(target)) return null;
        var proxy = _proxy.GetProxy(target);
        return proxy is null || proxy == target ? null : proxy;
    }

    private static async Task OpenConnectTunnelAsync(NetworkStream stream, SiteEndpoint endpoint, CancellationToken ct)
    {
        var authority = $"{endpoint.Host}:{endpoint.Port}";
        var request = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        await stream.WriteAsync(request, ct);

        // Read the proxy's response headers byte by byte so no TLS bytes are consumed.
        var header = new StringBuilder();
        var buffer = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer, ct) == 0) throw new IOException("Proxy closed the connection");
            header.Append((char)buffer[0]);
            if (header.Length > 16 * 1024) throw new IOException("Proxy response too large");
        }

        var statusLine = header.ToString().Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || parts[1] != "200")
            throw new IOException($"Proxy refused CONNECT: {statusLine}");
    }

    private static void CaptureChain(X509Certificate? cert, X509Chain? x509Chain, List<X509Certificate2> into)
    {
        into.Clear();
        if (x509Chain is { ChainElements.Count: > 0 })
        {
            foreach (var element in x509Chain.ChainElements)
                into.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));
        }
        else if (cert is not null)
        {
            into.Add(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
        }
    }

    private static ProbeResult Build(SiteEndpoint endpoint, List<X509Certificate2> chain, SslPolicyErrors errors,
        Uri? proxy, bool clientCertRequested, SslStream? ssl, string? error, IPAddress? remote) => new()
    {
        RemoteAddress = remote,
        Endpoint = endpoint,
        Timestamp = DateTimeOffset.UtcNow,
        Chain = chain.ToArray(),
        PolicyErrors = errors,
        Protocol = ssl?.SslProtocol ?? default,
        CipherSuite = ssl?.NegotiatedCipherSuite,
        Proxy = proxy,
        ClientCertificateRequested = clientCertRequested,
        Error = error ?? (chain.Count == 0 ? "No certificate received" : null),
    };
}
