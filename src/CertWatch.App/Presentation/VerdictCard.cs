using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Monitoring;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>
/// The verdict card: headline, path diagram, fact grid and notes. Filled from a live check or from a
/// saved history entry through the same <see cref="CardData"/> shape, so both look identical.
/// </summary>
public sealed class VerdictCard : Observable
{
    /// <summary>Everything the card shows, independent of where it came from.</summary>
    private sealed record CardData(
        string Site, DateTimeOffset Time, Verdict Verdict, string Reason, string? Vendor, string? Issuer, string? Root,
        bool RootPublic, DateTimeOffset? NotAfter, string? Protocol, string? Fingerprint, string? RemoteIp,
        string? EgressKind, string? EgressInterface, string? EgressDescription, string? EgressRoute, bool MutualTls,
        string Source, ChangeKind Change, IReadOnlyList<X509Certificate2> Chain);

    private readonly string _browserName;

    public VerdictCard(string browserName)
    {
        _browserName = browserName;
        ShowWaiting();
    }

    public ObservableCollection<PathNode> Path { get; } = [];
    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<CardNote> Notes { get; } = [];

    private StatusKind _status;
    public StatusKind Status { get => _status; private set => Set(ref _status, value); }

    private string _site = "", _context = "", _headline = "", _subline = "", _fingerprint = "";
    public string Site { get => _site; private set => Set(ref _site, value); }
    public string Context { get => _context; private set => Set(ref _context, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, value); }
    public string Fingerprint { get => _fingerprint; private set => Set(ref _fingerprint, value); }

    /// <summary>The chain behind the card (empty for saved results), for root export.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; private set; } = [];

    public void ShowWaiting(string? message = null, string? headline = null)
    {
        Status = StatusKind.Neutral;
        Site = "";
        Context = $"{_browserName} · waiting";
        Headline = headline ?? "No HTTPS page in focus";
        Subline = message ?? $"Open an HTTPS page in {_browserName}.";
        Fingerprint = "";
        Chain = [];
        Path.Clear();
        Facts.Clear();
        Notes.Clear();
    }

    public void ShowChecking(SiteEndpoint site)
    {
        Status = StatusKind.Neutral;
        Site = site.ToString();
        Context = $"{site} · {_browserName} · checking…";
        Headline = "Checking…";
        Subline = "Connecting the way the browser does";
    }

    /// <param name="path">Whether the browser takes CertWatch's path; Different turns the headline into "Browser uses its own path".</param>
    /// <param name="viewer">What the browser's own certificate viewer showed for this site, when the user opened it.</param>
    /// <param name="viewerHint">A prompt to open the browser's certificate viewer, shown as a note.</param>
    /// <param name="direct">An enterprise browser's own path: when found, the card shows that result and CertWatch's steered one becomes a note.</param>
    public void ShowLive(SiteCheck check, string? pastInspection, BrowserPathResult? path = null, CertViewerReading? viewer = null,
        string? viewerHint = null, DirectPathResult? direct = null)
    {
        if (direct is { Outcome: DirectPathOutcome.Found, Check: { } own })
        {
            Render(FromCheck(own, "browser's own path"), live: true, pastInspection, null, viewer, directAddress: own.Probe.RemoteAddress?.ToString());
            Notes.Insert(0, new CardNote($"Other apps on this PC, CertWatch's own check included, are steered through {check.Egress?.InterfaceLabel ?? "the tunnel"}: {HeadlineOf(check)}, issuer {check.Classification.IssuerName ?? "—"}.", StatusKind.Neutral));
        }
        else
        {
            Render(FromCheck(check), live: true, pastInspection, path, viewer);
            if (direct?.Outcome == DirectPathOutcome.Captured)
                Notes.Add(new CardNote($"CertWatch also checked {direct.Address}, the address {_browserName} is connected to for this site, and was inspected there too: the agent catches CertWatch's traffic whatever the address, so the browser's own result can't be seen this way. Compare with the browser's certificate viewer.", StatusKind.Neutral));
            else if (direct?.Outcome == DirectPathOutcome.NotFound)
                Notes.Add(new CardNote($"This is CertWatch's own connection. None of {_browserName}'s direct connections ({direct.AddressesTried} tried) served this site, so its own path wasn't found; if it uses its own gateway, compare with its certificate viewer.", StatusKind.Neutral));
        }
        if (viewerHint is not null) Notes.Add(new CardNote(viewerHint, StatusKind.Neutral));
    }

