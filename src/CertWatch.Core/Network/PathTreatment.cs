using CertWatch.Core.Trust;

namespace CertWatch.Core.Network;

/// <summary>
/// How a site's traffic is handled, combining the two independent facts CertWatch measures:
/// whether the certificate was replaced (inspection) and whether traffic leaves through a ZTNA
/// tunnel (steering). ZTNA can steer traffic without inspecting it, so both are tracked.
/// </summary>
public enum Treatment
{
    /// <summary>Tunnelled to ZTNA and the certificate was replaced.</summary>
    SteeredAndInspected,
    /// <summary>Tunnelled to ZTNA, original certificate: steered for access/routing only.</summary>
    SteeredNotInspected,
    /// <summary>Certificate replaced but traffic isn't tunnelled: inspected on this device (local agent or browser).</summary>
    InspectedOnDevice,
    /// <summary>Certificate replaced; egress unknown.</summary>
    Inspected,
    /// <summary>Not tunnelled, original certificate.</summary>
    Direct,
    /// <summary>Original certificate; egress unknown.</summary>
    NotInspected,
    /// <summary>Untrusted certificate or the check failed.</summary>
    Unknown,
}

public static class PathTreatment
{
    public static Treatment Of(Verdict verdict, string? egressKind) => (verdict, egressKind) switch
    {
        (Verdict.Inspected, nameof(EgressKind.Tunnel)) => Treatment.SteeredAndInspected,
        (Verdict.Inspected, nameof(EgressKind.Direct)) => Treatment.InspectedOnDevice,
        (Verdict.Inspected, _) => Treatment.Inspected,
        (Verdict.NotInspected, nameof(EgressKind.Tunnel)) => Treatment.SteeredNotInspected,
        (Verdict.NotInspected, nameof(EgressKind.Direct)) => Treatment.Direct,
        (Verdict.NotInspected, _) => Treatment.NotInspected,
        _ => Treatment.Unknown,
    };

    public static bool IsSteered(Treatment t) => t is Treatment.SteeredAndInspected or Treatment.SteeredNotInspected;
    public static bool IsInspected(Treatment t) => t is Treatment.SteeredAndInspected or Treatment.InspectedOnDevice or Treatment.Inspected;
}
