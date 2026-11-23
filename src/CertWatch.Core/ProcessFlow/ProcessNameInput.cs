namespace CertWatch.Core.ProcessFlow;

/// <summary>Turns a typed process name ("Java.exe", "\"my app\"") into the name Windows lists it under.</summary>
public static class ProcessNameInput
{
    public const int MaxLength = 128;
    private static readonly char[] NotInAFileName = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>The name without quotes, spaces around it or ".exe"; null when it can't be a process name.</summary>
    public static string? Normalize(string? text)
    {
        var name = text?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        if (name.Length is 0 or > MaxLength || name.Any(char.IsControl) || name.IndexOfAny(NotInAFileName) >= 0) return null;
        return name.All(c => c == '.') ? null : name;
    }
}
