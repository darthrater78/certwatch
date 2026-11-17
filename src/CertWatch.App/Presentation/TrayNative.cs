using System.Runtime.InteropServices;

namespace CertWatch.App.Presentation;

/// <summary>
/// The Win32 calls behind <see cref="TrayIcon"/>: the notification-area icon (Shell_NotifyIcon),
/// its pop-up menu, and an HICON built from PNG bytes. Replaces WinForms' NotifyIcon, which
/// pulled ~26 MB of WinForms into the self-contained build.
/// </summary>
internal static unsafe partial class TrayNative
{
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    internal const uint NOTIFYICON_VERSION_4 = 4;

    // Callback events (low word of lParam under NOTIFYICON_VERSION_4).
    internal const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203;
    internal const int NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;

    internal const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    internal const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    internal const uint WM_NULL = 0x0;

    internal const int TipChars = 128, InfoChars = 256, InfoTitleChars = 64;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[TipChars];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[InfoChars];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[InfoTitleChars];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>Copies <paramref name="value"/> into a fixed buffer, truncated and NUL-terminated.</summary>
    internal static void Copy(string value, char* dest, int capacity)
    {
        var n = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, n).CopyTo(new Span<char>(dest, n));
        dest[n] = '\0';
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Builds an icon from PNG bytes (supported by Windows since Vista).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr CreateIconFromResourceEx(byte* bits, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool isIcon, uint version, int cx, int cy, uint flags);
}
