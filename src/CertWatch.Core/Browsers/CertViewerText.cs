using System.Text.RegularExpressions;

namespace CertWatch.Core.Browsers;

/// <summary>What a browser's own certificate viewer showed for a site, read from the window the user opened.</summary>
public sealed record CertViewerReading(string Host, string Sha256, string? IssuerCommonName, string? IssuerOrganization, DateTimeOffset ReadAt)
{
    /// <summary>"YE2 (Let's Encrypt)", "YE2", or null.</summary>
    public string? IssuerLabel => (IssuerCommonName, IssuerOrganization) switch
    {
        ({ } cn, { } o) when !string.Equals(cn, o, StringComparison.Ordinal) => $"{cn} ({o})",
        ({ } cn, _) => cn,
        (null, { } o) => o,
        _ => null,
    };

    /// <summary>Whether the browser saw the same leaf certificate as CertWatch's own connection.</summary>
    public bool Matches(string? leafFingerprint) =>
        leafFingerprint is not null && string.Equals(Sha256, leafFingerprint, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the text of a Chromium-family certificate viewer ("Certificate Viewer: claude.ai", General
/// tab: Issued To, Issued By, Validity Period, SHA-256 Fingerprints). The text is untrusted: only a
/// host name, a 64-digit hex fingerprint and two short issuer names are taken from it.
/// </summary>
public static partial class CertViewerText
{
    private const string TitlePrefix = "Certificate Viewer:";
    private const int MaxNameLength = 128;

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostName();

    /// <summary>The site from the viewer's window title, or null when the title isn't a certificate viewer's.</summary>
    public static string? HostFromTitle(string? title)
    {
        if (title is null || !title.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var host = title[TitlePrefix.Length..].Trim().ToLowerInvariant();
        return HostName().IsMatch(host) ? host : null;
    }

    /// <summary>
    /// The certificate's SHA-256 (the first 64-digit hex value; Chromium lists the certificate's
    /// before the public key's) and the Issued By names. Hex split over two text runs is joined.
    /// </summary>
    public static (string? Sha256, string? IssuerCommonName, string? IssuerOrganization) Parse(IReadOnlyList<string> texts)
    {
        return (FindSha256(texts), IssuedBy(texts, "Common Name (CN)"), IssuedBy(texts, "Organization (O)"));
    }

    private static string? FindSha256(IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
        {
            var one = Compact(texts[i]);
            if (Sha256Hex().IsMatch(one)) return one.ToUpperInvariant();
            if (i + 1 < texts.Count && one.Length is > 0 and < 64)
            {
                var two = one + Compact(texts[i + 1]);
                if (Sha256Hex().IsMatch(two)) return two.ToUpperInvariant();
            }
        }
        return null;
    }

    private static string? IssuedBy(IReadOnlyList<string> texts, string label)
    {
        var start = IndexOf(texts, "Issued By", 0);
        if (start < 0) return null;
        var end = IndexOf(texts, "Validity Period", start);
        if (end < 0) end = texts.Count;
        var at = IndexOf(texts, label, start);
        if (at < 0 || at + 1 >= end) return null;
        var value = texts[at + 1].Trim();
        if (value.Length == 0 || value.StartsWith('<') || value.Length > MaxNameLength) return null; // "<Not Part Of Certificate>"
        return value;
    }

    private static int IndexOf(IReadOnlyList<string> texts, string value, int from)
    {
        for (var i = from; i < texts.Count; i++)
            if (string.Equals(texts[i].Trim(), value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c) && c != ':'));
}
