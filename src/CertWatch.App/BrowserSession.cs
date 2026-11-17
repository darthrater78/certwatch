using System.Diagnostics;
using System.Windows.Threading;
using CertWatch.App.Logging;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.Core.Browsers;
using CertWatch.Core.Cdp;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App;

/// <summary>
/// Everything one browser tab needs: address-bar follower, site monitor and view model. A demo tab
/// (opened by Launch for demo) follows only the demo browser's window, reads exact certificates over
/// its DevTools pipe, and checks every open tab.
/// </summary>
internal sealed class BrowserSession : IDisposable
{
    private readonly HistoryStore _history;
    private readonly Dispatcher _dispatcher;
    private readonly SiteMonitor _monitor;
    private readonly BrowserFollower _follower;
    private readonly BrowserCertProbe _probe;
    private readonly OpenTabsScanner? _scanner;
    private readonly DemoBrowser? _demo;

    public BrowserSession(BrowserDefinition browser, ICertProbe probe, InspectionClassifier classifier,
        HistoryStore history, TimeSpan recheckInterval, Dispatcher dispatcher, IRouteLookup? routes, DemoBrowser? demo = null)
    {
        Browser = browser;
        _history = history;
        _dispatcher = dispatcher;
        _demo = demo;

        View = new BrowserTabViewModel(browser, demo is not null);
        View.LoadSites(history.GetSiteSummaries(browser.Id));
        View.SiteSelected += row => View.LoadSiteHistory(row is null ? [] : _history.GetSiteHistory(row.Endpoint.Host, row.Endpoint.Port, browser.Id));
        View.ClearSiteRequested += ClearSite;
        View.ClearEverythingRequested += () => ClearEverythingRequested?.Invoke();
        View.LaunchDemoRequested += () => LaunchDemoRequested?.Invoke(this);

        _probe = new BrowserCertProbe(probe) { Cdp = demo?.Cdp };
        _monitor = new SiteMonitor(_probe, classifier, history, browser.Id, recheckInterval, routes);
        _monitor.SiteChanged += site => _dispatcher.BeginInvoke(() => OnSiteChanged(site));
        _monitor.Checked += check => _dispatcher.BeginInvoke(() => OnChecked(check, background: false));
        View.RecheckRequested += _monitor.RecheckNow;

        Func<int, bool> accept = demo is null ? pid => !DemoRegistry.IsDemo(pid) : pid => pid == demo.Process.Id;
        _follower = new BrowserFollower(browser, _monitor, accept);
        _follower.AddressRead += text => LogService.Debug(browser.Id, () => $"Address bar: {text ?? "(no window)"}");

        if (demo is not null)
        {
            _scanner = new OpenTabsScanner(demo.Cdp, _monitor, recheckInterval);
            _scanner.TabsListed += tabs => _dispatcher.BeginInvoke(() => View.SetOpenTabs(tabs));
            _scanner.TabChecked += check => _dispatcher.BeginInvoke(() => OnChecked(check, background: true));
            _scanner.BrowserGone += () => _dispatcher.BeginInvoke(EndDemo);
            demo.Process.EnableRaisingEvents = true;
            demo.Process.Exited += (_, _) => _dispatcher.BeginInvoke(EndDemo);
        }
        LogService.Info(browser.Id, demo is null ? $"Following {browser.DisplayName}" : $"Following the {browser.DisplayName} window (pid {demo.Process.Id}) over its DevTools pipe");
    }

    public BrowserDefinition Browser { get; }
    public BrowserTabViewModel View { get; }
    public bool IsDemo => _demo is not null;

    /// <summary>Raised on the UI thread after every check (the current site, and in a demo tab every open tab).</summary>
    public event Action<BrowserSession, SiteCheck>? Checked;
    public event Action? ClearEverythingRequested;
    public event Action<BrowserSession>? LaunchDemoRequested;

    public void RecheckNow() => _monitor.RecheckNow();

    private void OnSiteChanged(SiteEndpoint? site)
    {
        LogService.Info(Browser.Id, site is { } s ? $"Now on {s}" : "No HTTPS page in focus");
        if (site is { } x) View.ShowChecking(x);
        else View.ShowNoSite();
    }

    private void OnChecked(SiteCheck check, bool background)
    {
        if (background) View.UpdateRow(check);
        else View.ShowCheck(check);
        var c = check.Classification;
        LogService.Info(Browser.Id, $"{check.Probe.Endpoint}: {c.Verdict}{(c.Vendor is null ? "" : $" ({c.Vendor})")}, egress {check.Egress?.InterfaceLabel ?? "unknown"} {check.Egress?.Kind}, change {check.Observation.Change}{(background ? " [background tab]" : "")}");
        LogService.Debug(Browser.Id, () => $"{check.Probe.Endpoint}: issuer {c.IssuerName}, root {c.RootName}, source {check.Probe.Source}, ip {check.Probe.RemoteAddress}, route {check.Egress?.RouteLabel}, reason: {check.Egress?.Reason}");
        Checked?.Invoke(this, check);
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

    /// <summary>After "Clear everything": empty the list. Sites reappear as they're checked again.</summary>
    public void OnHistoryCleared()
    {
        View.ClearSites();
        View.StatusMessage = $"Everything cleared at {DateTime.Now:T}. Sites reappear here as they're checked again.";
    }

    private void EndDemo()
    {
        if (_demo is null || View.DemoClosed) return;
        _probe.Cdp = null;
        if (_scanner is { } s) _ = s.DisposeAsync().AsTask();
        _ = _demo.Cdp.DisposeAsync().AsTask();
        DemoRegistry.Remove(_demo.Process.Id);
        View.DemoClosed = true;
        View.SetOpenTabs([]);
        View.ShowNoSite();
        LogService.Info(Browser.Id, "Demo window closed");
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
        EndDemo();
        _follower.Dispose();
        _ = _monitor.DisposeAsync().AsTask();
    }
}
