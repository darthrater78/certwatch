using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CertWatch.App.Logging;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App;

/// <summary>
/// Everything one browser tab needs: address-bar follower, site monitor and view model.
/// </summary>
internal sealed class BrowserSession : IDisposable
{
    private readonly HistoryStore _history;
    private readonly Dispatcher _dispatcher;
    private readonly SiteMonitor _monitor;
    private readonly BrowserFollower _follower;
    private readonly IRouteLookup? _routes;
    private readonly ICertProbe _probe;
    private readonly InspectionClassifier _classifier;
    // The address that served each site on the browser's own path last time (UI thread only; bounded).
    private readonly Dictionary<string, System.Net.IPAddress> _directCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _directSearch;

    public BrowserSession(BrowserDefinition browser, ICertProbe probe, InspectionClassifier classifier,
        HistoryStore history, TimeSpan recheckInterval, Dispatcher dispatcher, IRouteLookup? routes)
    {
        Browser = browser;
        _history = history;
        _routes = routes;
        _probe = probe;
        _classifier = classifier;
        _dispatcher = dispatcher;

        View = new BrowserTabViewModel(browser);
        View.LoadSites(history.GetSiteSummaries(browser.Id));
        View.SiteSelected += row => View.LoadSiteHistory(row is null ? [] : _history.GetSiteHistory(row.Endpoint.Host, row.Endpoint.Port, browser.Id));
        View.ClearSiteRequested += ClearSite;
        View.ClearBrowserRequested += ClearBrowser;

        _monitor = new SiteMonitor(probe, classifier, history, browser.Id, recheckInterval, routes);
        _monitor.SiteChanged += site => _dispatcher.BeginInvoke(() => OnSiteChanged(site));
        _monitor.Checked += check => _dispatcher.BeginInvoke(() => OnChecked(check));
        View.RecheckRequested += _monitor.RecheckNow;

        _follower = new BrowserFollower(browser, _monitor, _ => true);
        // Only the host:port that will be checked is logged, never the full address (paths and query strings can hold tokens).
        _follower.AddressRead += OnAddressRead;
        LogService.Info(browser.Id, $"Following {browser.DisplayName}");
    }

    public BrowserDefinition Browser { get; }
    public BrowserTabViewModel View { get; }
    /// <summary>Raised on the UI thread after every check of the current site.</summary>
    public event Action<BrowserSession, SiteCheck, BrowserPathResult>? Checked;
    /// <summary>Raised on the UI thread when the search for an enterprise browser's own path ends.</summary>
    public event Action<BrowserSession, SiteCheck, DirectPathResult>? DirectPathChecked;

    public void RecheckNow() => _monitor.RecheckNow();

    /// <summary>The browser window this session last read (zero when none).</summary>
    public IntPtr Window => _follower.Window;

