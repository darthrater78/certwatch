using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;

namespace CertWatch.App.Presentation;

/// <summary>
/// One browser tab: the live card, every site seen (once each) and the selected site's history.
/// Clicking a site or a history entry shows that saved
/// result on the card; "Back to live" (or the browser moving to a new site) returns to live.
/// </summary>
public sealed class BrowserTabViewModel : Observable
{
    private SiteEndpoint? _liveSite;
    private SiteCheck? _lastLive;
    private BrowserPathResult _lastPath = BrowserPathResult.Unknown;
    private CertViewerReading? _viewer;
    // A viewer reading describes what the user looked at; after this long it's stale.
    private static readonly TimeSpan ViewerReadingLifetime = TimeSpan.FromMinutes(15);

    public BrowserTabViewModel(BrowserDefinition browser)
    {
        Browser = browser;
        Card = new VerdictCard(browser.DisplayName);

        SitesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Sites);
        SitesView.SortDescriptions.Add(new SortDescription(nameof(SiteRow.LastSeen), ListSortDirection.Descending));
        SitesView.IsLiveSorting = true;
        SitesView.LiveSortingProperties.Add(nameof(SiteRow.LastSeen));
        SitesView.Filter = o => _filter.Length == 0 || ((SiteRow)o).Host.Contains(_filter, StringComparison.OrdinalIgnoreCase);

