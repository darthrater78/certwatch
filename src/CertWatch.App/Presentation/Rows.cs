using System.Windows.Media;
using CertWatch.Core.Browsers;
using CertWatch.Core.History;
using CertWatch.Core.Network;

namespace CertWatch.App.Presentation;

/// <summary>One labelled value in the card's fact grid.</summary>
public sealed record Fact(string Label, string Value, bool Mono = false, string? Tooltip = null)
{
    public string ToolTipText => Tooltip ?? Value;
}

/// <summary>One hop in the card's path diagram.</summary>
public sealed record PathNode(string Title, string Sub, StatusKind Status, Geometry Icon, bool HasArrow, StatusKind LineStatus);

/// <summary>A note strip under the card (browser/own-connection mismatch, mTLS, earlier inspection).</summary>
public sealed record CardNote(string Text, StatusKind Status);

/// <summary>One entry in a site's history (first visit or a change). Clicking it shows it on the card.</summary>
public sealed record HistoryRow(Observation Observation)
{
    public string Time => Observation.Timestamp.LocalDateTime.ToString("MMM d HH:mm:ss");
    public StatusKind Status => Presentation.Status.Of(Observation.Verdict, Observation.EgressKind);
    public string Verdict => VerdictStyle.Short(PathTreatment.Of(Observation.Verdict, Observation.EgressKind), Observation.Verdict);
    public string Change => VerdictStyle.Describe(Observation.Change) ?? "";
    public string Egress => Observation.EgressInterface ?? "";
    public string MutualTls => Observation.MutualTls ? "mTLS" : "";
}

/// <summary>An HTTPS tab open in the demo browser, with its site's current result.</summary>
public sealed record OpenTabRow(string Title, string Site, StatusKind Status, string Verdict, string Egress);

/// <summary>One site in the "Sites" list. Updated in place so the list never shows a site twice.</summary>
public sealed class SiteRow(SiteEndpoint endpoint) : Observable
{
    public SiteEndpoint Endpoint { get; } = endpoint;
    public string Host => Endpoint.ToString();

    private StatusKind _status;
    public StatusKind Status { get => _status; private set => Set(ref _status, value); }

    private string _verdict = "", _issuer = "", _egress = "", _lastChange = "", _inspection = "", _mutualTls = "";
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string Issuer { get => _issuer; private set => Set(ref _issuer, value); }
    public string Egress { get => _egress; private set => Set(ref _egress, value); }
    public string LastChange { get => _lastChange; private set => Set(ref _lastChange, value); }
    public string MutualTls { get => _mutualTls; private set => Set(ref _mutualTls, value); }

    /// <summary>"Inspected now", "Was inspected · …", or "Never inspected".</summary>
    public string Inspection { get => _inspection; private set => Set(ref _inspection, value); }
    private StatusKind _inspectionStatus;
    public StatusKind InspectionStatus { get => _inspectionStatus; private set => Set(ref _inspectionStatus, value); }

    private int _changeCount;
    public int ChangeCount { get => _changeCount; private set => Set(ref _changeCount, value); }

    private DateTimeOffset _lastSeen;
    public DateTimeOffset LastSeen { get => _lastSeen; private set { if (Set(ref _lastSeen, value)) OnChanged(nameof(LastSeenText)); } }
    public string LastSeenText => _lastSeen.LocalDateTime.ToString("MMM d HH:mm:ss");

    /// <summary>When the site was last seen inspected, even if it isn't now.</summary>
    public DateTimeOffset? LastInspected { get; private set; }
    public string? InspectedBy { get; private set; }

    public void SetInspectionHistory(DateTimeOffset? lastInspected, string? inspectedBy)
    {
        LastInspected = lastInspected;
        InspectedBy = inspectedBy;
    }

    /// <summary>Full refresh from a site's latest result (initial load, or after a change).</summary>
    public void Apply(Observation latest, int changeCount, DateTimeOffset? lastChangeAt)
    {
        ApplyLive(latest);
        Issuer = latest.Issuer ?? "—";
        ChangeCount = changeCount;
        MutualTls = latest.MutualTls ? "mTLS" : "";
        var when = (lastChangeAt ?? latest.Timestamp).LocalDateTime.ToString("MMM d HH:mm");
        LastChange = changeCount == 0 ? $"No changes · first seen {when}" : $"{VerdictStyle.Describe(latest.Change) ?? "Changed"} · {when}";
    }

    /// <summary>A re-check or change for this site just came in.</summary>
    public void Update(Observation o)
    {
        var isChange = o.Change is not (ChangeKind.None or ChangeKind.FirstSeen);
        if (isChange || (ChangeCount == 0 && o.Change == ChangeKind.FirstSeen)) Apply(o, ChangeCount + (isChange ? 1 : 0), o.Timestamp);
        else ApplyLive(o);
    }

    private void ApplyLive(Observation o)
    {
        if (o.Verdict == Core.Trust.Verdict.Inspected)
        {
            var at = o.LastSeen > o.Timestamp ? o.LastSeen : o.Timestamp;
            if (LastInspected is null || at >= LastInspected) { LastInspected = at; InspectedBy = o.Issuer; }
        }
        Status = Presentation.Status.Of(o.Verdict, o.EgressKind);
        Verdict = VerdictStyle.Short(PathTreatment.Of(o.Verdict, o.EgressKind), o.Verdict);
        Egress = o.EgressInterface ?? "";
        LastSeen = o.LastSeen > o.Timestamp ? o.LastSeen : o.Timestamp;
        RefreshInspection(o.Verdict);
    }

    private void RefreshInspection(Core.Trust.Verdict current)
    {
        if (current == Core.Trust.Verdict.Inspected) (Inspection, InspectionStatus) = ("Inspected now", StatusKind.Inspected);
        else if (LastInspected is { } at) (Inspection, InspectionStatus) = ($"Was inspected · {at.LocalDateTime:MMM d HH:mm}{(InspectedBy is null ? "" : " · " + InspectedBy)}", StatusKind.Inspected);
        else (Inspection, InspectionStatus) = ("Never inspected", StatusKind.Neutral);
    }
}
