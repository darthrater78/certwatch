using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using CertWatch.Core.Browsers;
using CertWatch.Core.Cdp;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>One entry in a site's history (first visit or a change).</summary>
/// <summary>An HTTPS tab open in the demo browser, with its site's current result.</summary>
internal sealed record OpenTabRow(string Title, string Site, string Verdict, Brush VerdictBrush, string Egress);

internal sealed record HistoryRow(string Time, string Verdict, Brush VerdictBrush, string Issuer, string Egress, string Change, string MutualTls);

/// <summary>One site in the "Sites" list. Updated in place so the list never shows a site twice.</summary>
internal sealed class SiteRow : Observable
{
    public SiteRow(SiteEndpoint endpoint) => Endpoint = endpoint;

    public SiteEndpoint Endpoint { get; }
    public string Host => Endpoint.ToString();

    private string _verdict = "";
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }

    private Brush _verdictBrush = Brushes.Gray;
    public Brush VerdictBrush { get => _verdictBrush; private set => Set(ref _verdictBrush, value); }

    private string _issuer = "";
    public string Issuer { get => _issuer; private set => Set(ref _issuer, value); }

    private DateTimeOffset _lastSeen;
    public DateTimeOffset LastSeen { get => _lastSeen; private set { if (Set(ref _lastSeen, value)) OnChanged(nameof(LastSeenText)); } }
    public string LastSeenText => _lastSeen.LocalDateTime.ToString("MMM d HH:mm:ss");

    private string _lastChange = "";
    public string LastChange { get => _lastChange; private set => Set(ref _lastChange, value); }

    private int _changeCount;
    public int ChangeCount { get => _changeCount; private set => Set(ref _changeCount, value); }

    private string _mutualTls = "";
    public string MutualTls { get => _mutualTls; private set => Set(ref _mutualTls, value); }

    private string _egress = "";
    public string Egress { get => _egress; private set => Set(ref _egress, value); }

    /// <summary>When the site was last seen inspected, even if it isn't now.</summary>
    public DateTimeOffset? LastInspected { get; private set; }
    public string? InspectedBy { get; private set; }

    private string _inspection = "";
    /// <summary>"Inspected now", "Was inspected · …", or "Never inspected".</summary>
    public string Inspection { get => _inspection; private set => Set(ref _inspection, value); }

    private Brush _inspectionBrush = Brushes.Gray;
    public Brush InspectionBrush { get => _inspectionBrush; private set => Set(ref _inspectionBrush, value); }

    public void SetInspectionHistory(DateTimeOffset? lastInspected, string? inspectedBy)
    {
        LastInspected = lastInspected;
        InspectedBy = inspectedBy;
    }

    private void RefreshInspection(Verdict current)
    {
        if (current == Core.Trust.Verdict.Inspected)
        {
            Inspection = "Inspected now";
            InspectionBrush = VerdictStyle.Frozen(VerdictStyle.Inspected);
        }
        else if (LastInspected is { } at)
        {
            Inspection = $"Was inspected · {at.LocalDateTime:MMM d HH:mm}{(InspectedBy is null ? "" : " · " + InspectedBy)}";
            InspectionBrush = VerdictStyle.Frozen(VerdictStyle.Inspected);
        }
        else
        {
            Inspection = "Never inspected";
            InspectionBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
        }
    }

    /// <summary>Records an inspected result (call before Apply/Update so the text is right).</summary>
    private void Track(Observation o)
    {
        if (o.Verdict == Core.Trust.Verdict.Inspected)
        {
            var at = o.LastSeen > o.Timestamp ? o.LastSeen : o.Timestamp;
            if (LastInspected is null || at >= LastInspected) { LastInspected = at; InspectedBy = o.Issuer; }
        }
    }

    public void Apply(Observation latest, int changeCount, DateTimeOffset? lastChangeAt)
    {
        Track(latest);
        RefreshInspection(latest.Verdict);
        var t = PathTreatment.Of(latest.Verdict, latest.EgressKind);
        Verdict = VerdictStyle.Short(t, latest.Verdict);
        VerdictBrush = VerdictStyle.Frozen(VerdictStyle.ColorOf(t, latest.Verdict));
        Issuer = latest.Issuer ?? "—";
        LastSeen = latest.LastSeen;
        ChangeCount = changeCount;
        MutualTls = latest.MutualTls ? "mTLS" : "";
        Egress = BrowserTabViewModel.EgressShort(latest);
        var when = (lastChangeAt ?? latest.Timestamp).LocalDateTime.ToString("MMM d HH:mm");
        LastChange = changeCount == 0 ? $"No changes · first seen {when}" : $"{VerdictStyle.Describe(latest.Change) ?? "Changed"} · {when}";
    }

    /// <summary>A re-check or change for this site just came in.</summary>
    public void Update(Observation o)
    {
        Track(o);
        RefreshInspection(o.Verdict);
        var isChange = o.Change is not (ChangeKind.None or ChangeKind.FirstSeen);
        var newCount = ChangeCount + (isChange ? 1 : 0);
        if (isChange || ChangeCount == 0 && o.Change == ChangeKind.FirstSeen)
            Apply(o, newCount, o.Timestamp);
        else
        {
            // No change: keep the last-change text, refresh the live fields.
            var t = PathTreatment.Of(o.Verdict, o.EgressKind);
            Verdict = VerdictStyle.Short(t, o.Verdict);
            VerdictBrush = VerdictStyle.Frozen(VerdictStyle.ColorOf(t, o.Verdict));
            LastSeen = o.LastSeen > o.Timestamp ? o.LastSeen : o.Timestamp;
            Egress = BrowserTabViewModel.EgressShort(o);
        }
    }
}

