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

    public void ShowLive(SiteCheck check, string? pastInspection) => Render(FromCheck(check), live: true, pastInspection);

    public void ShowSaved(Observation o, string? pastInspection) => Render(FromObservation(o), live: false, pastInspection);

    private static CardData FromCheck(SiteCheck check)
    {
        var c = check.Classification;
        var p = check.Probe;
        var o = check.Observation;
        return new CardData(p.Endpoint.ToString(), o.Timestamp, c.Verdict, c.Reason, c.Vendor, c.IssuerName, c.RootName,
            c.RootIsPublic, o.NotAfter, Protocol(o.Protocol, p.CipherSuite?.ToString()), o.LeafFingerprint, p.RemoteAddress?.ToString(),
            check.Egress?.Kind.ToString(), check.Egress?.InterfaceLabel, check.Egress?.Facts.InterfaceDescription,
            check.Egress?.RouteLabel, c.MutualTls, "own connection", o.Change, p.Chain);
    }

    private static CardData FromObservation(Observation o) => new(
        o.Port == 443 ? o.Host : $"{o.Host}:{o.Port}", o.Timestamp, o.Verdict, o.Reason, o.Vendor, o.Issuer, o.Root,
        o.Verdict == Verdict.NotInspected, o.NotAfter, Protocol(o.Protocol, null), o.LeafFingerprint, o.RemoteAddress,
        o.EgressKind, o.EgressInterface, null, o.EgressRoute, o.MutualTls, $"saved {o.Timestamp.LocalDateTime:MMM d HH:mm:ss}",
        o.Change, []);

    private static string? Protocol(string? protocol, string? cipher) =>
        protocol is null ? null : cipher is null ? protocol : $"{protocol} · {cipher}";

    private void Render(CardData d, bool live, string? pastInspection)
    {
        var treatment = PathTreatment.Of(d.Verdict, d.EgressKind);
        Status = Presentation.Status.Of(treatment, d.Verdict);
        Site = d.Site;
        Context = $"{d.Site} · {_browserName} · {(live ? $"checked {d.Time.LocalDateTime:T}" : "")}{(live ? " · " : "")}{d.Source}";
        Headline = VerdictStyle.Headline(treatment, d.Verdict, d.Vendor, d.MutualTls);
        var change = VerdictStyle.Describe(d.Change);
        Subline = change is null || d.Change == ChangeKind.FirstSeen ? d.Reason : $"{change} · {d.Reason}";
        Fingerprint = d.Fingerprint ?? "";
        Chain = d.Chain;
        BuildPath(d);
        BuildFacts(d);
        BuildNotes(d, pastInspection);
    }

    private void BuildPath(CardData d)
    {
        Path.Clear();
        var inspected = d.Verdict == Verdict.Inspected;
        var egress = Presentation.Status.OfEgress(d.EgressKind);
        var egressThenLine = inspected ? StatusKind.Inspected : egress;

        Path.Add(new PathNode(_browserName, "your browser", StatusKind.Neutral, Icon("IconBrowser"), true, egress));
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

    private void BuildNotes(CardData d, string? pastInspection)
    {
        Notes.Clear();
        if (d.MutualTls)
            Notes.Add(new CardNote("mTLS: the server asks for a client certificate, so it can't be inspected and needs a bypass rule.", StatusKind.Steered));
        if (pastInspection is not null && d.Verdict != Verdict.Inspected)
            Notes.Add(new CardNote(pastInspection, StatusKind.Inspected));
    }

    private static string RoutePrefix(string? route) => route is null ? "route unknown" : route.Split(" → ")[0];

    private static string Shorten(string? fp) => fp is { Length: > 16 } ? $"{fp[..8]}…{fp[^6..]}" : fp ?? "—";

    private static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
