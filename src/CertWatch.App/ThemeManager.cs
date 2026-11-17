using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CertWatch.App;

/// <summary>
/// Swaps the colour dictionary (Themes/Flashbang.xaml or Themes/Dark.xaml) and darkens the window
/// title bars to match. With no saved choice, the first run follows Windows' app theme setting.
/// </summary>
internal static partial class ThemeManager
{
    private static ThemeMode _mode = ThemeMode.Flashbang;
    private static readonly List<Window> Windows = [];

    public static ThemeMode Mode => _mode;
    public static bool IsDark => _mode == ThemeMode.Dark;
    public static event Action? Changed;

    public static void Initialize(ThemeMode? saved)
    {
        _mode = saved is { } m && Enum.IsDefined(m) ? m : SystemUsesDarkTheme() ? ThemeMode.Dark : ThemeMode.Flashbang;
        Apply();
    }

    public static void Toggle()
    {
        _mode = IsDark ? ThemeMode.Flashbang : ThemeMode.Dark;
        Apply();
    }

    /// <summary>Registers a window so its title bar follows the theme.</summary>
    public static void Track(Window window)
    {
        Windows.Add(window);
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        window.Closed += (_, _) => Windows.Remove(window);
    }

    private static void Apply()
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"Themes/{_mode}.xaml", UriKind.Relative) };
        if (dictionaries.Count > 0) dictionaries[0] = theme;
        else dictionaries.Add(theme);
        foreach (var w in Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    private static bool SystemUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light
        && light == 0;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
