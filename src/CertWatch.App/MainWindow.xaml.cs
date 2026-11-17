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
/// One window, one tab per browser. Only browsers the user ticked in "Browsers…" (or named with
/// --browser/--process) are monitored: each gets a tab once it's running, and the tab stays after the
/// browser closes so its history remains visible. Unticked browsers' address bars are never read.
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
    // Catalog ids the user ticked; saved in settings.json. Empty until the first choice.
    private readonly HashSet<string> _monitored = new(StringComparer.OrdinalIgnoreCase);
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
        _popups.OpenRequested += OpenPopupEntry;
        _popups.Lifetime = PopupNotifier.LifetimeFor(_settings.PopupSeconds);
        _shell.PopupText = PopupNotifier.Describe(_settings.PopupSeconds);
        _tray.TestNotificationRequested += () =>
        {
            _popups.Show(new PopupContent("CertWatch test pop-up",
                "Pop-ups like this appear when a site is steered to ZTNA, inspected, or changes.", StatusKind.Steered));
            LogService.Info("notify", "Test pop-up shown");
        };
        _tray.SetState(StatusKind.Neutral, "CertWatch: waiting for a browser");

        _monitored.UnionWith(BrowserSelection.Normalize(_settings.MonitoredBrowsers));
        // Browsers named on the command line are an explicit choice for this run (not saved).
        foreach (var b in options.Browsers) AddSession(b);
        UpdateEmptyState();
        // First run: nothing is monitored yet, so ask straight away (several can be ticked before Apply).
        if (_settings.MonitoredBrowsers is null && options.Browsers.Count == 0)
        {
            LogService.Info("app", "First run: asking which browsers to monitor");
            ContentRendered += (_, _) => Dispatcher.BeginInvoke(ChooseBrowsers);
        }
        ScanForBrowsers();
        _browserScan.Tick += (_, _) => ScanForBrowsers();
        _browserScan.Start();
    }

    private void ScanForBrowsers()
    {
        foreach (var browser in BrowserCatalog.All)
        {
            if (!_monitored.Contains(browser.Id) || _sessions.Any(s => s.Browser.Id == browser.Id)) continue;
            if (BrowserSession.IsRunning(browser)) AddSession(browser);
        }
    }

    /// <summary>Opens the picker; applies the whole selection at once, so tabs only change after Apply.</summary>
    private void ChooseBrowsers()
    {
        var choices = BrowserCatalog.All.Select(b =>
            new BrowserChoice(b.Id, b.DisplayName,
                _monitored.Contains(b.Id) || _sessions.Any(s => s.Browser.Id == b.Id), // includes --browser ones
                BrowserSession.IsRunning(b)));
        var picker = new BrowserPickerWindow(choices) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } selected) return;
        ApplySelection(selected);
    }

    private void ApplySelection(IReadOnlyList<string> selected)
    {
        var chosen = BrowserSelection.Normalize(selected);
        _monitored.Clear();
        _monitored.UnionWith(chosen);
        _settings.MonitoredBrowsers = chosen.ToList();
        _settings.Save();
        LogService.Info("app", chosen.Count == 0 ? "Monitoring no browsers" : $"Monitoring {string.Join(", ", chosen)}");

        foreach (var session in _sessions.Where(s => BrowserCatalog.Find(s.Browser.Id) is not null && !_monitored.Contains(s.Browser.Id)).ToList())
            RemoveSession(session);
        ScanForBrowsers();
        UpdateEmptyState();
    }

    /// <summary>Stops reading that browser's address bar and removes its tab. Its history stays saved.</summary>
    private void RemoveSession(BrowserSession session)
    {
        session.Checked -= OnChecked;
        // Stopping the follower waits for its poll to finish; keep that wait off the UI thread.
        _ = Task.Run(session.Dispose);
        _sessions.Remove(session);
        var prefix = $"{session.Browser.DisplayName}|";
        _announcedInspected.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        _announcedSteered.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        var wasSelected = _shell.SelectedTab == session.View;
        _shell.Tabs.Remove(session.View);
        if (wasSelected) _shell.SelectedTab = _shell.Tabs.FirstOrDefault();
        _shell.HasTabs = _shell.Tabs.Count > 0;
        if (!_shell.HasTabs) _tray.SetState(StatusKind.Neutral, "CertWatch: waiting for a browser");
    }

    private void UpdateEmptyState()
    {
        _shell.AnyChosen = _monitored.Count > 0 || _sessions.Count > 0;
        var names = BrowserCatalog.All.Where(b => _monitored.Contains(b.Id)).Select(b => b.DisplayName).ToList();
        _shell.EmptyText = names.Count == 0
            ? "No browsers are monitored yet. Choose which ones CertWatch watches; it reads the address bar of those browsers only."
            : $"Monitoring {string.Join(", ", names)}. None of them is running: open one and its tab appears here.";
    }

    private void OnBrowsers(object sender, RoutedEventArgs e) => ChooseBrowsers();

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
            _popups.Show(new PopupContent(title, text, status, session.Browser.Id, check.Probe.Endpoint));
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

    /// <summary>A pop-up was clicked: bring CertWatch forward on that browser's tab with the site selected.</summary>
    private void OpenPopupEntry(PopupContent popup)
    {
        ShowFromTray();
        if (popup.Site is not { } site || _sessions.FirstOrDefault(s => s.Browser.Id == popup.BrowserId) is not { } session) return;
        _shell.SelectedTab = session.View;
        // After the tab switch has rebound the tables, so the grid doesn't push its old selection back.
        Dispatcher.BeginInvoke(() => session.View.ShowSite(site), System.Windows.Threading.DispatcherPriority.Background);
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
