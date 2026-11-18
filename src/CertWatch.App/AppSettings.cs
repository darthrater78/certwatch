using System.IO;
using System.Text.Json;
using CertWatch.Core.Docking;

namespace CertWatch.App;

// 1 and 2 match the old Light and Dark values in settings.json; 0 (old "System") or missing means "not chosen yet".
public enum ThemeMode { Flashbang = 1, Dark = 2 }

/// <summary>User preferences, kept in %LOCALAPPDATA%\CertWatch\settings.json (nothing sensitive).</summary>
internal sealed class AppSettings
{
    public ThemeMode? Theme { get; set; }
    /// <summary>How long pop-ups stay, in seconds; 0 (the default) keeps them until dismissed.</summary>
    public int PopupSeconds { get; set; }
    public bool DebugLogging { get; set; }
    public bool LogToFile { get; set; }
    /// <summary>Ids of the browsers to monitor (BrowserCatalog). Null until the user first chooses: nothing is monitored.</summary>
    public List<string>? MonitoredBrowsers { get; set; }
    /// <summary>Floating window, or docked right/left/above the browser.</summary>
    public DockPosition Dock { get; set; }
    /// <summary>Docked to the browser window (moves with it) or to the screen edge (reserves space).</summary>
    public DockAnchor DockAnchor { get; set; }
    /// <summary>Switch to the tab of whichever monitored browser has focus.</summary>
    public bool FollowActiveBrowser { get; set; } = true;
    public bool KeepOnTop { get; set; }
    /// <summary>Read the certificate viewer the user opens in a monitored browser, and compare it with CertWatch's own connection. Off by default.</summary>
    public bool ReadCertificateViewer { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertWatch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
