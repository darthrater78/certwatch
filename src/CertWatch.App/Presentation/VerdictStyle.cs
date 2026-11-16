using System.Windows.Media;
using CertWatch.Core.History;
using CertWatch.Core.Network;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>
/// Colours and wording for verdicts. Inspected is purple and not-inspected teal, deliberately not
/// red/green: in a ZTNA demo inspection is the product working. Red is reserved for real problems.
/// </summary>
internal static class VerdictStyle
{
    public static readonly Color Inspected = Color.FromRgb(0x5B, 0x3F, 0xC4);
    public static readonly Color NotInspected = Color.FromRgb(0x0E, 0x6B, 0x5C);
    public static readonly Color Untrusted = Color.FromRgb(0xB3, 0x26, 0x1E);
    public static readonly Color Neutral = Color.FromRgb(0x5C, 0x5C, 0x5C);
    /// <summary>Steered into the ZTNA tunnel but not inspected.</summary>
    public static readonly Color Steered = Color.FromRgb(0x0A, 0x5F, 0xB4);

    public static Color ColorOf(Treatment t, Verdict v) => t switch
    {
        Treatment.SteeredAndInspected or Treatment.InspectedOnDevice or Treatment.Inspected => Inspected,
        Treatment.SteeredNotInspected => Steered,
        Treatment.Direct or Treatment.NotInspected => NotInspected,
        _ => ColorOf(v),
    };

    public static string Short(Treatment t, Verdict v) => t switch
    {
        Treatment.SteeredAndInspected => "ZTNA · inspected",
        Treatment.SteeredNotInspected => "ZTNA · not inspected",
        Treatment.InspectedOnDevice => "Inspected on device",
        Treatment.Inspected => "Inspected",
        Treatment.Direct => "Direct · not inspected",
        Treatment.NotInspected => "Not inspected",
        _ => Short(v),
    };

    public static string Headline(Treatment t, Verdict v, string? vendor)
    {
        var by = vendor is null ? "" : $" by {vendor}";
        return t switch
        {
            Treatment.SteeredAndInspected => $"STEERED TO ZTNA · INSPECTED{by}",
            Treatment.SteeredNotInspected => "STEERED TO ZTNA · NOT INSPECTED",
            Treatment.InspectedOnDevice => $"INSPECTED ON THIS DEVICE{by}",
            Treatment.Direct => "DIRECT · NOT INSPECTED",
            _ => Headline(v, vendor),
        };
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color ColorOf(Verdict? v) => v switch
    {
        Verdict.Inspected => Inspected,
        Verdict.NotInspected => NotInspected,
        Verdict.Untrusted => Untrusted,
        _ => Neutral,
    };

    public static string Headline(Verdict v, string? vendor) => v switch
    {
        Verdict.Inspected => vendor is null ? "INSPECTED" : $"INSPECTED by {vendor}",
        Verdict.NotInspected => "NOT INSPECTED",
        Verdict.Untrusted => "UNTRUSTED CERTIFICATE",
        _ => "COULDN'T CHECK",
    };

    public static string Short(Verdict v) => v switch
    {
        Verdict.Inspected => "Inspected",
        Verdict.NotInspected => "Not inspected",
        Verdict.Untrusted => "Untrusted",
        _ => "Error",
    };

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