    /// <summary>The process name behind a window (e.g. "msedge"), or null when it can't be read.</summary>
    public static string? ProcessNameOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether a process name is this browser's (the same test the follower uses).</summary>
    public bool OwnsProcess(string? processName) =>
        processName is not null && Browser.ProcessNames.Any(n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a top-level window belongs to this browser.</summary>
    public bool Owns(IntPtr window) => OwnsProcess(ProcessNameOf(window));

    // Background thread. Raised before the monitor hears about the site, and the only place the
    // no-site states are shown, so "not running" and "no HTTPS page" can be told apart.
    private void OnAddressRead(string? text)
    {
        var site = text is null ? null : AddressParser.Parse(text);
        LogService.Debug(Browser.Id, () => $"Address bar: {(text is null ? "(no window)" : site?.ToString() ?? "(not an https site)")}");
        if (site is not null) return; // OnSiteChanged shows it
        _dispatcher.BeginInvoke(() =>
        {
            if (text is null) View.ShowNotRunning();
            else View.ShowNoSite();
        });
    }

    private void OnSiteChanged(SiteEndpoint? site)
    {
        CancelDirectSearch();
        LogService.Info(Browser.Id, site is { } s ? $"Now on {s}" : "No HTTPS page in focus");
        if (site is { } x) View.ShowChecking(x);
    }

    private void OnChecked(SiteCheck check)
    {
        // A different path only matters for an enterprise browser whose check came back inspected:
        // the steering rule for apps outside it can catch CertWatch while the browser goes out on its own.
        // In any other browser, CertWatch's path is the browser's and its result stands.
        var inspected = Browser.Enterprise && check.Classification.Verdict == Verdict.Inspected;
        var path = inspected ? ComparePath(check) : BrowserPathResult.Unknown;
        View.ShowCheck(check, path);
        var c = check.Classification;
        LogService.Info(Browser.Id, $"{check.Probe.Endpoint}: {c.Verdict}{(c.Vendor is null ? "" : $" ({c.Vendor})")}, egress {check.Egress?.InterfaceLabel ?? "unknown"} {check.Egress?.Kind}, change {check.Observation.Change}{(c.Verdict == Verdict.Error ? $": {c.Reason}" : "")}");
        LogService.Debug(Browser.Id, () => $"{check.Probe.Endpoint}: issuer {c.IssuerName}, root {c.RootName}, ip {check.Probe.RemoteAddress}, route {check.Egress?.RouteLabel}, reason: {check.Egress?.Reason}");
        if (path.Match == BrowserPathMatch.Different)
            LogService.Info(Browser.Id, $"{check.Probe.Endpoint}: {Browser.DisplayName} isn't connected to {check.Probe.RemoteAddress} (CertWatch's tunnel address); it uses its own path{(path.BrowserInterfaces.Count == 0 ? "" : $" via {string.Join(", ", path.BrowserInterfaces)}")}");
        Checked?.Invoke(this, check, path);

        // An enterprise browser can go out on its own path while CertWatch is steered: follow its address too.
        if (Browser.Enterprise && inspected) StartDirectSearch(check);
        else
        {
            CancelDirectSearch();
            View.ClearDirectPath();
        }
    }

    private void CancelDirectSearch()
    {
        _directSearch?.Cancel();
        _directSearch?.Dispose();
        _directSearch = null;
    }

    /// <summary>
    /// Handshakes (SNI = the site) to the direct addresses the browser is connected to, in parallel, at
    /// most <see cref="DirectPath.MaxAddresses"/>; the first, in rank order, that serves the site is the
    /// browser's own path. Runs off the UI thread; the result comes back through the dispatcher.
    /// </summary>
    private void StartDirectSearch(SiteCheck check)
    {
        CancelDirectSearch();
        var cts = _directSearch = new CancellationTokenSource();
        var site = check.Probe.Endpoint;
        var lastFound = _directCache.GetValueOrDefault(site.Host);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await FindDirectPathAsync(check, lastFound, cts.Token);
                _ = _dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) OnDirectPath(check, result); });
            }
            catch (OperationCanceledException)
            {
                // The site changed or a newer check started.
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
            {
                LogService.Info(Browser.Id, $"{site}: couldn't check the browser's own path ({ex.Message})");
            }
        });
    }

    private async Task<DirectPathResult> FindDirectPathAsync(SiteCheck check, System.Net.IPAddress? lastFound, CancellationToken ct)
    {
        var site = check.Probe.Endpoint;
        var connections = TcpConnections.EstablishedFor(ProcessIds());
        var candidates = DirectPath.Candidates(connections, site.Port, check.Probe.RemoteAddress,
            ip => EgressFor(ip)?.Kind ?? EgressKind.Unknown, lastFound);
        if (candidates.Count == 0) return new DirectPathResult(DirectPathOutcome.NotFound, null, 0);

        // Re-checks: the address that served the site last time, alone, while it still does.
        var tried = 0;
        ProbeResult? served = null;
        if (lastFound is not null && candidates[0].Equals(lastFound))
        {
            tried = 1;
            var again = await _probe.ProbeAtAsync(site, lastFound, ct);
            if (DirectPath.ServesSite(again)) served = again;
        }
        if (served is null)
        {
            var rest = candidates.Where(ip => !ip.Equals(lastFound)).ToList();
            var probes = await Task.WhenAll(rest.Select(ip => _probe.ProbeAtAsync(site, ip, ct)));
            tried += rest.Count;
            served = probes.FirstOrDefault(DirectPath.ServesSite);
        }
        ct.ThrowIfCancellationRequested();
        if (served is null) return new DirectPathResult(DirectPathOutcome.NotFound, null, tried);

        var classification = _classifier.Classify(served);
        var egress = served.RemoteAddress is { } a ? EgressFor(a) : null;
        var direct = new SiteCheck(served, classification, SiteMonitor.BuildObservation(served, classification, egress, Browser.Id, ChangeKind.None), egress);
        // Inspected by the same issuer as CertWatch's own check: the agent caught this handshake too.
        var captured = classification.Verdict == Verdict.Inspected && check.Classification.Verdict == Verdict.Inspected
                       && string.Equals(classification.IssuerName, check.Classification.IssuerName, StringComparison.Ordinal);
        return new DirectPathResult(captured ? DirectPathOutcome.Captured : DirectPathOutcome.Found, direct, tried);
    }

    private EgressInfo? EgressFor(System.Net.IPAddress address)
    {
        try
        {
            return _routes?.Lookup(address) is { } facts ? EgressClassifier.Classify(facts) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            return null;
        }
    }

    private void OnDirectPath(SiteCheck check, DirectPathResult result)
    {
        var site = check.Probe.Endpoint;
        if (result is { Outcome: DirectPathOutcome.Found, Address: { } found })
        {
            if (_directCache.Count >= 256) _directCache.Clear();
            _directCache[site.Host] = found;
        }
        var d = result.Check;
        LogService.Info(Browser.Id, result.Outcome switch
        {
            DirectPathOutcome.Found => $"{site}: {Browser.DisplayName}'s own path via {result.Address} ({d!.Egress?.InterfaceLabel ?? "route unknown"} {d.Egress?.Kind}): {d.Classification.Verdict}, issuer {d.Classification.IssuerName}",
            DirectPathOutcome.Captured => $"{site}: {Browser.DisplayName} is connected to {result.Address}, but CertWatch's handshake there was inspected too",
            _ => $"{site}: none of {result.AddressesTried} direct connection(s) of {Browser.DisplayName} served the site",
        });
        View.ShowDirectPath(site, result);
        DirectPathChecked?.Invoke(this, check, result);
    }

    /// <summary>
    /// Whether the browser is connected to the address CertWatch checked (read from Windows' TCP
    /// connection table; nothing is asked of the browser). Different only for a tunnel path, where
    /// each site has its own address.
    /// </summary>
    private BrowserPathResult ComparePath(SiteCheck check)
    {
        try
        {
            var connections = TcpConnections.EstablishedFor(ProcessIds());
            var port = check.Probe.Endpoint.Port;
            var match = BrowserPath.Compare(check.Probe.RemoteAddress, port, check.Egress?.Kind, connections);
            if (match != BrowserPathMatch.Different) return new BrowserPathResult(match, []);
            // Name the adapters the browser's own connections to that port leave by (a few lookups at most).
            var interfaces = connections.Where(c => c.RemotePort == port).Select(c => c.Remote).Distinct().Take(6)
                .Select(ip => _routes?.Lookup(ip)?.InterfaceName).OfType<string>().Distinct().ToList();
            return new BrowserPathResult(match, interfaces);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
        {
            return BrowserPathResult.Unknown;
        }
    }

    private HashSet<int> ProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in Browser.ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p) ids.Add(p.Id);
            }
        }
        return ids;
    }

    /// <summary>What the browser's own certificate viewer showed (read from the window the user opened).</summary>
    public void ApplyViewerReading(CertViewerReading reading)
    {
        View.SetViewerReading(reading);
        var live = View.Card.Fingerprint;
        LogService.Info(Browser.Id, $"Certificate viewer for {reading.Host}: issued by {reading.IssuerLabel ?? "unknown"}, {(View.Card.Site.Length > 0 && reading.Matches(live) ? "same certificate as CertWatch's connection" : "compared on the card")}");
    }

    private void ClearSite(SiteRow row)
    {
        var answer = System.Windows.MessageBox.Show(
            $"Clear {Browser.DisplayName}'s history for {row.Host}?\n\nIts next check counts as a first visit.",
            "CertWatch", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        _history.DeleteSite(row.Endpoint.Host, row.Endpoint.Port, Browser.Id);
        View.RemoveSite(row);
        View.StatusMessage = $"Cleared {row.Host} at {DateTime.Now:T}. It reappears when it's checked again.";
        LogService.Info(Browser.Id, $"Cleared history for {row.Host}");
    }

    private void ClearBrowser()
    {
        var answer = System.Windows.MessageBox.Show(
            $"Clear all of {Browser.DisplayName}'s history?\n\nEvery site in this tab is removed; other browsers and the log are kept. This can't be undone.",
            "CertWatch", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        _history.DeleteBrowser(Browser.Id);
        View.ClearSites();
        View.StatusMessage = $"Cleared {Browser.DisplayName}'s history at {DateTime.Now:T}. Sites reappear here as they're checked again.";
        LogService.Info(Browser.Id, $"Cleared all history for {Browser.DisplayName}");
    }

    /// <summary>After the header's "Clear all": empty the list. Sites reappear as they're checked again.</summary>
    public void OnHistoryCleared()
    {
        View.ClearSites();
        View.StatusMessage = $"Everything cleared at {DateTime.Now:T}. Sites reappear here as they're checked again.";
    }

    public static bool IsRunning(BrowserDefinition browser)
    {
        foreach (var name in browser.ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            var any = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    public void Dispose()
    {
        CancelDirectSearch();
        _follower.Dispose();
        _ = _monitor.DisposeAsync().AsTask();
    }
}
