using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CertWatch.App.Presentation;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App;

/// <summary>
/// One window, one tab per browser. Browsers are added automatically when they start running
/// (and always for any named on the command line); a tab stays after its browser closes so its
/// history remains visible.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private readonly StartupOptions _options;
    private readonly HistoryStore _history;
    private readonly CertProbe _probe = new();
    private readonly InspectionClassifier _classifier = new(PublicRootSet.LoadEmbedded());
    private readonly TrayIcon _tray = new();
    private readonly Tracking.WindowsRouteLookup _routes = new();
    private readonly List<BrowserSession> _sessions = [];
    private readonly DispatcherTimer _browserScan = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _exiting;
    // Browser|site pairs already announced as inspected this session, so each gets one pop-up.
    private readonly HashSet<string> _announcedInspected = [];

    internal MainWindow(StartupOptions options)
    {
        InitializeComponent();
        WindowPlacement.Apply(this);
        DataContext = _shell;
        _options = options;
        _history = HistoryKeyStore.OpenHistory(options.DatabasePath ?? HistoryStore.DefaultPath);

        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray); // also raised from toast clicks (background thread)
        _tray.RecheckRequested += () => SessionFor(_shell.SelectedTab)?.RecheckNow();
        _tray.ExitRequested += ExitApp;
        _tray.TestNotificationRequested += () => _tray.Notify("CertWatch test notification",
            "If you can see this, CertWatch pop-ups work. If not, check Windows Settings › System › Notifications (and that Do not disturb is off).",
            important: false);
        _tray.SetBrowser("CertWatch");
        _tray.SetState(null, "CertWatch: waiting for a browser");

        foreach (var b in options.Browsers) AddSession(b);
        ScanForBrowsers();
        _browserScan.Tick += (_, _) => ScanForBrowsers();
        _browserScan.Start();
    }

    private void ScanForBrowsers()
    {
        foreach (var browser in BrowserCatalog.All)
        {
            if (_sessions.Any(s => s.Browser.Id == browser.Id)) continue;
            if (BrowserSession.IsRunning(browser)) AddSession(browser);
        }
    }

    private void AddSession(BrowserDefinition browser)
    {
        if (_sessions.Any(s => s.Browser.Id == browser.Id)) return;
        var session = new BrowserSession(browser, _probe, _classifier, _history, _options.RecheckInterval, Dispatcher, _routes);
        session.Checked += OnChecked;
        session.ClearAllRequested += ClearAllHistory;
        _sessions.Add(session);
        _shell.Tabs.Add(session.View);
        _shell.SelectedTab ??= session.View;
        _shell.HasTabs = true;
    }

    private BrowserSession? SessionFor(BrowserTabViewModel? tab) => _sessions.FirstOrDefault(s => s.View == tab);

    private void OnChecked(BrowserSession session, SiteCheck check)
    {
        var c = check.Classification;
        var browser = session.Browser.DisplayName;
        var site = check.Probe.Endpoint.ToString();

        // The single tray icon shows the latest result across all browsers.
        _tray.SetBrowser(browser);
        _tray.SetState(c.Verdict, $"CertWatch · {browser} · {site}: {VerdictStyle.Short(c.Verdict)}");
        NotifyIfNeeded(check, browser, site);
    }

    /// <summary>
    /// Pop-ups. Inspection: whenever a site is seen inspected and hasn't been announced this session
    /// (so landing on an already-known inspected site still alerts once). Also: inspection stopped,
    /// a different inspecting CA, traffic moving to another interface, and mTLS sites.
    /// </summary>
    private void NotifyIfNeeded(SiteCheck check, string browser, string site)
    {
        var c = check.Classification;
        var change = check.Observation.Change;
        var treatment = PathTreatment.Of(c.Verdict, check.Egress?.Kind.ToString());
        var path = VerdictStyle.Short(treatment, c.Verdict);
        var by = c.Vendor is null ? "" : $" by {c.Vendor}";
        var key = $"{browser}|{site}";

        if (PathTreatment.IsInspected(treatment))
        {
            if (_announcedInspected.Add(key))
            {
                var title = change == ChangeKind.InspectionStarted
                    ? $"{browser} · certificate changed for inspection"
                    : $"{browser} · inspection found";
                _tray.Notify(title, $"{site}: {path}{by}. Issuer: {c.IssuerName}", important: true);
                return;
            }
            if (change == ChangeKind.CertificateChanged)
            {
                _tray.Notify($"{browser} · inspecting CA changed", $"{site} is now inspected via {c.IssuerName}.", important: true);
                return;
            }
        }
        else
        {
            _announcedInspected.Remove(key);
        }

        switch (change)
        {
            case ChangeKind.InspectionStopped:
                _tray.Notify($"{browser} · inspection stopped", $"{site} now shows its original certificate ({c.IssuerName}). {path}.", important: false);
                break;
            case ChangeKind.EgressChanged:
                _tray.Notify($"{browser} · traffic moved", $"{site} now leaves via {check.Egress?.InterfaceLabel}: {path}.", important: true);
                break;
            case ChangeKind.CertificateChanged:
            case ChangeKind.VerdictChanged:
                _tray.Notify($"{browser} · certificate changed", $"{site}: {c.Reason}", important: false);
                break;
        }

        if (c.MutualTls && change == ChangeKind.FirstSeen)
            _tray.Notify($"{browser} · mTLS site", $"{site} asks for a client certificate, so it can't be inspected and needs a bypass rule.", important: true);
    }

    private void ClearAllHistory()
    {
        var answer = MessageBox.Show(this,
            "Clear the history for all sites in every browser?\n\nThis can't be undone.",
            "CertWatch", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _history.DeleteAll();
        _announcedInspected.Clear();
        foreach (var session in _sessions) session.OnHistoryCleared();
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // Closing the window keeps CertWatch running in the tray; Exit is in the tray menu.
    protected override void OnClosing(CancelEventArgs e)
    {
        WindowPlacement.Save(this);
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        WindowPlacement.Save(this);
        _exiting = true;
        _browserScan.Stop();
        foreach (var s in _sessions) s.Dispose();
        _tray.Dispose();
        _history.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
