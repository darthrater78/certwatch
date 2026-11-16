using CertWatch.Core.Trust;

namespace CertWatch.Core.History;

public enum ChangeKind
{
    None,
    FirstSeen,
    /// <summary>Same verdict, different certificate (e.g. a routine renewal).</summary>
    CertificateChanged,
    InspectionStarted,
    InspectionStopped,
    /// <summary>Same verdict and certificate, but traffic now leaves through a different interface.</summary>
    EgressChanged,
    /// <summary>Any other verdict transition (e.g. to or from Error/Untrusted).</summary>
    VerdictChanged,
}

/// <summary>One check of one site, as stored in the history database.</summary>
public sealed record Observation(
    DateTimeOffset Timestamp,
    string Host,
    int Port,
    string Browser,
    Verdict Verdict,
    string Reason,
    string? Vendor,
    string? LeafFingerprint,
    string? LeafSubject,
    string? Issuer,
    string? Root,
    DateTimeOffset? NotAfter,
    string? Protocol,
    bool MutualTls,
    ChangeKind Change)
{
    public long Id { get; init; }

    /// <summary>When this result was last confirmed by a re-check (re-checks with no change don't add rows).</summary>
    public DateTimeOffset LastSeen { get; init; } = Timestamp;

    /// <summary>IP address CertWatch connected to.</summary>
    public string? RemoteAddress { get; init; }
    /// <summary>Interface the traffic left on (name, description).</summary>
    public string? EgressInterface { get; init; }
    /// <summary>Tunnel / Direct / Unknown.</summary>
    public string? EgressKind { get; init; }
    /// <summary>e.g. "104.18.32.7/32 → no gateway (on-link)".</summary>
    public string? EgressRoute { get; init; }
}

/// <summary>One site as seen by one browser: its latest result and how many times it changed.</summary>
public sealed record SiteSummary(Observation Latest, int ChangeCount)
{
    /// <summary>When this site was last seen inspected (null if never), and by which issuing CA.</summary>
    public DateTimeOffset? LastInspected { get; init; }
    public string? InspectedBy { get; init; }
}

public static class ChangeDetector
{
    public static ChangeKind Detect(Observation? previous, Verdict verdict, string? leafFingerprint, string? issuer = null,
        string? egressInterface = null)
    {
        var change = DetectCertificate(previous, verdict, leafFingerprint, issuer);
        if (change == ChangeKind.None && previous?.EgressInterface is { } before && egressInterface is not null &&
            !string.Equals(before, egressInterface, StringComparison.Ordinal))
            return ChangeKind.EgressChanged;
        return change;
    }

    private static ChangeKind DetectCertificate(Observation? previous, Verdict verdict, string? leafFingerprint, string? issuer)
    {
        if (previous is null) return ChangeKind.FirstSeen;
        if (previous.Verdict != verdict)
        {
            return (previous.Verdict, verdict) switch
            {
                (_, Verdict.Inspected) when previous.Verdict == Verdict.NotInspected => ChangeKind.InspectionStarted,
                (Verdict.Inspected, Verdict.NotInspected) => ChangeKind.InspectionStopped,
                _ => ChangeKind.VerdictChanged,
            };
        }
        // Inspection proxies mint a fresh leaf certificate per site and re-mint it often, so for an
        // inspected site only a different issuing CA is a real change.
        if (verdict == Verdict.Inspected)
            return string.Equals(previous.Issuer, issuer, StringComparison.Ordinal) ? ChangeKind.None : ChangeKind.CertificateChanged;

        return string.Equals(previous.LeafFingerprint, leafFingerprint, StringComparison.OrdinalIgnoreCase)
            ? ChangeKind.None
            : ChangeKind.CertificateChanged;
    }
}
