using System.Diagnostics;
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

    public BrowserSession(BrowserDefinition browser, ICertProbe probe, InspectionClassifier classifier,
        HistoryStore history, TimeSpan recheckInterval, Dispatcher dispatcher, IRouteLookup? routes)
    {
        Browser = browser;
        _history = history;
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
        _follower.AddressRead += text => LogService.Debug(browser.Id, () =>
            $"Address bar: {(text is null ? "(no window)" : AddressParser.Parse(text)?.ToString() ?? "(not an https site)")}");
        LogService.Info(browser.Id, $"Following {browser.DisplayName}");
    }

    public BrowserDefinition Browser { get; }
    public BrowserTabViewModel View { get; }
    /// <summary>Raised on the UI thread after every check of the current site.</summary>
    public event Action<BrowserSession, SiteCheck>? Checked;

    public void RecheckNow() => _monitor.RecheckNow();

    private void OnSiteChanged(SiteEndpoint? site)
    {
        LogService.Info(Browser.Id, site is { } s ? $"Now on {s}" : "No HTTPS page in focus");
        if (site is { } x) View.ShowChecking(x);
        else View.ShowNoSite();
    }

    private void OnChecked(SiteCheck check)
    {
        View.ShowCheck(check);
        var c = check.Classification;
        LogService.Info(Browser.Id, $"{check.Probe.Endpoint}: {c.Verdict}{(c.Vendor is null ? "" : $" ({c.Vendor})")}, egress {check.Egress?.InterfaceLabel ?? "unknown"} {check.Egress?.Kind}, change {check.Observation.Change}{(c.Verdict == Verdict.Error ? $": {c.Reason}" : "")}");
        LogService.Debug(Browser.Id, () => $"{check.Probe.Endpoint}: issuer {c.IssuerName}, root {c.RootName}, ip {check.Probe.RemoteAddress}, route {check.Egress?.RouteLabel}, reason: {check.Egress?.Reason}");
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
        _follower.Dispose();
        _ = _monitor.DisposeAsync().AsTask();
    }
}