        RecheckCommand = new RelayCommand(() => RecheckRequested?.Invoke());
        CopyFingerprintCommand = new RelayCommand(() => { if (Card.Fingerprint.Length > 0) System.Windows.Clipboard.SetText(Card.Fingerprint); });
        ExportRootCommand = new RelayCommand(() => StatusMessage = RootExporter.Export(Card.Chain, Card.Site) ?? StatusMessage);
        ClearSiteCommand = new RelayCommand(() => { if (SelectedSite is { } row) ClearSiteRequested?.Invoke(row); });
        ClearBrowserCommand = new RelayCommand(() => ClearBrowserRequested?.Invoke());
        BackToLiveCommand = new RelayCommand(BackToLive);
        ToggleViewerCommand = new RelayCommand(() => ViewerToggleRequested?.Invoke(!_viewerEnabled));
    }

    public BrowserDefinition Browser { get; }
    public string Name => Browser.DisplayName;
    public VerdictCard Card { get; }

    public ObservableCollection<SiteRow> Sites { get; } = [];
    public ListCollectionView SitesView { get; }
    public ObservableCollection<HistoryRow> History { get; } = [];

    public ICommand RecheckCommand { get; }
    public ICommand CopyFingerprintCommand { get; }
    public ICommand ExportRootCommand { get; }
    public ICommand ClearSiteCommand { get; }
    public ICommand ClearBrowserCommand { get; }
    public ICommand BackToLiveCommand { get; }
    /// <summary>Turns reading the browser's certificate viewer on or off (offered when the browser has its own path).</summary>
    public ICommand ToggleViewerCommand { get; }
    public event Action<bool>? ViewerToggleRequested;

    private bool _ownPath;
    /// <summary>The live site's check found the browser on its own path: the card offers the certificate viewer.</summary>
    public bool OwnPathDetected
    {
        get => _ownPath;
        private set { if (Set(ref _ownPath, value)) OnChanged(nameof(ViewerButtonText)); }
    }

    private bool _viewerEnabled;
    /// <summary>Mirrors the saved setting; the shell sets it on every tab.</summary>
    public bool ViewerReadingEnabled
    {
        get => _viewerEnabled;
        set
        {
            if (!Set(ref _viewerEnabled, value)) return;
            OnChanged(nameof(ViewerButtonText));
            if (_lastLive is { } live && ShowingLive) ShowLiveCard(live);
        }
    }
    public string ViewerButtonText => _viewerEnabled ? "Stop reading the certificate viewer" : "Compare with the browser's certificate viewer";

    public event Action? RecheckRequested;
    public event Action<SiteRow>? ClearSiteRequested;
    public event Action? ClearBrowserRequested;
    /// <summary>A site was selected; the session loads its history.</summary>
    public event Action<SiteRow?>? SiteSelected;

    // Tab header: dot colour and one-line live status.
    private StatusKind _status;
    public StatusKind Status { get => _status; private set => Set(ref _status, value); }
    private string _tabStatus = "Waiting";
    public string TabStatus { get => _tabStatus; set => Set(ref _tabStatus, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _cardNote = "";
    /// <summary>Set when the card shows a saved result instead of the live tab.</summary>
    public string CardNote
    {
        get => _cardNote;
        private set
        {
            if (!Set(ref _cardNote, value)) return;
            OnChanged(nameof(ShowingSaved));
            OnChanged(nameof(ShowingLive));
        }
    }
    public bool ShowingSaved => _cardNote.Length > 0;
    public bool ShowingLive => !ShowingSaved;


    private string _filter = "";
    public string SiteFilter
    {
        get => _filter;
        set { if (Set(ref _filter, value ?? "")) SitesView.Refresh(); }
    }
    public string SiteCount => $"{Sites.Count} seen in {Name}";

    private SiteRow? _selectedSite;
    public SiteRow? SelectedSite
    {
        get => _selectedSite;
        set
        {
            // Tabs share one view; when it switches tabs the grid briefly pushes null back. Keep the selection.
            if (value is null && _selectedSite is not null && Sites.Contains(_selectedSite)) return;
            if (!Set(ref _selectedSite, value)) return;
            OnChanged(nameof(HistoryTitle));
            SiteSelected?.Invoke(value); // loads history, which shows a saved result for non-live sites
            if (value is not null && value.Endpoint == _liveSite && _lastLive is { } live) ShowLiveCard(live);
        }
    }
    public string HistoryTitle => _selectedSite is null ? "History" : $"History · {_selectedSite.Host}";

    private HistoryRow? _selectedHistory;
    public HistoryRow? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (!Set(ref _selectedHistory, value) || value is null) return;
            var row = _selectedSite;
            Card.ShowSaved(value.Observation, PastInspection(row));
            CardNote = $"Showing the saved result from {value.Time} for {value.Observation.Host}. The browser is on {LiveText}.";
        }
    }

    private string LiveText => _liveSite?.ToString() ?? "no HTTPS page";

    // ---- live updates from the session ----

    public void ShowNoSite()
    {
        _liveSite = null;
        _lastLive = null;
        CardNote = "";
        Status = StatusKind.Neutral;
        OwnPathDetected = false;
        TabStatus = "No HTTPS page";
        Card.ShowWaiting();
    }

    /// <summary>The browser is chosen but has no window open; its history stays browsable.</summary>
    public void ShowNotRunning()
    {
        _liveSite = null;
        _lastLive = null;
        CardNote = "";
        Status = StatusKind.Neutral;
        OwnPathDetected = false;
        TabStatus = "Not running";
        Card.ShowWaiting($"Start {Browser.DisplayName} and CertWatch follows its active tab.", $"{Browser.DisplayName} isn't running");
    }

    public void ShowChecking(SiteEndpoint site)
    {
        _liveSite = site;
        _lastLive = null;
        _lastPath = BrowserPathResult.Unknown;
        OwnPathDetected = false;
        CardNote = "";
        SelectRow(Sites.FirstOrDefault(r => r.Endpoint == site));
        TabStatus = $"{site} · checking";
        Card.ShowChecking(site);
    }

    /// <summary>A live check of the browser's current site.</summary>
    public void ShowCheck(SiteCheck check, BrowserPathResult? path = null)
    {
        _liveSite = check.Probe.Endpoint;
        _lastLive = check;
        _lastPath = path ?? BrowserPathResult.Unknown;
        OwnPathDetected = _lastPath.Match == BrowserPathMatch.Different;
        var row = UpdateRow(check);
        row.SetBrowserPath(_lastPath.Match);
        if (_selectedSite is null) SelectRow(row);
        Status = row.Status;
        TabStatus = $"{check.Probe.Endpoint} · {row.Verdict}";
        if (_selectedSite is null || _selectedSite.Endpoint == check.Probe.Endpoint) ShowLiveCard(check);
    }

    /// <summary>Updates (or adds) a site's row without touching the card: also used for background tabs.</summary>
    public SiteRow UpdateRow(SiteCheck check)
    {
        var o = check.Observation;
        StatusMessage = "";
        var row = Sites.FirstOrDefault(s => s.Endpoint == check.Probe.Endpoint);
        if (row is null)
        {
            row = new SiteRow(check.Probe.Endpoint);
            row.Apply(o, o.Change is ChangeKind.None or ChangeKind.FirstSeen ? 0 : 1, null);
            Sites.Add(row);
            OnChanged(nameof(SiteCount));
        }
        else
        {
            row.Update(o);
        }
        if (row == _selectedSite && o.Change != ChangeKind.None) History.Insert(0, new HistoryRow(o));
        return row;
    }

    private void ShowLiveCard(SiteCheck check)
    {
        CardNote = "";
        var viewer = ViewerFor(check);
        var hint = _ownPath && _viewerEnabled && viewer is null
            ? $"To see {Name}'s own certificate, open its certificate viewer for {check.Probe.Endpoint.Host} (padlock → certificate). CertWatch reads it when it opens (read-only) and compares it here."
            : null;
        Card.ShowLive(check, PastInspection(Sites.FirstOrDefault(r => r.Endpoint == check.Probe.Endpoint)), _lastPath, viewer, hint);
    }

    private CertViewerReading? ViewerFor(SiteCheck check) =>
        _viewer is { } v && v.Host == check.Probe.Endpoint.Host && DateTimeOffset.Now - v.ReadAt < ViewerReadingLifetime ? v : null;

    /// <summary>The user opened this browser's certificate viewer; show it on the live card when it's for the live site.</summary>
    public void SetViewerReading(CertViewerReading reading)
    {
        _viewer = reading;
        if (_lastLive is { } live && live.Probe.Endpoint.Host == reading.Host && ShowingLive
            && (_selectedSite is null || _selectedSite.Endpoint == live.Probe.Endpoint))
            ShowLiveCard(live);
    }

    private void BackToLive()
    {
        var row = _liveSite is { } live ? Sites.FirstOrDefault(r => r.Endpoint == live) : null;
        _selectedHistory = null;
        OnChanged(nameof(SelectedHistory));
        if (row is not null && row != _selectedSite) SelectedSite = row;
        else if (_lastLive is { } l) ShowLiveCard(l);
    }

    private static string? PastInspection(SiteRow? row) => row?.LastInspected is { } at
        ? $"Was inspected {at.LocalDateTime:MMM d HH:mm}{(row.InspectedBy is null ? "" : $" via {row.InspectedBy}")}. Not inspected right now."
        : null;

    // ---- loading ----

    public void LoadSites(IEnumerable<SiteSummary> summaries)
    {
        _selectedSite = null;
        History.Clear();
        Sites.Clear();
        foreach (var s in summaries)
        {
            var row = new SiteRow(new SiteEndpoint(s.Latest.Host, s.Latest.Port));
            row.SetInspectionHistory(s.LastInspected, s.InspectedBy);
            row.Apply(s.Latest, s.ChangeCount, s.ChangeCount > 0 ? s.Latest.Timestamp : null);
            Sites.Add(row);
        }
        OnChanged(nameof(SiteCount));
    }

    public void LoadSiteHistory(IEnumerable<Observation> newestFirst)
    {
        var list = newestFirst.ToList();
        History.Clear();
        foreach (var o in list) History.Add(new HistoryRow(o));

        // A site picked from the list that isn't the live tab: show its latest saved result on the card.
        if (_selectedSite is { } row && row.Endpoint != _liveSite && list.Count > 0)
        {
            Card.ShowSaved(list[0], PastInspection(row));
            CardNote = $"Showing the saved result for {row.Host} (last seen {list[0].LastSeen.LocalDateTime:MMM d HH:mm:ss}). The browser is on {LiveText}.";
        }
    }

    public void RemoveSite(SiteRow row)
    {
        Sites.Remove(row);
        OnChanged(nameof(SiteCount));
        if (_selectedSite == row) SelectRow(null);
    }

    public void ClearSites()
    {
        CardNote = "";
        Sites.Clear();
        SelectRow(null);
        OnChanged(nameof(SiteCount));
    }

    /// <summary>Selects a row from code (following the live site), bypassing the tab-switch null guard.</summary>
    /// <summary>Selects a site's row (from a pop-up click). A site with no row yet is left alone.</summary>
    public void ShowSite(SiteEndpoint site)
    {
        if (Sites.FirstOrDefault(r => r.Endpoint == site) is { } row) SelectRow(row);
    }

    private void SelectRow(SiteRow? row)
    {
        if (row is not null) { SelectedSite = row; return; }
        if (_selectedSite is null) return;
        _selectedSite = null;
        OnChanged(nameof(SelectedSite));
        OnChanged(nameof(HistoryTitle));
        History.Clear();
    }
}
