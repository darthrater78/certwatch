using System.Runtime.InteropServices;
using CertWatch.Core.Docking;

namespace CertWatch.App.Docking;

/// <summary>
/// Win32 calls behind docking. Everything here either reads another window's position or moves
/// CertWatch's own window; nothing moves, resizes, owns or injects into a browser window.
/// </summary>
internal static unsafe partial class DockNative
{
    // ---- window events (out of context: no code is loaded into the browser) ----
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    internal const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> callback, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- reading windows ----
    internal const uint GA_ROOT = 2;

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    internal const uint GW_OWNER = 4;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x80;

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(IntPtr hWnd, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    /// <summary>The window's visible frame (without the invisible resize borders), or its window rect.</summary>
    internal static PixelRect? FrameOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, sizeof(RECT)) == 0) return r.ToPixelRect();
        return GetWindowRect(hwnd, out r) ? r.ToPixelRect() : null;
    }

    // ---- monitors ----
    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>The whole monitor and its work area for the monitor nearest the window (primary when none).</summary>
    internal static (PixelRect Monitor, PixelRect Work)? MonitorOf(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? MONITOR_DEFAULTTOPRIMARY : MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        return (info.rcMonitor.ToPixelRect(), info.rcWork.ToPixelRect());
    }

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// The effective DPI of the monitor nearest the window (96 when unknown). Screen-edge sizes come from the
    /// monitor, not from CertWatch's window, whose DPI changes while it moves between monitors.
    /// </summary>
    internal static uint MonitorDpiOf(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? MONITOR_DEFAULTTOPRIMARY : MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0 && x > 0 ? x : 96;
    }

    // ---- moving CertWatch's own window ----
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    // ---- app bar (screen-edge docking) ----
    internal const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    internal const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2;
    internal const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    [LibraryImport("shell32.dll")]
    internal static partial nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
        public static RECT From(PixelRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }
}
