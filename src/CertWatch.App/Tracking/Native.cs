using System.Runtime.InteropServices;

namespace CertWatch.App.Tracking;

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    /// <summary>A window's title (read-only), at most 255 characters.</summary>
    internal static string WindowTitle(IntPtr hWnd)
    {
        var buffer = new char[256];
        var n = GetWindowText(hWnd, buffer, buffer.Length);
        return n <= 0 ? "" : new string(buffer, 0, Math.Min(n, buffer.Length));
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);
}
