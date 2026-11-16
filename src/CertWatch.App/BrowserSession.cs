using System.Diagnostics;
using System.Windows.Threading;
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

/// <summary>Everything one browser tab needs: address-bar follower, site monitor and its view model.</summary>
internal sealed class BrowserSession : IDisposable
{
    private readonly HistoryStore _history;
    private readonly Dispatcher _dispatcher;
    private readonly SiteMonitor _monitor;
    private readonly BrowserFollower _follower;
    private readonly BrowserCertProbe _probe;
    private readonly TimeSpan _recheckInterval;
    private OpenTabsScanner? _scanner;
    private DemoBrowser? _demo;

    public BrowserSession(BrowserDefinition browser, ICertProbe probe, InspectionClassifier classifier,
        HistoryStore history, TimeSpan recheckInterval, Dispatcher dispatcher, IRouteLookup? routes)
    {
        Browser = browser;
        _history = history;
        _dispatcher = dispatcher;

        View = new BrowserTabViewModel(browser);
        View.LoadSites(history.GetSiteSummaries(browser.Id));
        View.SiteSelected += row => View.LoadSiteHistory(row is null
            ? []
            : _history.GetSiteHistory(row.Endpoint.Host, row.Endpoint.Port, browser.Id));

        _recheckInterval = recheckInterval;
        _probe = new BrowserCertProbe(probe);
        _monitor = new SiteMonitor(_probe, classifier, history, browser.Id, recheckInterval, routes);
        _monitor.SiteChanged += site => _dispatcher.BeginInvoke(() =>
        {
            if (site is { } s) View.ShowChecking(s);
            else View.ShowNoSite();
        });
        _monitor.Checked += check => _dispatcher.BeginInvoke(() => OnChecked(check));
        View.RecheckRequested += _monitor.RecheckNow;
        View.ClearSiteRequested += ClearSite;
        View.ClearAllRequested += () => ClearAllRequested?.Invoke();
        View.LaunchDemoRequested += () => _ = LaunchDemoAsync();

        _follower = new BrowserFollower(browser, _monitor);
        _follower.AddressRead += text => _dispatcher.BeginInvoke(() =>
            View.AddressText = text ?? $"{browser.DisplayName} isn't running or has no window");
    }

    public BrowserDefinition Browser { get; }
    public BrowserTabViewModel View { get; }

    /// <summary>Raised on the UI thread after every check: the current site, and in exact mode every open tab.</summary>
    public event Action<BrowserSession, SiteCheck>? Checked;
    /// <summary>The user asked to clear all history (handled for every browser by the window).</summary>
    public event Action? ClearAllRequested;

    public void RecheckNow() => _monitor.RecheckNow();

    private void OnChecked(SiteCheck check)
    {
        View.ShowCheck(check);
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
    }

    /// <summary>After all history was deleted: empty the list. Sites reappear as they're checked again.</summary>
    public void OnHistoryCleared()
    {
        View.ClearSites();
        View.StatusMessage = $"History cleared at {DateTime.Now:T}. Sites reappear here as they're checked again.";
    }

    private async Task LaunchDemoAsync()
    {
        if (_demo is not null) return;

        var name = Browser.DisplayName;
        var answer = System.Windows.MessageBox.Show(
            $"Launch for demo opens a separate {name} window for your demo.\n\n" +
            $"What happens:\n" +
            $"• {name} starts with its own CertWatch profile, so it won't have your usual bookmarks, sign-ins or extensions. Sign in there if the demo needs it; the profile is kept for next time.\n" +
            $"• CertWatch connects to that window over a private pipe that only CertWatch holds (no network port), which lets it see every open tab and the exact certificate {name} received for each site.\n" +
            $"• CertWatch then checks all of that window's tabs, not just the one in front.\n\n" +
            $"Your normal {name} windows keep working and are still tracked the usual way. Close the demo window to go back to normal.\n\n" +
            $"Why a separate profile: {name} doesn't allow remote debugging on your normal profile.\n\n" +
            "Continue?",
            $"CertWatch · Launch {name} for demo",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        View.DemoStarting = true;
        View.ModeText = $"Starting {Browser.DisplayName} for the demo…";
        try
        {
            var demo = await DemoBrowserLauncher.LaunchAsync(Browser);
            _demo = demo;
            _probe.Cdp = demo.Cdp;

            var scanner = new OpenTabsScanner(demo.Cdp, _monitor, _recheckInterval);
            scanner.TabsListed += tabs => _dispatcher.BeginInvoke(() => View.SetOpenTabs(tabs));
            scanner.TabChecked += check => _dispatcher.BeginInvoke(() =>
            {
                View.UpdateRow(check);
                Checked?.Invoke(this, check);
            });
            scanner.BrowserGone += () => _dispatcher.BeginInvoke(EndDemo);
            _scanner = scanner;

            demo.Process.EnableRaisingEvents = true;
            demo.Process.Exited += (_, _) => _dispatcher.BeginInvoke(EndDemo);

            View.DemoActive = true;
            View.ModeText = $"Exact mode: certificates as {Browser.DisplayName} received them · every open tab checked (demo profile, private DevTools pipe)";
            _monitor.RecheckNow();
        }
        catch (Exception ex)
        {
            View.ModeText = "Mode: CertWatch's own connection on this browser's path";
            System.Windows.MessageBox.Show($"Couldn't start {Browser.DisplayName} for the demo.\n\n{ex.Message}",
                "CertWatch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            View.DemoStarting = false;
        }
    }

    private void EndDemo()
    {
        if (_demo is null) return;
        _probe.Cdp = null;
        _ = _demo.Cdp.DisposeAsync().AsTask();
        if (_scanner is { } s) _ = s.DisposeAsync().AsTask();
        _scanner = null;
        _demo = null;
        View.DemoActive = false;
        View.SetOpenTabs([]);
        View.ModeText = "Mode: CertWatch's own connection on this browser's path (demo browser closed)";
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
        if (_scanner is { } s) _ = s.DisposeAsync().AsTask();
        _follower.Dispose();
        _ = _monitor.DisposeAsync().AsTask();
    }
}
