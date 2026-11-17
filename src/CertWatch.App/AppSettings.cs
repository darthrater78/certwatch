using System.IO;
using System.Text.Json;

namespace CertWatch.App;

public enum ThemeMode { System, Light, Dark }

/// <summary>User preferences, kept in %LOCALAPPDATA%\CertWatch\settings.json (nothing sensitive).</summary>
internal sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
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
