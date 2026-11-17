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
/// One window, one tab per browser. Browsers are added automatically
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
    private readonly PopupNotifier _popups = new();
    private readonly WindowsRouteLookup _routes = new();
    private readonly List<BrowserSession> _sessions = [];
    private readonly DispatcherTimer _browserScan = new() { Interval = TimeSpan.FromSeconds(3) };
    // Browser|site pairs already announced as inspected this session, so each gets one pop-up.
    private readonly HashSet<string> _announcedInspected = [];
    // Browser|site pairs already announced as steered to ZTNA (not inspected) this session.
    private readonly HashSet<string> _announcedSteered = [];
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

        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray); // also raised by clicking a notification
        _tray.RecheckRequested += () => SessionFor(_shell.SelectedTab)?.RecheckNow();
        _tray.ExitRequested += ExitApp;
        _popups.OpenRequested += ShowFromTray;
        _popups.Lifetime = PopupNotifier.LifetimeFor(_settings.PopupSeconds);
        _shell.PopupText = PopupNotifier.Describe(_settings.PopupSeconds);
        _tray.TestNotificationRequested += () =>
        {
            _popups.Show(new PopupContent("CertWatch test pop-up",
                "Pop-ups like this appear when a site is steered to ZTNA, inspected, or changes.", StatusKind.Steered));
            LogService.Info("notify", "Test pop-up shown");
        };
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

    private BrowserSession AddSession(BrowserDefinition browser)
    {
        var existing = _sessions.FirstOrDefault(s => s.Browser.Id == browser.Id);
        if (existing is not null) return existing;
        var session = new BrowserSession(browser, _probe, _classifier, _history, _options.RecheckInterval, Dispatcher, _routes);
        session.Checked += OnChecked;
        _sessions.Add(session);
        _shell.Tabs.Add(session.View);
        _shell.SelectedTab ??= session.View;
        _shell.HasTabs = true;
        return session;
    }

    private BrowserSession? SessionFor(BrowserTabViewModel? tab) => _sessions.FirstOrDefault(s => s.View == tab);

    // ---- results: tray and notifications ----

    private void OnChecked(BrowserSession session, SiteCheck check)
    {
        var c = check.Classification;
        var browser = session.Browser.DisplayName;
        var site = check.Probe.Endpoint.ToString();
        var treatment = PathTreatment.Of(c.Verdict, check.Egress?.Kind.ToString());
        var status = Status.Of(treatment, c.Verdict);
        _tray.SetState(status, $"CertWatch · {browser} · {site}: {VerdictStyle.Short(treatment, c.Verdict)}");

        foreach (var (title, text) in NotificationsFor(check, browser, site, treatment))
        {
            _popups.Show(new PopupContent(title, text, status));
            LogService.Info("notify", $"{title}: {text}");
        }
    }

    /// <summary>
    /// What to pop up for a result. Inspection: whenever a site is seen inspected and hasn't been
    /// announced this session. Steering: the same, for a site steered to ZTNA without inspection.
    /// Also: inspection stopped, a different inspecting CA, traffic moving to
    /// another interface, certificate changes, and mTLS sites.
    /// </summary>
    private IEnumerable<(string Title, string Text)> NotificationsFor(SiteCheck check, string browser, string site, Treatment treatment)
    {
        var c = check.Classification;
        var change = check.Observation.Change;
        var path = VerdictStyle.Short(treatment, c.Verdict);
        var key = $"{browser}|{site}";

        if (!PathTreatment.IsInspected(treatment)) _announcedInspected.Remove(key);
        else if (_announcedInspected.Add(key))
            yield return (change == ChangeKind.InspectionStarted ? $"{browser} · certificate changed for inspection" : $"{browser} · inspection found",
                          $"{site}: {path}{(c.Vendor is null ? "" : $" by {c.Vendor}")}. Issuer: {c.IssuerName}");
        else if (change == ChangeKind.CertificateChanged)
            yield return ($"{browser} · inspecting CA changed", $"{site} is now inspected via {c.IssuerName}.");

        if (!PathTreatment.IsSteered(treatment) || PathTreatment.IsInspected(treatment)) _announcedSteered.Remove(key);
        else if (_announcedSteered.Add(key) && change != ChangeKind.EgressChanged) // a move has its own pop-up below
            yield return ($"{browser} · steered to ZTNA", $"{site} leaves via {check.Egress?.InterfaceLabel ?? "the ZTNA tunnel"}: {path}.");

        switch (change)
        {
            case ChangeKind.InspectionStopped:
                yield return ($"{browser} · inspection stopped", $"{site} now shows its original certificate ({c.IssuerName}). {path}.");
                break;
            case ChangeKind.EgressChanged:
                yield return ($"{browser} · traffic moved", $"{site} now leaves via {check.Egress?.InterfaceLabel}: {path}.");
                break;
            case ChangeKind.CertificateChanged when !PathTreatment.IsInspected(treatment):
            case ChangeKind.VerdictChanged:
                yield return ($"{browser} · certificate changed", $"{site}: {c.Reason}");
                break;
        }

        if (c.MutualTls && change == ChangeKind.FirstSeen)
            yield return ($"{browser} · mTLS site", $"{site} asks for a client certificate, so it can't be inspected and needs a bypass rule.");
    }

    // ---- header actions ----

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Clear everything?\n\n• The history for all sites in every browser\n• The log, including any log files\n\nThis can't be undone.",
            "CertWatch", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _history.DeleteAll();
        LogService.Clear();
        _announcedInspected.Clear();
        _announcedSteered.Clear();
        foreach (var session in _sessions) session.OnHistoryCleared();
        LogService.Info("app", "Cleared all history and the log");
    }

    private void OnPopupDuration(object sender, RoutedEventArgs e)
    {
        _settings.PopupSeconds = PopupNotifier.Next(_settings.PopupSeconds);
        _settings.Save();
        _popups.Lifetime = PopupNotifier.LifetimeFor(_settings.PopupSeconds);
        _shell.PopupText = PopupNotifier.Describe(_settings.PopupSeconds);
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
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
        _popups.CloseAll();
        _tray.Dispose();
        _history.Dispose();
        _logWindow?.Close();
        Close();
        Application.Current.Shutdown();
    }
}
