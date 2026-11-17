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

    /// <summary>
    /// For an address bar that has keyboard focus. While focused, Chromium shows a loaded page's full
    /// URL with "https://", but text the user is typing has no scheme ("ipchicken.c" on the way to
    /// "ipchicken.com"), so only text with an explicit https:// is taken as the page.
    /// </summary>
    public static SiteEndpoint? ParseFocused(string? addressBarText) =>
        addressBarText is not null && addressBarText.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Parse(addressBarText)
            : null;

    // "example.com:8443/..." — the colon is followed by digits, so it's a port, not a scheme.
    private static bool IsPortColon(string text, int colon)
    {
        var i = colon + 1;
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        return i > start && (i == text.Length || text[i] is '/' or '?' or '#');
    }
}
