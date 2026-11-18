using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Navigation;
using System.Windows.Threading;
using CertWatch.App.Docking;
using CertWatch.App.Logging;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.App.Views;
using CertWatch.Core.Browsers;
using CertWatch.Core.Docking;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App;

/// <summary>
/// One window, one tab per browser. Only browsers the user chose in "Browsers…" (or named with
/// --browser/--process) are monitored. Each chosen browser has a tab whether or not it's running, so
/// its history is always visible. Browsers that weren't chosen are never read.
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
    // Browser|site pairs already announced as inspected this session, so each gets one pop-up.
    private readonly HashSet<string> _announcedInspected = [];
    // Browser|site pairs already announced as steered to ZTNA (not inspected) this session.
    private readonly HashSet<string> _announcedSteered = [];
    private LogWindow? _logWindow;
    private DockController? _dock;
    // Set while the foreground browser changes the selected tab, so the dock keeps that exact window.
    private bool _selectingFromForeground;
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
        _shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.SelectedTab)) OnSelectedTabChanged(); };
        SourceInitialized += (_, _) => InitDocking();
        // Minimised from the taskbar while docked: don't leave a reserved, empty strip behind.
        StateChanged += (_, _) =>
        {
            if (_dock is null || !_dock.IsDocked) return;
            if (WindowState == WindowState.Minimized) _dock.Suspend();
            else _dock.Resume();
        };
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
        AddChosenSessions();
    }

    /// <summary>A tab for every chosen browser, in catalog order, running or not.</summary>
    private void AddChosenSessions()
    {
        foreach (var browser in BrowserCatalog.All)
        {
            if (_monitored.Contains(browser.Id)) AddSession(browser); // no-op if it already has a tab
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
        AddChosenSessions();
        UpdateEmptyState();
    }

    /// <summary>Stops reading that browser's address bar and removes its tab. Its history stays saved.</summary>
    private void RemoveSession(BrowserSession session)
    {
        session.Checked -= OnChecked;
        session.View.ViewerToggleRequested -= SetViewerReading;
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
        // Only reachable with nothing chosen: every chosen browser has a tab.
        _shell.EmptyText = "No browsers are monitored yet. Choose which ones CertWatch watches; it reads the address bar of those browsers only.";
    }

    private void OnBrowsers(object sender, RoutedEventArgs e) => ChooseBrowsers();

    private BrowserSession AddSession(BrowserDefinition browser)
    {
        var existing = _sessions.FirstOrDefault(s => s.Browser.Id == browser.Id);
        if (existing is not null) return existing;
        var session = new BrowserSession(browser, _probe, _classifier, _history, _options.RecheckInterval, Dispatcher, _routes);
        session.Checked += OnChecked;
        session.View.ViewerReadingEnabled = _settings.ReadCertificateViewer;
        session.View.ViewerToggleRequested += SetViewerReading;
        _sessions.Add(session);
        _shell.Tabs.Add(session.View);
        _shell.SelectedTab ??= session.View;
        _shell.HasTabs = true;
        return session;
    }

    private BrowserSession? SessionFor(BrowserTabViewModel? tab) => _sessions.FirstOrDefault(s => s.View == tab);

    // ---- docking (DESIGN.md "Docked layouts") ----

    private void InitDocking()
    {
        _dock = new DockController(this, () => DockTargetFor(SessionFor(_shell.SelectedTab)));
        _dock.Attach();
        _dock.ForegroundChanged += OnForegroundChanged;
        _dock.HintChanged += hint => _shell.DockHint = hint ?? "";
        _dock.KeepOnTop = _settings.KeepOnTop;
        _settings.Dock = DockLayout.Validate(_settings.Dock);
        _settings.DockAnchor = DockLayout.Validate(_settings.DockAnchor);
        if (_settings.Dock != DockPosition.Floating) ApplyDock(_settings.Dock, _settings.DockAnchor);
    }

    /// <summary>The browser window to dock beside for a session: its main window only, never a dialog or pop-up.</summary>
    private static IntPtr DockTargetFor(BrowserSession? session) =>
        session?.Window is { } w && DockController.IsDockable(w) ? w : IntPtr.Zero;

    /// <summary>Switches layout and window chrome, then lets the controller place the window. Saved by the caller.</summary>
    private void ApplyDock(DockPosition position, DockAnchor anchor)
    {
        if (_dock is null) return;
        var wasFloating = _shell.IsFloating;
        // Remember the floating window's place so undocking puts it back.
        if (wasFloating && position != DockPosition.Floating && IsVisible) WindowPlacement.Save(this);

        if (position == DockPosition.Floating)
        {
            _dock.Apply(DockPosition.Floating, anchor); // gives back a reserved screen edge first
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 720;
            MinHeight = 480;
            _shell.Dock = DockPosition.Floating;
            if (!wasFloating) WindowPlacement.Apply(this);
        }
        else
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 0;
            MinHeight = 0;
            _shell.Dock = position;
            _popups.CloseAll(); // pop-ups are off while docked
            _dock.Target = DockTargetFor(SessionFor(_shell.SelectedTab));
            _dock.Apply(position, anchor);
        }
        LogService.Info("app", position == DockPosition.Floating ? "Floating window" : $"Docked {position} ({anchor})");
    }

    private void SetDock(DockPosition position, DockAnchor anchor)
    {
        _settings.Dock = position;
        _settings.DockAnchor = anchor;
        _settings.Save();
        ApplyDock(position, anchor);
    }

    /// <summary>A window of another app took focus. If it's a monitored browser, follow it.</summary>
    private void OnForegroundChanged(IntPtr window)
    {
        var name = BrowserSession.ProcessNameOf(window); // one process lookup, however many browsers are monitored
        var session = _sessions.FirstOrDefault(s => s.OwnsProcess(name));
        if (session is null) return;
        if (_settings.FollowActiveBrowser && _shell.SelectedTab != session.View)
        {
            _selectingFromForeground = true;
            try { _shell.SelectedTab = session.View; }
            finally { _selectingFromForeground = false; }
        }
        // The browser's certificate viewer is never the window to dock beside, whatever its window style.
        if (CertViewerText.HostFromTitle(Native.WindowTitle(window)) is { } viewerHost)
        {
            if (_settings.ReadCertificateViewer) TryReadCertViewer(session, window, viewerHost);
            return;
        }
        if (_dock is not null && _shell.SelectedTab == session.View && DockController.IsDockable(window)) _dock.Target = window;
    }

    /// <summary>Offered on the card when a browser has its own path; saved, and applied to every tab.</summary>
    private void SetViewerReading(bool on)
    {
        _settings.ReadCertificateViewer = on;
        _settings.Save();
        foreach (var tab in _shell.Tabs) tab.ViewerReadingEnabled = on;
        LogService.Info("app", on ? "Reading browsers' certificate viewers when opened" : "Stopped reading certificate viewers");
    }

    /// <summary>
    /// The browser's certificate viewer (found by its title) came forward: read its fingerprint and
    /// issuer on a background thread (UI Automation must stay off the UI thread).
    /// </summary>
    private void TryReadCertViewer(BrowserSession session, IntPtr window, string host)
    {
        Task.Run(() => CertViewerReader.Read(window, host)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } reading)
                Dispatcher.BeginInvoke(() => { if (_sessions.Contains(session)) session.ApplyViewerReading(reading); });
        }, TaskScheduler.Default);
    }

    private void OnSelectedTabChanged()
    {
        if (_dock is null || _selectingFromForeground) return;
        var session = SessionFor(_shell.SelectedTab);
        // Keep the window already followed when it belongs to the newly selected browser.
        if (session is not null && session.Owns(_dock.Target)) return;
        _dock.Target = DockTargetFor(session);
    }

    private void OnDockMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Position(string header, DockPosition p) => menu.Items.Add(Item(header, _settings.Dock == p, () => SetDock(p, _settings.DockAnchor)));
        void Anchor(string header, DockAnchor a) => menu.Items.Add(Item(header, _settings.DockAnchor == a, () => SetDock(_settings.Dock, a)));

        Position("_Floating window", DockPosition.Floating);
        Position("_Right of the browser", DockPosition.Right);
        Position("_Left of the browser", DockPosition.Left);
        Position("_Above the browser", DockPosition.Top);
        menu.Items.Add(new Separator());
        Anchor("Docked to the _browser window (moves with it)", DockAnchor.Browser);
        Anchor("Docked to the _screen edge (reserves space)", DockAnchor.ScreenEdge);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("F_ollow whichever browser is active", _settings.FollowActiveBrowser, () =>
        {
            _settings.FollowActiveBrowser = !_settings.FollowActiveBrowser;
            _settings.Save();
        }));
        var onTop = Item("_Keep on top", _settings.KeepOnTop, () =>
        {
            _settings.KeepOnTop = !_settings.KeepOnTop;
            _settings.Save();
            if (_dock is not null) _dock.KeepOnTop = _settings.KeepOnTop;
        });
        // A screen-edge dock is always on top, like the taskbar.
        onTop.IsEnabled = !(_settings.Dock != DockPosition.Floating && _settings.DockAnchor == DockAnchor.ScreenEdge);
        menu.Items.Add(onTop);
        Open(menu, sender);
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("_Browsers…", false, ChooseBrowsers));
        menu.Items.Add(Item("Clear _all…", false, () => OnClearAll(this, new RoutedEventArgs())));
        menu.Items.Add(Item("_Log", false, () => OnLog(this, new RoutedEventArgs())));
        // Pop-ups are off while docked; the duration still applies once CertWatch floats again.
        menu.Items.Add(new MenuItem { Header = "Pop-ups: off while docked", IsEnabled = false });
        menu.Items.Add(Item(_shell.ThemeText, false, () => OnTheme(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Read the browser's _certificate viewer", _settings.ReadCertificateViewer, () => SetViewerReading(!_settings.ReadCertificateViewer)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("_GitHub", false, () => OpenOwnLink(_shell.RepoUrl)));
        menu.Items.Add(Item($"_Release notes ({_shell.VersionText})", false, () => OpenOwnLink(_shell.ReleaseNotesUrl)));
        Open(menu, sender);
    }

    private void OnBrowserMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var tab in _shell.Tabs)
        {
            var t = tab;
            menu.Items.Add(Item($"{t.Name} · {t.TabStatus}", _shell.SelectedTab == t, () => _shell.SelectedTab = t));
        }
        Open(menu, sender);
    }

    private static MenuItem Item(string header, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void Open(ContextMenu menu, object anchor)
    {
        menu.PlacementTarget = anchor as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- results: tray and notifications ----

    private void OnChecked(BrowserSession session, SiteCheck check, BrowserPathResult path)
    {
        var c = check.Classification;
        var browser = session.Browser.DisplayName;
        var site = check.Probe.Endpoint.ToString();
        var treatment = PathTreatment.Of(c.Verdict, check.Egress?.Kind.ToString());
        var status = Status.Of(treatment, c.Verdict);
        if (path.Match == BrowserPathMatch.Different)
        {
            // CertWatch's result isn't the browser's: no inspected/steered tray colour or pop-up for it.
            _tray.SetState(StatusKind.Neutral, $"CertWatch · {browser} · {site}: browser uses its own path");
            return;
        }
        _tray.SetState(status, $"CertWatch · {browser} · {site}: {VerdictStyle.Short(treatment, c.Verdict)}");

        // Docked, the verdict is already on screen beside the browser, so pop-ups stay off (still logged).
        var docked = !_shell.IsFloating;
        foreach (var (title, text) in NotificationsFor(check, browser, site, treatment))
        {
            if (!docked) _popups.Show(new PopupContent(title, text, status, session.Browser.Id, check.Probe.Endpoint));
            LogService.Info("notify", docked ? $"{title}: {text} (docked: no pop-up)" : $"{title}: {text}");
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

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        OpenOwnLink(e.Uri);
        e.Handled = true;
    }

    // Only our own https links (repo, release notes) are opened.
    private static void OpenOwnLink(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps && uri.AbsoluteUri.StartsWith(ShellViewModel.Repository, StringComparison.Ordinal))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
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
        _dock?.Resume();
        Activate();
    }

    // Closing the window keeps CertWatch running in the tray; Exit is in the tray menu.
    protected override void OnClosing(CancelEventArgs e)
    {
        // Docked bounds belong to the dock; only the floating window's place is remembered.
        if (_shell.IsFloating) WindowPlacement.Save(this);
        if (!_exiting)
        {
            e.Cancel = true;
            _dock?.Suspend();
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        if (_shell.IsFloating) WindowPlacement.Save(this);
        _exiting = true;
        _dock?.Dispose(); // gives back a reserved screen edge
        foreach (var s in _sessions.ToList()) s.Dispose();
        _popups.CloseAll();
        _tray.Dispose();
        _history.Dispose();
        _logWindow?.Close();
        Close();
        Application.Current.Shutdown();
    }
}
