using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using CertWatch.App.Logging;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.App.Views;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App;

/// <summary>
/// One window, one tab per browser (plus a tab per demo window). Browsers are added automatically
/// when they start running; a tab stays after its browser closes so its history remains visible.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private readonly StartupOptions _options;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly CertProbe _probe = new();
    private readonly InspectionClassifier _classifier = new(PublicRootSet.LoadEmbedded());
    private readonly TrayIcon _tray = new();
    private readonly WindowsRouteLookup _routes = new();
    private readonly List<BrowserSession> _sessions = [];
    private readonly DispatcherTimer _browserScan = new() { Interval = TimeSpan.FromSeconds(3) };
    // Browser|site pairs already announced as inspected this session, so each gets one pop-up.
    private readonly HashSet<string> _announcedInspected = [];
    private LogWindow? _logWindow;
    private bool _exiting;

    internal MainWindow(StartupOptions options)
    {
        _settings = AppSettings.Load();
        LogService.Initialize(Dispatcher, _settings.DebugLogging, _settings.LogToFile);
        ThemeManager.Initialize(_settings.Theme);
        InitializeComponent();
        ThemeManager.Track(this);
        WindowPlacement.Apply(this);
        DataContext = _shell;
        _shell.ThemeText = $"Theme: {ThemeManager.Mode}";
        _options = options;
        _history = HistoryKeyStore.OpenHistory(options.DatabasePath ?? HistoryStore.DefaultPath);
        LogService.Info("app", $"CertWatch {_shell.VersionText} started");

        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray); // also raised from toast clicks (background thread)
        _tray.RecheckRequested += () => SessionFor(_shell.SelectedTab)?.RecheckNow();
        _tray.ExitRequested += ExitApp;
        _tray.TestNotificationRequested += () => _tray.Notify("CertWatch test notification",
            "If you can see this, CertWatch pop-ups work. If not, check Windows Settings › System › Notifications (and that Do not disturb is off).", important: false);
        _tray.SetState(StatusKind.Neutral, "CertWatch: waiting for a browser");

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

    private BrowserSession AddSession(BrowserDefinition browser, DemoBrowser? demo = null)
    {
        var existing = _sessions.FirstOrDefault(s => s.Browser.Id == browser.Id);
        if (existing is not null) return existing;
        var session = new BrowserSession(browser, _probe, _classifier, _history, _options.RecheckInterval, Dispatcher, _routes, demo);
        session.Checked += OnChecked;
        session.ClearEverythingRequested += ClearEverything;
        session.LaunchDemoRequested += s => _ = LaunchDemoAsync(s);
        _sessions.Add(session);
        _shell.Tabs.Add(session.View);
        _shell.SelectedTab ??= session.View;
        _shell.HasTabs = true;
        return session;
    }

    private BrowserSession? SessionFor(BrowserTabViewModel? tab) => _sessions.FirstOrDefault(s => s.View == tab);

    // ---- Launch for demo: a separate browser window in its own tab ----

    private async Task LaunchDemoAsync(BrowserSession normal)
    {
        var browser = normal.Browser;
        if (!DemoBrowserLauncher.Confirm(browser.DisplayName)) return;

        var demoDefinition = browser with { Id = $"{browser.Id}-demo", DisplayName = $"{browser.DisplayName} · demo" };
        if (_sessions.FirstOrDefault(s => s.Browser.Id == demoDefinition.Id) is { } old)
        {
            if (!old.View.DemoClosed) { _shell.SelectedTab = old.View; return; } // already running
            RemoveSession(old);
        }

        try
        {
            normal.View.StatusMessage = $"Starting a {browser.DisplayName} window for the demo…";
            var demo = await DemoBrowserLauncher.LaunchAsync(browser);
            DemoRegistry.Add(demo.Process.Id);
            normal.View.StatusMessage = "";
            _shell.SelectedTab = AddSession(demoDefinition, demo).View;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            normal.View.StatusMessage = "";
            LogService.Error(browser.Id, $"Launch for demo failed: {ex.Message}");
            MessageBox.Show(this, $"Couldn't start {browser.DisplayName} for the demo.\n\n{ex.Message}", "CertWatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSession(BrowserSession session)
    {
        session.Dispose();
        _sessions.Remove(session);
        _shell.Tabs.Remove(session.View);
    }

    // ---- results: tray and notifications ----

    private void OnChecked(BrowserSession session, SiteCheck check)
    {
        var c = check.Classification;
        var browser = session.Browser.DisplayName;
        var site = check.Probe.Endpoint.ToString();
        var treatment = PathTreatment.Of(c.Verdict, check.Egress?.Kind.ToString());
        _tray.SetState(Status.Of(treatment, c.Verdict), $"CertWatch · {browser} · {site}: {VerdictStyle.Short(treatment, c.Verdict)}");

        foreach (var (title, text, important) in NotificationsFor(check, browser, site, treatment))
        {
            _tray.Notify(title, text, important);
            LogService.Info("notify", $"{title}: {text}");
        }
    }

    /// <summary>
    /// What to pop up for a result. Inspection: whenever a site is seen inspected and hasn't been
    /// announced this session. Also: inspection stopped, a different inspecting CA, traffic moving to
    /// another interface, certificate changes, and mTLS sites.
    /// </summary>
    private IEnumerable<(string Title, string Text, bool Important)> NotificationsFor(SiteCheck check, string browser, string site, Treatment treatment)
    {
        var c = check.Classification;
        var change = check.Observation.Change;
        var path = VerdictStyle.Short(treatment, c.Verdict);
        var key = $"{browser}|{site}";

        if (!PathTreatment.IsInspected(treatment)) _announcedInspected.Remove(key);
        else if (_announcedInspected.Add(key))
            yield return (change == ChangeKind.InspectionStarted ? $"{browser} · certificate changed for inspection" : $"{browser} · inspection found",
                          $"{site}: {path}{(c.Vendor is null ? "" : $" by {c.Vendor}")}. Issuer: {c.IssuerName}", true);
        else if (change == ChangeKind.CertificateChanged)
            yield return ($"{browser} · inspecting CA changed", $"{site} is now inspected via {c.IssuerName}.", true);

        switch (change)
        {
            case ChangeKind.InspectionStopped:
                yield return ($"{browser} · inspection stopped", $"{site} now shows its original certificate ({c.IssuerName}). {path}.", false);
                break;
            case ChangeKind.EgressChanged:
                yield return ($"{browser} · traffic moved", $"{site} now leaves via {check.Egress?.InterfaceLabel}: {path}.", true);
                break;
            case ChangeKind.CertificateChanged when !PathTreatment.IsInspected(treatment):
            case ChangeKind.VerdictChanged:
                yield return ($"{browser} · certificate changed", $"{site}: {c.Reason}", false);
                break;
        }

        if (c.MutualTls && change == ChangeKind.FirstSeen)
            yield return ($"{browser} · mTLS site", $"{site} asks for a client certificate, so it can't be inspected and needs a bypass rule.", true);
    }

    // ---- header actions ----

    private void ClearEverything()
    {
        var answer = MessageBox.Show(this,
            "Clear everything?\n\n• The history for all sites in every browser\n• The log, including any log files\n\nThis can't be undone.",
            "CertWatch", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _history.DeleteAll();
        LogService.Clear();
        _announcedInspected.Clear();
        foreach (var session in _sessions) session.OnHistoryCleared();
        LogService.Info("app", "Cleared all history and the log");
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        ThemeManager.Set(ThemeManager.Next());
        _settings.Theme = ThemeManager.Mode;
        _settings.Save();
        _shell.ThemeText = $"Theme: {ThemeManager.Mode}";
    }

    private void OnLog(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow((debug, file) => { _settings.DebugLogging = debug; _settings.LogToFile = file; _settings.Save(); }) { Owner = this };
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }
        else _logWindow.Activate();
    }

    // Only our own https links (repo, release notes) are opened.
    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps && e.Uri.AbsoluteUri.StartsWith(ShellViewModel.Repository, StringComparison.Ordinal))
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
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
        foreach (var s in _sessions.ToList()) s.Dispose();
        _tray.Dispose();
        _history.Dispose();
        _logWindow?.Close();
        Close();
        Application.Current.Shutdown();
    }
}
