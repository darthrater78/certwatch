using System.IO;
using System.Text.Json;
using System.Windows;

namespace CertWatch.App;

/// <summary>Remembers the main window's size, position and maximised state between runs.</summary>
internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CertWatch", "window.json");

    public static void Apply(Window window)
    {
        var saved = Load();
        var work = SystemParameters.WorkArea;
        if (saved is not null && IsOnScreen(saved))
        {
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = saved.Width;
            window.Height = saved.Height;
            if (saved.Maximized) window.WindowState = WindowState.Maximized;
            return;
        }

        // First run: most of the screen, centred, so nothing needs expanding.
        window.Width = Math.Min(1500, work.Width * 0.9);
        window.Height = work.Height * 0.9;
        window.Left = work.Left + (work.Width - window.Width) / 2;
        window.Top = work.Top + (work.Height - window.Height) / 2;
    }

    public static void Save(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 100) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new WindowPlacement(
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static WindowPlacement? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Guard against a saved position on a monitor that's no longer connected.
    private static bool IsOnScreen(WindowPlacement p) =>
        p.Width >= 560 && p.Height >= 400 &&
        p.Left + 100 > SystemParameters.VirtualScreenLeft &&
        p.Top >= SystemParameters.VirtualScreenTop - 10 &&
        p.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        p.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
}
