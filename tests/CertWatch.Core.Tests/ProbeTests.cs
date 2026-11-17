using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CertWatch.Core.Browsers;
using CertWatch.Core.Tls;

namespace CertWatch.Core.Tests;

/// <summary>Probes a local TLS server, with and without a client-certificate request.</summary>
public class ProbeTests
{
    private static async Task<(int Port, Task Server)> StartServerAsync(X509Certificate2 serverCert,
        X509Certificate2 intermediate, bool requestClientCert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                var context = SslStreamCertificateContext.Create(serverCert, [intermediate], offline: true);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = context,
                    ClientCertificateRequired = requestClientCert,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.Tls12,
                });
                await Task.Delay(200);
            }
            catch { /* the probe may abort; that's fine */ }
            finally { listener.Stop(); }
        });
        await Task.Yield();
        return (port, server);
    }

    private static (X509Certificate2 Leaf, X509Certificate2 Inter) MakeChain()
    {
        var root = TestCerts.Root("Probe Test Root");
        var inter = TestCerts.Issue(root, "Probe Test Intermediate", isCa: true);
        var leaf = TestCerts.Issue(inter, "localhost", isCa: false, ["localhost"]);
        return (leaf, inter);
    }

    [Fact]
    public async Task Captures_chain_from_server()
    {
        var (leaf, inter) = MakeChain();
        var (port, server) = await StartServerAsync(leaf, inter, requestClientCert: false);
        var result = await new CertProbe(proxy: new WebProxy()).ProbeAsync(new SiteEndpoint("localhost", port));
        await server;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProbeResult.Sha256Hex(leaf), result.LeafFingerprint);
        Assert.Contains(result.Chain, c => c.Subject == "CN=Probe Test Intermediate");
        Assert.False(result.ClientCertificateRequested);
        Assert.True(result.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task Detects_client_certificate_request()
    {
        var (leaf, inter) = MakeChain();
        var (port, server) = await StartServerAsync(leaf, inter, requestClientCert: true);
        var result = await new CertProbe(proxy: new WebProxy()).ProbeAsync(new SiteEndpoint("localhost", port));
        await server;

        Assert.True(result.ClientCertificateRequested);
        Assert.Equal(ProbeResult.Sha256Hex(leaf), result.LeafFingerprint);
    }

    [Fact]
    public async Task Unreachable_host_reports_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await new CertProbe(TimeSpan.FromSeconds(3), new WebProxy()).ProbeAsync(new SiteEndpoint("localhost", port));
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }
}
