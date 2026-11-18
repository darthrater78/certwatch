namespace CertWatch.Core.Browsers;

public enum BrowserEngine { Chromium, Firefox }

/// <summary>A browser CertWatch can follow, identified by its process name(s) (without ".exe").</summary>
public sealed record BrowserDefinition(
    string Id,
    string DisplayName,
    BrowserEngine Engine,
    IReadOnlyList<string> ProcessNames,
    string AccentHex,
    /// <summary>
    /// An enterprise browser with ZTNA built in: it can send a site along its own path while other apps
    /// on the PC (CertWatch included) are steered by the device agent. CertWatch then also checks the
    /// address the browser itself is connected to, and offers the certificate-viewer comparison.
    /// </summary>
    bool Enterprise = false);

public static class BrowserCatalog
{
    public static IReadOnlyList<BrowserDefinition> All { get; } =
    [
        new("edge",    "Edge",    BrowserEngine.Chromium, ["msedge"],  "#0A5FB4"),
        new("chrome",  "Chrome",  BrowserEngine.Chromium, ["chrome"],  "#2E7D32"),
        new("brave",   "Brave",   BrowserEngine.Chromium, ["brave"],   "#B23A12"),
        // Island's process name should be confirmed on the demo machine; override with --process if it differs.
        new("island",  "Island",  BrowserEngine.Chromium, ["Island", "island"], "#8A5200", Enterprise: true),
        new("vivaldi", "Vivaldi", BrowserEngine.Chromium, ["vivaldi"], "#A12A2A"),
        new("opera",   "Opera",   BrowserEngine.Chromium, ["opera"],   "#9C1C2B"),
        new("firefox", "Firefox", BrowserEngine.Firefox,  ["firefox"], "#6A3FB5"),
    ];

    public static BrowserDefinition? Find(string id) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A browser not in the catalog, followed by an explicit process name.</summary>
    public static BrowserDefinition Custom(string processName, BrowserEngine engine = BrowserEngine.Chromium)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return new BrowserDefinition(name.ToLowerInvariant(), name, engine, [name], "#3D3D3D");
    }
}
