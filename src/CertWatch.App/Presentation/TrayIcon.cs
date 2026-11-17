using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CertWatch.App.Tracking;
using static CertWatch.App.Presentation.TrayNative;

namespace CertWatch.App.Presentation;

/// <summary>
/// The tray icon: the CertWatch shield-and-split-path symbol on a tile coloured by the latest
/// verdict (slate while waiting), drawn with WPF and shown through Shell_NotifyIcon. Notifications
/// are the icon's balloons, which Windows 10 and 11 show as standard notifications.
/// Must be created and used on the UI thread.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const int IconSize = 32;
    private enum MenuCommand { Open = 1, Recheck, TestNotification, Exit }

    private static readonly Geometry Shield = Geometry.Parse("M32,9 L50,16 L50,31 C50,43 42,51 32,55 C22,51 14,43 14,31 L14,16 Z");
    private static readonly Geometry SplitPath = Geometry.Parse("M32,46 L32,37 L24,28 M32,37 L40,28");

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private IntPtr _hicon;
    private string _tooltip = "CertWatch";
    private StatusKind _status = StatusKind.Neutral;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? RecheckRequested;
    public event Action? ExitRequested;
    public event Action? TestNotificationRequested;

    public TrayIcon()
    {
        // A hidden top-level window (not message-only: a message-only window can't take the
        // foreground, and without that the tray menu doesn't close when you click elsewhere).
        _window = new HwndSource(new HwndSourceParameters("CertWatch tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hicon = RenderIcon(_status);
        Add();
    }

    public void SetState(StatusKind status, string tooltip)
    {
        _status = status;
        _tooltip = tooltip;
        var old = _hicon;
        _hicon = RenderIcon(status);
        var data = NewData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    /// <summary>Shows a notification. Alerts use the Windows warning glyph; the rest show the verdict tile.</summary>
    public void Notify(string title, string text, bool important)
    {
        var data = NewData(NIF_INFO);
        Copy(title, data.szInfoTitle, InfoTitleChars);
        Copy(text, data.szInfo, InfoChars);
        data.dwInfoFlags = NIIF_RESPECT_QUIET_TIME | (important ? NIIF_WARNING : NIIF_USER | NIIF_LARGE_ICON);
        data.hBalloonIcon = important ? IntPtr.Zero : _hicon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _hicon,
        };
        // Tooltip limit is 127 characters; cut long ones visibly.
        Copy(_tooltip.Length > TipChars - 1 ? _tooltip[..(TipChars - 4)] + "..." : _tooltip, data.szTip, TipChars);
        return data;
    }

    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // Explorer restarted: the icon is gone until re-added.
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage) return IntPtr.Zero;

        handled = true;
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case WM_LBUTTONDBLCLK:
            case NIN_KEYSELECT:
            case NIN_BALLOONUSERCLICK:
                OpenRequested?.Invoke();
                break;
            case WM_CONTEXTMENU:
                // Under NOTIFYICON_VERSION_4, wParam carries the anchor point (also right for keyboard use).
                var anchor = wParam.ToInt64();
                ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, (nuint)MenuCommand.Open, "Open CertWatch");
            AppendMenu(menu, MF_STRING, (nuint)MenuCommand.Recheck, "Re-check now");
            AppendMenu(menu, MF_STRING, (nuint)MenuCommand.TestNotification, "Send test notification");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, (nuint)MenuCommand.Exit, "Exit");

            // The documented dance for tray menus: foreground first, WM_NULL after, or the menu sticks.
            SetForegroundWindow(_window.Handle);
            var command = (MenuCommand)TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, x, y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, 0, 0);

            switch (command)
            {
                case MenuCommand.Open: OpenRequested?.Invoke(); break;
                case MenuCommand.Recheck: RecheckRequested?.Invoke(); break;
                case MenuCommand.TestNotification: TestNotificationRequested?.Invoke(); break;
                case MenuCommand.Exit: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static Color TileColor(StatusKind s) => s switch
    {
        StatusKind.Inspected => Color.FromRgb(0x5B, 0x3F, 0xC4),
        StatusKind.Steered => Color.FromRgb(0x0A, 0x5F, 0xB4),
        StatusKind.Direct => Color.FromRgb(0x0E, 0x6B, 0x5C),
        StatusKind.Untrusted => Color.FromRgb(0xB3, 0x26, 0x1E),
        _ => Color.FromRgb(0x1F, 0x2A, 0x44),
    };

    /// <summary>Draws the symbol from the same 64-unit geometry as Assets/CertWatch.svg and returns an HICON.</summary>
    private static IntPtr RenderIcon(StatusKind status)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(IconSize / 64.0, IconSize / 64.0));
            dc.DrawRoundedRectangle(new SolidColorBrush(TileColor(status)), null, new Rect(0, 0, 64, 64), 14, 14);
            var pen = new Pen(Brushes.White, 5.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Shield);
            dc.DrawGeometry(null, pen, SplitPath);
        }
        var bitmap = new RenderTargetBitmap(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var bytes = png.ToArray();
        fixed (byte* p = bytes)
        {
            return CreateIconFromResourceEx(p, (uint)bytes.Length, true, 0x00030000, IconSize, IconSize, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_hicon != IntPtr.Zero) Native.DestroyIcon(_hicon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