internal sealed class BrowserTabViewModel : Observable
{
    public BrowserTabViewModel(BrowserDefinition browser)
    {
        // Most recently seen site first, re-sorted live as checks come in (until the user sorts a column).
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Sites);
        view.SortDescriptions.Add(new SortDescription(nameof(SiteRow.LastSeen), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(SiteRow.LastSeen));

        Browser = browser;
        AccentBrush = VerdictStyle.Frozen((Color)ColorConverter.ConvertFromString(browser.AccentHex));
        RecheckCommand = new RelayCommand(() => RecheckRequested?.Invoke());
        CopyFingerprintCommand = new RelayCommand(() =>
        {
            if (Fingerprint is { Length: > 1 } fp) System.Windows.Clipboard.SetText(fp);
        });
        ClearSiteHistoryCommand = new RelayCommand(() =>
        {
            if (SelectedSite is { } row) ClearSiteRequested?.Invoke(row);
        });
        ClearAllHistoryCommand = new RelayCommand(() => ClearAllRequested?.Invoke());
        LaunchDemoCommand = new RelayCommand(() => LaunchDemoRequested?.Invoke());
        ExportRootCommand = new RelayCommand(ExportRoot);
        CanLaunchDemo = browser.Engine == BrowserEngine.Chromium;
    }

    public System.Windows.Input.ICommand LaunchDemoCommand { get; }
    public System.Windows.Input.ICommand ExportRootCommand { get; }

    // Chain of the site currently on the card (leaf first, root last when available).
    private IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> _currentChain = [];
    private string _currentSite = "";

