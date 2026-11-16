using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using CertWatch.Core.Browsers;
using CertWatch.Core.Cdp;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.Core.Tests;

/// <summary>
/// A stand-in for a browser started with --remote-debugging-pipe, over real anonymous pipes:
/// NUL-delimited JSON, flattened sessions, and an unrelated event before every reply.
/// </summary>
internal sealed class FakePipeBrowser : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _browserToClient = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _clientToBrowser = new(PipeDirection.In);
    private readonly Task _loop;
    private readonly string _pageUrl;
    private readonly IReadOnlyList<byte[]> _chain;

    public FakePipeBrowser(string pageUrl, IReadOnlyList<byte[]> chain)
    {
        _pageUrl = pageUrl;
        _chain = chain;
        Client = new CdpPipeClient(
            new AnonymousPipeClientStream(PipeDirection.Out, _clientToBrowser.ClientSafePipeHandle),
            new AnonymousPipeClientStream(PipeDirection.In, _browserToClient.ClientSafePipeHandle));
        _loop = Task.Run(RunAsync);
    }

    public CdpPipeClient Client { get; }
    public List<(string Method, string? Session)> Calls { get; } = [];

    /// <summary>Simulates the browser exiting: its write end closes.</summary>
    public void Close() => _browserToClient.Dispose();

    private async Task RunAsync()
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        try
        {
            while (true)
            {
                var read = await _clientToBrowser.ReadAsync(buffer);
                if (read == 0) return;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 0) { pending.WriteByte(buffer[i]); continue; }
                    await ReplyAsync(pending.ToArray());
                    pending.SetLength(0);
                }
            }
        }
        catch (Exception) { /* pipe closed */ }
    }

    private async Task ReplyAsync(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString()!;
        var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        lock (Calls) Calls.Add((method, session));

        object result = method switch
        {
            "Target.getTargets" => new
            {
                targetInfos = new object[]
                {
                    new { targetId = "T1", type = "page", title = "Test page", url = _pageUrl },
                    new { targetId = "T2", type = "service_worker", title = "sw", url = _pageUrl },
                    new { targetId = "T3", type = "page", title = "Settings", url = "chrome://settings/" },
                },
            },
            "Target.attachToTarget" => new { sessionId = "S1" },
            "Network.getCertificate" when session == "S1" => new { tableNames = _chain.Select(Convert.ToBase64String).ToArray() },
            _ => new { },
        };
        await SendAsync("""{"method":"Network.dataReceived","params":{}}""");
        await SendAsync(JsonSerializer.Serialize(new { id, result }));
    }

    private async Task SendAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        await _browserToClient.WriteAsync(bytes);
        await _browserToClient.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _browserToClient.Dispose();
        _clientToBrowser.Dispose();
        try { await _loop; } catch { }
    }
}

internal sealed class FixedProbe(ProbeResult result) : ICertProbe
{
    public Task<ProbeResult> ProbeAsync(SiteEndpoint endpoint, CancellationToken ct = default) => Task.FromResult(result);
}

public class CdpTests
{
    private static readonly SiteEndpoint Site = new("news.adatum.com", 443);

