namespace CertWatch.Core.Browsers;

public readonly record struct SiteEndpoint(string Host, int Port)
{
    public override string ToString() => Port == 443 ? Host : $"{Host}:{Port}";
}

/// <summary>
/// Turns address-bar text into the HTTPS endpoint to check. Chromium hides "https://" and Firefox
/// may show it, so both forms are accepted. Non-web pages (edge://, about:, file:) and plain http
/// return null.
/// </summary>
public static class AddressParser
{
    public static SiteEndpoint? Parse(string? addressBarText)
    {
        if (string.IsNullOrWhiteSpace(addressBarText)) return null;
        var text = addressBarText.Trim();

        // Search terms typed into the bar, not an address.
        if (text.Contains(' ')) return null;

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            // about:blank, data:..., javascript:... have a scheme without "//".
            var colon = text.IndexOf(':');
            if (colon > 0 && !IsPortColon(text, colon)) return null;
            text = "https://" + text;
        }
        else
        {
            var scheme = text[..schemeEnd];
            if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;
        // A bare word ("intranet") is valid DNS but usually a search; require a dot or an IP.
        if (uri.HostNameType == UriHostNameType.Dns && !uri.Host.Contains('.')) return null;

        return new SiteEndpoint(uri.IdnHost.ToLowerInvariant(), uri.Port);
    }

    // "example.com:8443/..." — the colon is followed by digits, so it's a port, not a scheme.
    private static bool IsPortColon(string text, int colon)
    {
        var i = colon + 1;
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        return i > start && (i == text.Length || text[i] is '/' or '?' or '#');
    }
}
