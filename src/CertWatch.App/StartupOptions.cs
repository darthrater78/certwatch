using CertWatch.Core.Browsers;

namespace CertWatch.App;

/// <summary>
/// Command line (all optional; running browsers get a tab automatically):
///   --browser edge|chrome|brave|island|vivaldi|opera|firefox   always show this browser's tab (repeatable)
///   --process MyBrowser.exe [--firefox]                       add a tab for a browser not in the list
///   --interval 15                                          re-check the current site every N seconds
///   --db path\to\history.db                                history database (shared by default)
/// </summary>
internal sealed record StartupOptions(IReadOnlyList<BrowserDefinition> Browsers, TimeSpan RecheckInterval, string? DatabasePath, string? Error)
{
    public static StartupOptions Parse(string[] args)
    {
        var browsers = new List<BrowserDefinition>();
        string? process = null;
        var firefoxEngine = false;
        var interval = TimeSpan.FromSeconds(15);
        string? db = null;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--browser":
                    var id = Next();
                    var browser = id is null ? null : BrowserCatalog.Find(id);
                    if (browser is not null) browsers.Add(browser);
                    else error = $"Unknown browser \"{id}\". Known: {string.Join(", ", BrowserCatalog.All.Select(b => b.Id))}, or use --process.";
                    break;
                case "--process":
                    process = Next();
                    break;
                case "--firefox":
                    firefoxEngine = true;
                    break;
                case "--interval":
                    if (int.TryParse(Next(), out var seconds) && seconds >= 3) interval = TimeSpan.FromSeconds(seconds);
                    else error = "--interval needs a number of seconds (3 or more).";
                    break;
                case "--db":
                    db = Next();
                    break;
                default:
                    error = $"Unknown option \"{args[i]}\".";
                    break;
            }
        }

        if (process is not null)
            browsers.Add(BrowserCatalog.Custom(process, firefoxEngine ? BrowserEngine.Firefox : BrowserEngine.Chromium));

        return new StartupOptions(browsers, interval, db, error);
    }
}