    private static string HeadlineOf(SiteCheck c) =>
        VerdictStyle.Headline(PathTreatment.Of(c.Classification.Verdict, c.Egress?.Kind.ToString()), c.Classification.Verdict, c.Classification.Vendor, c.Classification.MutualTls);

    public void ShowSaved(Observation o, string? pastInspection) => Render(FromObservation(o), live: false, pastInspection);

    private static CardData FromCheck(SiteCheck check, string source = "own connection")
    {
        var c = check.Classification;
        var p = check.Probe;
        var o = check.Observation;
        return new CardData(p.Endpoint.ToString(), o.Timestamp, c.Verdict, c.Reason, c.Vendor, c.IssuerName, c.RootName,
            c.RootIsPublic, o.NotAfter, Protocol(o.Protocol, p.CipherSuite?.ToString()), o.LeafFingerprint, p.RemoteAddress?.ToString(),
            check.Egress?.Kind.ToString(), check.Egress?.InterfaceLabel, check.Egress?.Facts.InterfaceDescription,
            check.Egress?.RouteLabel, c.MutualTls, source, o.Change, p.Chain);
    }

    private static CardData FromObservation(Observation o) => new(
        o.Port == 443 ? o.Host : $"{o.Host}:{o.Port}", o.Timestamp, o.Verdict, o.Reason, o.Vendor, o.Issuer, o.Root,
        o.Verdict == Verdict.NotInspected, o.NotAfter, Protocol(o.Protocol, null), o.LeafFingerprint, o.RemoteAddress,
        o.EgressKind, o.EgressInterface, null, o.EgressRoute, o.MutualTls, $"saved {o.Timestamp.LocalDateTime:MMM d HH:mm:ss}",
        o.Change, []);

    private static string? Protocol(string? protocol, string? cipher) =>
        protocol is null ? null : cipher is null ? protocol : $"{protocol} · {cipher}";

    private void Render(CardData d, bool live, string? pastInspection, BrowserPathResult? path = null, CertViewerReading? viewer = null, string? directAddress = null)
    {
        var ownPath = path?.Match == BrowserPathMatch.Different;
        var treatment = PathTreatment.Of(d.Verdict, d.EgressKind);
        Status = Presentation.Status.Of(treatment, d.Verdict);
        Site = d.Site;
        Context = $"{d.Site} · {_browserName} · {(live ? $"checked {d.Time.LocalDateTime:T}" : "")}{(live ? " · " : "")}{d.Source}";
        Headline = VerdictStyle.Headline(treatment, d.Verdict, d.Vendor, d.MutualTls);
        var change = VerdictStyle.Describe(d.Change);
        Subline = change is null || d.Change == ChangeKind.FirstSeen ? d.Reason : $"{change} · {d.Reason}";
        if (ownPath)
        {
            // CertWatch's result is true of its own connection only; don't present it as the browser's.
            Subline = $"CertWatch's own connection: {Headline}. {Subline}";
            Headline = "Browser uses its own path";
            Status = StatusKind.Neutral;
        }
        if (directAddress is not null) Subline = $"{_browserName}'s own path, checked at {directAddress}. {Subline}";
        if (viewer is not null && !viewer.Matches(d.Fingerprint))
        {
            // The browser's own viewer outranks any connection CertWatch made.
            Subline = $"Browser: {viewer.IssuerLabel ?? "issuer not read"} · CertWatch's connection: {Headline}";
            Headline = "Browser sees a different certificate";
            Status = StatusKind.Neutral;
        }
        Fingerprint = d.Fingerprint ?? "";
        Chain = d.Chain;
        BuildPath(d, ownPath && directAddress is null);
        BuildFacts(d);
        BuildNotes(d, pastInspection, ownPath ? path : null, viewer);
    }

