using System.Security.Cryptography.X509Certificates;

namespace CertWatch.Core.Trust;

/// <summary>
/// Recognises the products behind common TLS-inspection roots by name. Only consulted for chains
/// that do NOT end at a public root, so a vendor's ordinary public certificates are never matched.
/// Patterns are matched case-insensitively against each chain certificate's subject and issuer.
/// </summary>
public static class InspectionVendors
{
    private static readonly (string Pattern, string Vendor)[] Patterns =
    [
        ("Zscaler", "Zscaler"),
        ("Netskope", "Netskope"),
        ("Palo Alto Networks", "Palo Alto Networks"),
        ("Prisma Access", "Palo Alto Networks"),
        ("Cloudflare", "Cloudflare"),
        ("Cisco Umbrella", "Cisco Umbrella"),
        ("Forcepoint", "Forcepoint"),
        ("Fortinet", "Fortinet"),
        ("FortiGate", "Fortinet"),
        ("Island", "Island"),
        ("iboss", "iboss"),
        ("Menlo Security", "Menlo Security"),
        ("Check Point", "Check Point"),
        ("Sophos", "Sophos"),
        ("Blue Coat", "Broadcom (Blue Coat)"),
        ("Symantec Web", "Broadcom (Symantec)"),
        ("Skyhigh", "Skyhigh Security"),
        ("Cato Networks", "Cato Networks"),
        ("Versa", "Versa Networks"),
    ];

    public static string? Identify(IReadOnlyList<X509Certificate2> chain)
    {
        foreach (var cert in chain)
        {
            foreach (var (pattern, vendor) in Patterns)
            {
                if (cert.Subject.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    cert.Issuer.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return vendor;
            }
        }
        return null;
    }
}
