using CertWatch.Core.Network;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>The five status colours of DESIGN.md. Views pick theme brushes and icons from it.</summary>
public enum StatusKind { Neutral, Inspected, Steered, Direct, Untrusted }

internal static class Status
{
    public static StatusKind Of(Treatment t, Verdict v) => t switch
    {
        Treatment.SteeredAndInspected or Treatment.InspectedOnDevice or Treatment.Inspected => StatusKind.Inspected,
        Treatment.SteeredNotInspected => StatusKind.Steered,
        Treatment.Direct or Treatment.NotInspected => StatusKind.Direct,
        _ => v == Verdict.Untrusted ? StatusKind.Untrusted : StatusKind.Neutral,
    };

    public static StatusKind Of(Verdict v, string? egressKind) => Of(PathTreatment.Of(v, egressKind), v);

    public static StatusKind OfEgress(string? egressKind) => egressKind switch
    {
        nameof(EgressKind.Tunnel) => StatusKind.Steered,
        nameof(EgressKind.Direct) => StatusKind.Direct,
        _ => StatusKind.Neutral,
    };
}