    [Fact]
    public async Task Lists_targets_and_reads_certificate_chain_over_the_pipe()
    {
        var root = TestCerts.Root("CDP Test Root");
        var leaf = TestCerts.Issue(root, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        await using var browser = new FakePipeBrowser("https://news.adatum.com/world", [leaf.RawData, root.RawData]);
        var cdp = browser.Client;

        Assert.True(await cdp.IsAliveAsync());
        var targets = await cdp.ListTargetsAsync();
        Assert.Equal(3, targets.Count);
        var page = Assert.Single(targets, t => t.IsPage && t.Url.StartsWith("https://"));

        var chain = await cdp.GetCertificateAsync(page, "https://news.adatum.com");
        Assert.Equal(2, chain.Count);
        Assert.Equal(leaf.RawData, chain[0]);
        Assert.Contains(("Network.getCertificate", "S1"), browser.Calls);
        Assert.Contains(browser.Calls, c => c.Method == "Target.detachFromTarget");
    }

    [Fact]
    public async Task Closed_browser_is_reported_not_alive()
    {
        await using var browser = new FakePipeBrowser("https://news.adatum.com/", []);
        Assert.True(await browser.Client.IsAliveAsync());
        browser.Close();
        for (var i = 0; i < 50 && !browser.Client.IsClosed; i++) await Task.Delay(20);
        Assert.False(await browser.Client.IsAliveAsync());
    }

    [Fact]
    public async Task Browser_chain_replaces_own_connection_and_difference_is_flagged()
    {
        var publicRoot = TestCerts.Root("Test Public Root");
        var publicLeaf = TestCerts.Issue(publicRoot, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var own = new ProbeResult
        {
            Endpoint = Site, Timestamp = DateTimeOffset.UtcNow, Chain = [publicLeaf, publicRoot],
            RemoteAddress = IPAddress.Parse("104.18.32.7"),
        };

        var islandRoot = TestCerts.Root("Island Technology, Inc. Root CA");
        var islandInter = TestCerts.Issue(islandRoot, "Island Technology, Inc. Intermediate CA", isCa: true);
        var islandLeaf = TestCerts.Issue(islandInter, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        await using var browser = new FakePipeBrowser("https://news.adatum.com/", [islandLeaf.RawData, islandInter.RawData]);

        var result = await new BrowserCertProbe(new FixedProbe(own)) { Cdp = browser.Client }.ProbeAsync(Site);

        Assert.Equal(CertificateSource.Browser, result.Source);
        Assert.Equal(ProbeResult.Sha256Hex(islandLeaf), result.LeafFingerprint);
        Assert.Equal(ProbeResult.Sha256Hex(publicLeaf), result.OwnConnectionLeafFingerprint);
        Assert.True(result.BrowserAndOwnConnectionDiffer);
        Assert.Equal(IPAddress.Parse("104.18.32.7"), result.RemoteAddress);

        var c = new InspectionClassifier(new PublicRootSet([publicRoot])).Classify(result);
        Assert.Equal(Verdict.Inspected, c.Verdict);
        Assert.Equal("Island", c.Vendor);
    }

    [Fact]
    public async Task Site_not_open_in_browser_falls_back_to_own_connection()
    {
        var root = TestCerts.Root("Test Public Root");
        var leaf = TestCerts.Issue(root, "news.adatum.com", isCa: false, ["news.adatum.com"]);
        var own = new ProbeResult { Endpoint = Site, Timestamp = DateTimeOffset.UtcNow, Chain = [leaf, root] };
        await using var browser = new FakePipeBrowser("https://other.example/", [leaf.RawData]);

        var result = await new BrowserCertProbe(new FixedProbe(own)) { Cdp = browser.Client }.ProbeAsync(Site);
        Assert.Equal(CertificateSource.OwnConnection, result.Source);
    }

    [Fact]
    public async Task Closed_pipe_falls_back_to_own_connection()
    {
        var root = TestCerts.Root("Test Public Root");
        var own = new ProbeResult { Endpoint = Site, Timestamp = DateTimeOffset.UtcNow, Chain = [root] };
        await using var browser = new FakePipeBrowser("https://news.adatum.com/", [root.RawData]);
        browser.Close();
        for (var i = 0; i < 50 && !browser.Client.IsClosed; i++) await Task.Delay(20);

        var result = await new BrowserCertProbe(new FixedProbe(own)) { Cdp = browser.Client }.ProbeAsync(Site);
        Assert.Equal(CertificateSource.OwnConnection, result.Source);
    }

    [Theory]
    [InlineData("https://news.adatum.com/world", true)]
    [InlineData("https://NEWS.adatum.com", true)]
    [InlineData("https://news.adatum.com:8443/", false)]
    [InlineData("http://news.adatum.com/", false)]
    [InlineData("https://sub.news.adatum.com/", false)]
    [InlineData("chrome://newtab", false)]
    public void Matches_tab_url_to_site(string url, bool expected) =>
        Assert.Equal(expected, BrowserCertProbe.Matches(url, Site));
}
