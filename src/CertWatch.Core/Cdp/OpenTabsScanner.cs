using CertWatch.Core.Browsers;
using CertWatch.Core.Monitoring;

namespace CertWatch.Core.Cdp;

/// <summary>An HTTPS tab open in the demo browser.</summary>
public sealed record OpenTab(string Title, string Url, SiteEndpoint Site);

/// <summary>
/// In exact mode, checks every HTTPS tab open in the demo browser (not just the active one) on an
/// interval, through the same monitor so history, change detection and alerts all apply.
/// </summary>
public sealed class OpenTabsScanner : IAsyncDisposable
{
    private readonly ICdpClient _cdp;
    private readonly SiteMonitor _monitor;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public OpenTabsScanner(ICdpClient cdp, SiteMonitor monitor, TimeSpan interval)
    {
        _cdp = cdp;
        _monitor = monitor;
        _interval = interval;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The current list of HTTPS tabs (background thread).</summary>
    public event Action<IReadOnlyList<OpenTab>>? TabsListed;
    /// <summary>A background tab's site was checked (background thread).</summary>
    public event Action<SiteCheck>? TabChecked;
    /// <summary>The demo browser stopped answering (closed).</summary>
    public event Action? BrowserGone;

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var targets = await _cdp.ListTargetsAsync(_stop.Token);
                failures = 0;
                var tabs = targets
                    .Where(t => t.IsPage)
                    .Select(t => (t, site: AddressParser.Parse(t.Url)))
                    .Where(x => x.site is not null && x.t.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .Select(x => new OpenTab(x.t.Title, x.t.Url, x.site!.Value))
                    .ToList();
                TabsListed?.Invoke(tabs);

                // The active tab's site is already checked by the monitor's own loop.
                var current = _monitor.CurrentSite;
                foreach (var site in tabs.Select(t => t.Site).Distinct().Where(s => !Nullable.Equals(s, current)))
                {
                    if (_stop.IsCancellationRequested) break;
                    TabChecked?.Invoke(await _monitor.CheckAsync(site, _stop.Token));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (++failures >= 3)
                {
                    BrowserGone?.Invoke();
                    return;
                }
            }

            try { await Task.Delay(_interval, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
