using System.Globalization;
using System.Text;

namespace CertWatch.Core.ProcessFlow;

/// <summary>
/// How a process is shown: its friendly name and its exe. The friendly name comes from the exe's own
/// version resource, so any program can claim any name; the exe is always shown beside it.
/// </summary>
public sealed record ProcessIdentity(string ProcessName, string Exe, string? Product)
{
    public const int MaxTextLength = 80;

    /// <summary>The friendly name when the exe has one, otherwise the process name.</summary>
    public string DisplayName => Product ?? ProcessName;

    public string Label => Product is null ? Exe : $"{Product} ({Exe})";

    public static ProcessIdentity From(string processName, string? imagePath, string? description, string? productName = null)
    {
        var name = Clean(processName) ?? "Unknown";
        var exe = Clean(FileNameOf(imagePath)) ?? $"{name}.exe";
        // A field that only repeats the name adds nothing, so the next one gets a turn.
        string? Useful(string? text) =>
            Clean(text) is { } value && !value.Equals(name, StringComparison.OrdinalIgnoreCase) && !value.Equals(exe, StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
        return new ProcessIdentity(name, exe, Useful(description) ?? Useful(productName));
    }

    private static string? FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? path : path[(slash + 1)..];
    }

    // Exe metadata is untrusted: control and direction-override characters could disguise a name.
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kept = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (kept.Length == MaxTextLength) break;
            kept.Append(c);
        }
        var cleaned = kept.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