    private void BuildPath(CardData d, bool ownConnection = false)
    {
        Path.Clear();
        var inspected = d.Verdict == Verdict.Inspected;
        var egress = Presentation.Status.OfEgress(d.EgressKind);
        var egressThenLine = inspected ? StatusKind.Inspected : egress;

        Path.Add(ownConnection
            ? new PathNode("CertWatch", "own connection, not the browser's", StatusKind.Neutral, Icon("IconBrowser"), true, egress)
            : new PathNode(_browserName, "your browser", StatusKind.Neutral, Icon("IconBrowser"), true, egress));
        Path.Add(d.EgressKind switch
        {
            nameof(EgressKind.Tunnel) => new PathNode(d.EgressInterface ?? "ZTNA tunnel", $"tunnel · {RoutePrefix(d.EgressRoute)}", StatusKind.Steered, Icon("IconSteered"), true, egressThenLine),
            nameof(EgressKind.Direct) => new PathNode(d.EgressInterface ?? "Direct", $"direct · {RoutePrefix(d.EgressRoute)}", StatusKind.Direct, Icon("IconDirect"), true, egressThenLine),
            _ => new PathNode("Network", "route not known", StatusKind.Neutral, Icon("IconNeutral"), true, egressThenLine),
        });
        if (inspected)
            Path.Add(new PathNode(d.Vendor is null ? "Inspection" : $"{d.Vendor} inspection", "certificate replaced", StatusKind.Inspected, Icon("IconInspected"), true, StatusKind.Inspected));

        var siteStatus = d.Verdict switch { Verdict.Untrusted => StatusKind.Untrusted, Verdict.Error => StatusKind.Neutral, _ => StatusKind.Direct };
        var siteSub = d.Verdict switch
        {
            Verdict.Inspected => "original certificate hidden",
            Verdict.NotInspected => $"serves {d.Issuer} · public",
            Verdict.Untrusted => $"{d.Issuer} · untrusted",
            _ => "no certificate",
        };
        Path.Add(new PathNode(d.Site, siteSub, siteStatus, Icon("IconSite"), false, StatusKind.Neutral));
    }

    private void BuildFacts(CardData d)
    {
        Facts.Clear();
        Facts.Add(new Fact("Issuer", d.Issuer ?? "—"));
        Facts.Add(new Fact("Root", d.Root is null ? "—" : $"{d.Root} · {(d.RootPublic ? "public" : "private")}"));
        Facts.Add(new Fact("Valid until", d.NotAfter is { } na ? $"{na.LocalDateTime:MMM d, yyyy} ({Math.Max(0, (int)(na - DateTimeOffset.UtcNow).TotalDays)} days)" : "—"));
        Facts.Add(new Fact("Protocol", d.Protocol ?? "—"));
        Facts.Add(new Fact("Egress", d.EgressInterface is null ? "—" : d.EgressDescription is { Length: > 0 } desc ? $"{d.EgressInterface} ({desc})" : d.EgressInterface));
        Facts.Add(new Fact("Route", d.EgressRoute ?? "—", Mono: true));
        Facts.Add(new Fact("Connected to", d.RemoteIp ?? "—", Mono: true));
        Facts.Add(new Fact("SHA-256", Shorten(d.Fingerprint), Mono: true, Tooltip: d.Fingerprint));
    }

    private void BuildNotes(CardData d, string? pastInspection, BrowserPathResult? ownPath = null, CertViewerReading? viewer = null)
    {
        Notes.Clear();
        if (ownPath is not null)
        {
            var via = ownPath.BrowserInterfaces.Count == 0 ? "" : $"; its connections leave via {string.Join(", ", ownPath.BrowserInterfaces)}";
            Notes.Add(new CardNote($"{_browserName} isn't connected to {d.RemoteIp}, the address CertWatch reached through {d.EgressInterface ?? "the tunnel"}{via}. The details below are CertWatch's own connection, not the browser's.", StatusKind.Neutral));
        }
        if (viewer is not null)
        {
            Notes.Add(viewer.Matches(d.Fingerprint)
                ? new CardNote($"{_browserName}'s certificate viewer shows the same certificate as CertWatch's connection.", StatusKind.Neutral)
                : new CardNote($"{_browserName}'s certificate viewer shows a different certificate: issued by {viewer.IssuerLabel ?? "an issuer CertWatch couldn't read"}, SHA-256 {Shorten(viewer.Sha256)}. CertWatch's connection got one issued by {d.Issuer ?? "—"}.", StatusKind.Neutral));
        }
        if (d.MutualTls)
            Notes.Add(new CardNote("mTLS: the server asks for a client certificate, so it can't be inspected and needs a bypass rule.", StatusKind.Steered));
        if (pastInspection is not null && d.Verdict != Verdict.Inspected)
            Notes.Add(new CardNote(pastInspection, StatusKind.Inspected));
    }

    private static string RoutePrefix(string? route) => route is null ? "route unknown" : route.Split(" → ")[0];

    private static string Shorten(string? fp) => fp is { Length: > 16 } ? $"{fp[..8]}…{fp[^6..]}" : fp ?? "—";

    private static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