    /// <summary>
    /// Saves the root of the current site's chain (.cer DER or .pem), or the whole chain as one .pem.
    /// For an inspected site this is the inspection root, e.g. to install it on another machine.
    /// </summary>
    private void ExportRoot()
    {
        if (_currentChain.Count == 0)
        {
            System.Windows.MessageBox.Show("Open an HTTPS page first: there's no certificate to export yet.", "CertWatch");
            return;
        }
        var root = _currentChain[^1];
        var isRoot = root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData);
        var name = root.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        var safe = string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = isRoot ? $"Export root: {name}" : $"Export top certificate: {name} (root not available)",
            FileName = string.IsNullOrWhiteSpace(safe) ? "root" : safe,
            Filter = "Root certificate, DER (*.cer)|*.cer|Root certificate, PEM (*.pem)|*.pem|Full chain, PEM (*.pem)|*.pem",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            switch (dialog.FilterIndex)
            {
                case 1:
                    System.IO.File.WriteAllBytes(dialog.FileName, root.RawData);
                    break;
                case 2:
                    System.IO.File.WriteAllText(dialog.FileName, root.ExportCertificatePem() + "\n");
                    break;
                default:
                    System.IO.File.WriteAllText(dialog.FileName,
                        string.Join("\n", _currentChain.Select(c => c.ExportCertificatePem())) + "\n");
                    break;
            }
            StatusMessage = isRoot
                ? $"Exported {name} (root for {_currentSite}) to {dialog.FileName}"
                : $"Exported {name} to {dialog.FileName}. The root itself wasn't available on this machine, so this is the top certificate the site sent.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show($"Couldn't save the file.\n\n{ex.Message}", "CertWatch");
        }
    }
    public event Action? LaunchDemoRequested;
    public bool CanLaunchDemo { get; }

    private bool _demoActive;
    public bool DemoActive { get => _demoActive; set { if (Set(ref _demoActive, value)) OnChanged(nameof(ShowLaunchButton)); } }

    private bool _demoStarting;
    public bool DemoStarting { get => _demoStarting; set { if (Set(ref _demoStarting, value)) OnChanged(nameof(ShowLaunchButton)); } }
    public bool ShowLaunchButton => CanLaunchDemo && !_demoActive && !_demoStarting;

    /// <summary>One-line explanation shown beside the Launch for demo button.</summary>
    public string LaunchHelp => CanLaunchDemo
        ? $"Optional. Launch for demo opens a separate {Browser.DisplayName} window with its own CertWatch profile, so CertWatch can check every open tab and read the exact certificate {Browser.DisplayName} received. Normal tracking keeps working; close that window to stop."
        : "";

    private string _modeText = "Mode: CertWatch's own connection on this browser's path";
    public string ModeText { get => _modeText; set => Set(ref _modeText, value); }

    private string _statusMessage = "";
    /// <summary>One-line notice above the sites list (e.g. after clearing history).</summary>
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public ObservableCollection<OpenTabRow> OpenTabs { get; } = [];
    private IReadOnlyList<OpenTab> _lastTabs = [];

    public void SetOpenTabs(IReadOnlyList<OpenTab> tabs)
    {
        _lastTabs = tabs;
        RefreshOpenTabs();
    }

    private void RefreshOpenTabs()
    {
        OpenTabs.Clear();
        foreach (var tab in _lastTabs)
        {
            var row = Sites.FirstOrDefault(s => s.Endpoint == tab.Site);
            OpenTabs.Add(new OpenTabRow(
                string.IsNullOrWhiteSpace(tab.Title) ? tab.Site.ToString() : tab.Title,
                tab.Site.ToString(),
                row?.Verdict ?? "Checking…",
                row?.VerdictBrush ?? VerdictStyle.Frozen(VerdictStyle.Neutral),
                row?.Egress ?? ""));
        }
    }

    private string _sourceText = "—";
    public string SourceText { get => _sourceText; set => Set(ref _sourceText, value); }

    private string _differNote = "";
    /// <summary>Set when the browser received a different certificate than CertWatch's own connection.</summary>
    public string DifferNote { get => _differNote; set => Set(ref _differNote, value); }

    public System.Windows.Input.ICommand ClearSiteHistoryCommand { get; }
    public System.Windows.Input.ICommand ClearAllHistoryCommand { get; }
    public event Action<SiteRow>? ClearSiteRequested;
    public event Action? ClearAllRequested;

    /// <summary>Removes a site's row after its history was deleted.</summary>
    public void RemoveSite(SiteRow row)
    {
        Sites.Remove(row);
        if (_selectedSite == row)
        {
            _selectedSite = null;
            OnChanged(nameof(SelectedSite));
            OnChanged(nameof(SiteHistoryTitle));
            SiteHistory.Clear();
        }
    }

    public void ClearSites()
    {
        CardNote = "";
        Sites.Clear();
        _selectedSite = null;
        OnChanged(nameof(SelectedSite));
        OnChanged(nameof(SiteHistoryTitle));
        SiteHistory.Clear();
    }

    internal static string EgressShort(Observation o) => o.EgressKind switch
    {
        null => "",
        "Tunnel" => $"Tunnel · {o.EgressInterface}",
        "Direct" => $"Direct · {o.EgressInterface}",
        _ => o.EgressInterface ?? "",
    };

    public BrowserDefinition Browser { get; }
    public string Name => Browser.DisplayName;
    public Brush AccentBrush { get; }
    public ObservableCollection<SiteRow> Sites { get; } = [];
    public ObservableCollection<HistoryRow> SiteHistory { get; } = [];

    public System.Windows.Input.ICommand RecheckCommand { get; }
    public System.Windows.Input.ICommand CopyFingerprintCommand { get; }
    public event Action? RecheckRequested;
    /// <summary>Raised when the user (or CertWatch) selects a site, so its history can be loaded.</summary>
    public event Action<SiteRow?>? SiteSelected;

    private SiteRow? _selectedSite;
    public SiteRow? SelectedSite
    {
        get => _selectedSite;
        set
        {
            // Tabs share one view; when it switches tabs the grid briefly pushes null back. Keep the
            // selection so it's still there when the user returns to this tab.
            if (value is null && _selectedSite is not null && Sites.Contains(_selectedSite)) return;
            if (!Set(ref _selectedSite, value)) return;
            OnChanged(nameof(SiteHistoryTitle));
            SiteSelected?.Invoke(value); // loads history, which shows a saved result for other sites
            if (value is not null && value.Endpoint == _liveSite && _lastLive is { } live) RenderLive(live);
        }
    }

    /// <summary>Short status for the tab header, e.g. "youtube.com · Inspected".</summary>
    private string _tabStatus = "Waiting";
    public string TabStatus { get => _tabStatus; set => Set(ref _tabStatus, value); }
    public string SiteHistoryTitle => _selectedSite is null ? "Select a site to see its history" : $"History for {_selectedSite.Host}";

    private string _addressText = "Waiting for the browser…";
    public string AddressText { get => _addressText; set => Set(ref _addressText, value); }

    private string _site = "No HTTPS page in focus";
    public string Site { get => _site; set => Set(ref _site, value); }

    private string _headline = "WAITING";
    public string Headline { get => _headline; set => Set(ref _headline, value); }

    private string _subline = "Open an HTTPS page in the followed browser.";
    public string Subline { get => _subline; set => Set(ref _subline, value); }

    private Brush _verdictBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
    public Brush VerdictBrush { get => _verdictBrush; set => Set(ref _verdictBrush, value); }

    private string _issuer = "—", _root = "—", _validUntil = "—", _protocol = "—", _fingerprint = "—", _path = "—", _reason = "—";
    public string Issuer { get => _issuer; set => Set(ref _issuer, value); }
    public string Root { get => _root; set => Set(ref _root, value); }
    public string ValidUntil { get => _validUntil; set => Set(ref _validUntil, value); }
    public string Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value); }
    public string NetworkPath { get => _path; set => Set(ref _path, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }

    private bool _mutualTls;
    public bool MutualTls { get => _mutualTls; set => Set(ref _mutualTls, value); }

    private string _egressKind = "—", _egressInterface = "—", _egressRoute = "—", _egressReason = "", _remoteAddress = "—";
    public string EgressKind { get => _egressKind; set => Set(ref _egressKind, value); }
    public string EgressInterface { get => _egressInterface; set => Set(ref _egressInterface, value); }
    public string EgressRoute { get => _egressRoute; set => Set(ref _egressRoute, value); }
    public string EgressReason { get => _egressReason; set => Set(ref _egressReason, value); }
    public string RemoteAddress { get => _remoteAddress; set => Set(ref _remoteAddress, value); }

    private Brush _egressBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
    public Brush EgressBrush { get => _egressBrush; set => Set(ref _egressBrush, value); }

    private string _pastInspection = "";
    /// <summary>Shown on the card when the site isn't inspected now but was before.</summary>
    public string PastInspection { get => _pastInspection; set => Set(ref _pastInspection, value); }

    private string _lastChecked = "";
    public string LastChecked { get => _lastChecked; set => Set(ref _lastChecked, value); }

    public void ShowNoSite()
    {
        _liveSite = null;
        _lastLive = null;
        CardNote = "";
        TabStatus = "No HTTPS page";
        Site = "No HTTPS page in focus";
        Headline = "WAITING";
        Subline = "Open an HTTPS page in the followed browser.";
        VerdictBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
        Issuer = Root = ValidUntil = Protocol = Fingerprint = NetworkPath = Reason = "—";
        PastInspection = "";
        _currentChain = [];
        _currentSite = "";
        SourceText = "—";
        DifferNote = "";
        EgressKind = EgressInterface = EgressRoute = RemoteAddress = "—";
        EgressReason = "";
        EgressBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
        MutualTls = false;
        LastChecked = "";
    }

    public void ShowChecking(SiteEndpoint site)
    {
        _liveSite = site;
        _lastLive = null;
        CardNote = "";
        SelectRow(Sites.FirstOrDefault(r => r.Endpoint == site));
        Site = site.ToString();
        TabStatus = $"{site} · checking";
        Headline = "CHECKING…";
        Subline = "Connecting the way the browser does";
        VerdictBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
    }

    // The site the browser is on right now, and its latest live check.
    private SiteEndpoint? _liveSite;
    private SiteCheck? _lastLive;

    private string _cardNote = "";
    /// <summary>Set when the card shows a site picked from the list rather than the browser's live tab.</summary>
    public string CardNote { get => _cardNote; set { if (Set(ref _cardNote, value)) OnChanged(nameof(ShowingSelected)); } }
    public bool ShowingSelected => _cardNote.Length > 0;

    private System.Windows.Input.ICommand? _backToLive;
    public System.Windows.Input.ICommand BackToLiveCommand => _backToLive ??= new RelayCommand(() =>
    {
        var row = _liveSite is { } live ? Sites.FirstOrDefault(r => r.Endpoint == live) : null;
        if (row is not null) SelectedSite = row;
        else if (_lastLive is { } l) RenderLive(l);
    });

    /// <summary>
    /// A live check of the browser's current site. Updates its row; updates the card unless the user
    /// is looking at a different site picked from the list. Returns the row.
    /// </summary>
    public SiteRow ShowCheck(SiteCheck check)
    {
        _liveSite = check.Probe.Endpoint;
        _lastLive = check;
        var row = UpdateRow(check);
        if (_selectedSite is null) SelectRow(row);
        if (_selectedSite is null || _selectedSite.Endpoint == check.Probe.Endpoint) RenderLive(check);
        TabStatus = $"{check.Probe.Endpoint} · {VerdictStyle.Short(PathTreatment.Of(check.Classification.Verdict, check.Egress?.Kind.ToString()), check.Classification.Verdict)}";
        return row;
    }

    /// <summary>Fills the card from a live check.</summary>
    private void RenderLive(SiteCheck check)
    {
        CardNote = "";
        var c = check.Classification;
        var o = check.Observation;
        var p = check.Probe;

        var treatment = PathTreatment.Of(c.Verdict, check.Egress?.Kind.ToString());
        _currentChain = p.Chain;
        _currentSite = p.Endpoint.ToString();
        Site = p.Endpoint.ToString();
        TabStatus = $"{Site} · {VerdictStyle.Short(treatment, c.Verdict)}";
        Headline = VerdictStyle.Headline(treatment, c.Verdict, c.Vendor);
        var change = VerdictStyle.Describe(o.Change);
        Subline = change is null || o.Change == ChangeKind.FirstSeen ? c.Reason : $"{change} · {c.Reason}";
        VerdictBrush = VerdictStyle.Frozen(VerdictStyle.ColorOf(treatment, c.Verdict));

        SourceText = p.Source == CertificateSource.Browser
            ? "Exact: the certificate this browser received"
            : "CertWatch's own connection on this browser's path";
        DifferNote = p.BrowserAndOwnConnectionDiffer
            ? $"The browser received a different certificate than CertWatch's own connection got ({p.OwnConnectionIssuer ?? "unknown issuer"}). The browser's traffic is handled differently from the rest of the machine."
            : "";

        Issuer = c.IssuerName ?? "—";
        Root = c.RootName is null ? "—" : $"{c.RootName} · {(c.RootIsPublic ? "public" : "private")}";
        ValidUntil = o.NotAfter is { } na ? $"{na.LocalDateTime:MMM d, yyyy} ({Math.Max(0, (int)(na - DateTimeOffset.UtcNow).TotalDays)} days)" : "—";
        Protocol = o.Protocol is null ? "—" : $"{o.Protocol}{(p.CipherSuite is { } cs ? " · " + cs : "")}";
        Fingerprint = o.LeafFingerprint ?? "—";
        NetworkPath = p.Proxy is null ? "No proxy" : $"System proxy {p.Proxy.Authority}";
        RemoteAddress = p.RemoteAddress?.ToString() ?? "—";
        if (check.Egress is { } e)
        {
            EgressKind = e.Kind switch
            {
                Core.Network.EgressKind.Tunnel => "ZTNA TUNNEL",
                Core.Network.EgressKind.Direct => "DIRECT",
                _ => "UNKNOWN",
            };
            EgressBrush = VerdictStyle.Frozen(e.Kind == Core.Network.EgressKind.Tunnel ? VerdictStyle.Steered
                : e.Kind == Core.Network.EgressKind.Direct ? VerdictStyle.NotInspected : VerdictStyle.Neutral);
            EgressInterface = string.IsNullOrEmpty(e.Facts.InterfaceDescription)
                ? e.InterfaceLabel
                : $"{e.InterfaceLabel}  ({e.Facts.InterfaceDescription})";
            EgressRoute = e.RouteLabel;
            EgressReason = e.Reason;
        }
        else
        {
            EgressKind = "UNKNOWN";
            EgressBrush = VerdictStyle.Frozen(VerdictStyle.Neutral);
            EgressInterface = EgressRoute = "—";
            EgressReason = "Route lookup unavailable";
        }
        Reason = c.Reason;
        MutualTls = c.MutualTls;
        LastChecked = $"Checked {o.Timestamp.LocalDateTime:T}";

        var liveRow = Sites.FirstOrDefault(r => r.Endpoint == p.Endpoint);
        PastInspection = c.Verdict != Verdict.Inspected && liveRow?.LastInspected is { } at
            ? $"Was inspected {at.LocalDateTime:MMM d HH:mm}{(liveRow.InspectedBy is null ? "" : $" via {liveRow.InspectedBy}")}. Not inspected right now."
            : "";
    }

    /// <summary>Fills the card from a site's saved result (picked from the list).</summary>
    private void RenderSaved(Observation o, SiteRow row)
    {
        var t = PathTreatment.Of(o.Verdict, o.EgressKind);
        var live = _liveSite is { } l ? l.ToString() : "no HTTPS page";
        CardNote = $"Showing the saved result for {row.Host} (last seen {o.LastSeen.LocalDateTime:MMM d HH:mm:ss}). The browser is on {live}.";
        _currentChain = [];
        _currentSite = row.Host;

        Site = row.Host;
        Headline = VerdictStyle.Headline(t, o.Verdict, o.Vendor);
        Subline = o.Reason;
        VerdictBrush = VerdictStyle.Frozen(VerdictStyle.ColorOf(t, o.Verdict));
        SourceText = $"Saved result from {o.Timestamp.LocalDateTime:MMM d HH:mm:ss}";
        DifferNote = "";
        Issuer = o.Issuer ?? "—";
        Root = o.Root is null ? "—" : $"{o.Root} · {(o.Verdict == Verdict.NotInspected ? "public" : "private")}";
        ValidUntil = o.NotAfter is { } na ? $"{na.LocalDateTime:MMM d, yyyy} ({Math.Max(0, (int)(na - DateTimeOffset.UtcNow).TotalDays)} days)" : "—";
        Protocol = o.Protocol ?? "—";
        Fingerprint = o.LeafFingerprint ?? "—";
        NetworkPath = "—";
        RemoteAddress = o.RemoteAddress ?? "—";
        EgressKind = o.EgressKind switch { "Tunnel" => "ZTNA TUNNEL", "Direct" => "DIRECT", null => "UNKNOWN", _ => o.EgressKind.ToUpperInvariant() };
        EgressBrush = VerdictStyle.Frozen(o.EgressKind == "Tunnel" ? VerdictStyle.Steered : o.EgressKind == "Direct" ? VerdictStyle.NotInspected : VerdictStyle.Neutral);
        EgressInterface = o.EgressInterface ?? "—";
        EgressRoute = o.EgressRoute ?? "—";
        EgressReason = o.EgressKind is null ? "Not recorded for this result" : "";
        Reason = o.Reason;
        MutualTls = o.MutualTls;
        LastChecked = $"Last seen {o.LastSeen.LocalDateTime:T}";
        PastInspection = o.Verdict != Verdict.Inspected && row.LastInspected is { } at
            ? $"Was inspected {at.LocalDateTime:MMM d HH:mm}{(row.InspectedBy is null ? "" : $" via {row.InspectedBy}")}. Not inspected right now."
            : "";
    }

    /// <summary>Selects a row from code (follows the live site) without the tab-switch null guard.</summary>
    private void SelectRow(SiteRow? row)
    {
        if (row is null)
        {
            if (_selectedSite is null) return;
            _selectedSite = null;
            OnChanged(nameof(SelectedSite));
            OnChanged(nameof(SiteHistoryTitle));
            SiteHistory.Clear();
            return;
        }
        SelectedSite = row;
    }

    /// <summary>Updates (or adds) the site's row without touching the card: used for background tabs.</summary>
    public SiteRow UpdateRow(SiteCheck check)
    {
        var o = check.Observation;
        var p = check.Probe;
        StatusMessage = "";
        var row = Sites.FirstOrDefault(s => s.Endpoint == p.Endpoint);
        if (row is null)
        {
            row = new SiteRow(p.Endpoint);
            row.Apply(o, o.Change is ChangeKind.None or ChangeKind.FirstSeen ? 0 : 1, null);
            Sites.Add(row);
        }
        else
        {
            row.Update(o);
        }

        if (row == SelectedSite && o.Change != ChangeKind.None) SiteHistory.Insert(0, ToHistoryRow(o));
        if (_lastTabs.Count > 0) RefreshOpenTabs();
        return row;
    }

    public void LoadSites(IEnumerable<SiteSummary> summaries)
    {
        _selectedSite = null;
        SiteHistory.Clear();
        Sites.Clear();
        foreach (var s in summaries)
        {
            var row = new SiteRow(new SiteEndpoint(s.Latest.Host, s.Latest.Port));
            row.SetInspectionHistory(s.LastInspected, s.InspectedBy);
            row.Apply(s.Latest, s.ChangeCount, s.ChangeCount > 0 ? s.Latest.Timestamp : null);
            Sites.Add(row);
        }
    }

    public void LoadSiteHistory(IEnumerable<Observation> newestFirst)
    {
        var list = newestFirst.ToList();
        SiteHistory.Clear();
        foreach (var o in list) SiteHistory.Add(ToHistoryRow(o));

        // A site picked from the list that isn't the live tab: show its latest saved result on the card.
        if (_selectedSite is { } row && row.Endpoint != _liveSite && list.Count > 0) RenderSaved(list[0], row);
    }

    private static HistoryRow ToHistoryRow(Observation o) => new(
        o.Timestamp.LocalDateTime.ToString("MMM d HH:mm:ss"),
        VerdictStyle.Short(PathTreatment.Of(o.Verdict, o.EgressKind), o.Verdict),
        VerdictStyle.Frozen(VerdictStyle.ColorOf(PathTreatment.Of(o.Verdict, o.EgressKind), o.Verdict)),
        o.Issuer ?? "—",
        EgressShort(o),
        VerdictStyle.Describe(o.Change) ?? "",
        o.MutualTls ? "mTLS" : "");
}

internal sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
