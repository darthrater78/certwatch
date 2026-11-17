using CertWatch.Core.History;
using CertWatch.Core.Network;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>Wording for verdicts, path labels and changes (colours live in the theme dictionaries).</summary>
internal static class VerdictStyle
{
    public static string Short(Verdict v) => v switch
    {
        Verdict.Inspected => "Inspected",
        Verdict.NotInspected => "Not inspected",
        Verdict.Untrusted => "Untrusted",
        _ => "Error",
    };

    public static string Short(Treatment t, Verdict v) => t switch
    {
        Treatment.SteeredAndInspected => "ZTNA · inspected",
        Treatment.SteeredNotInspected => "ZTNA · steered",
        Treatment.InspectedOnDevice => "Inspected on device",
        Treatment.Inspected => "Inspected",
        Treatment.Direct => "Direct",
        Treatment.NotInspected => "Not inspected",
        _ => Short(v),
    };

    /// <param name="mutualTls">
    /// The site asks for a client certificate. A steered site that isn't inspected then says why: an
    /// inspecting proxy can't present the user's client certificate, so mTLS sites need a bypass rule.
    /// </param>
    public static string Headline(Treatment t, Verdict v, string? vendor, bool mutualTls = false)
    {
        var by = vendor is null ? "" : $" by {vendor}";
        return t switch
        {
            Treatment.SteeredAndInspected => $"Steered to ZTNA · inspected{by}",
            Treatment.SteeredNotInspected when mutualTls => "Steered to ZTNA · not inspected (mTLS: needs bypass)",
            Treatment.SteeredNotInspected => "Steered to ZTNA · not inspected",
            Treatment.InspectedOnDevice => $"Inspected on this device{by}",
            Treatment.Inspected => $"Inspected{by}",
            Treatment.Direct => "Direct · not inspected",
            Treatment.NotInspected => "Not inspected",
            _ => v == Verdict.Untrusted ? "Untrusted certificate" : "Couldn't check",
        };
    }

    public static string? Describe(ChangeKind change) => change switch
    {
        ChangeKind.InspectionStarted => "Certificate changed for inspection",
        ChangeKind.InspectionStopped => "Inspection stopped: original certificate",
        ChangeKind.CertificateChanged => "New certificate (same verdict)",
        ChangeKind.VerdictChanged => "Verdict changed",
        ChangeKind.EgressChanged => "Egress interface changed",
        ChangeKind.FirstSeen => "First visit",
        _ => null,
    };
}
