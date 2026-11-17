using System.IO;
using System.Text.Json;

namespace CertWatch.App;

// 1 and 2 match the old Light and Dark values in settings.json; 0 (old "System") or missing means "not chosen yet".
public enum ThemeMode { Flashbang = 1, Dark = 2 }

/// <summary>User preferences, kept in %LOCALAPPDATA%\CertWatch\settings.json (nothing sensitive).</summary>
internal sealed class AppSettings
{
    public ThemeMode? Theme { get; set; }
    public bool DebugLogging { get; set; }
    public bool LogToFile { get; set; }

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
