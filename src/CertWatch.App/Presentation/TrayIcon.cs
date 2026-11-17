using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CertWatch.App.Tracking;

namespace CertWatch.App.Presentation;

/// <summary>
/// The tray icon: the CertWatch shield-and-split-path symbol on a tile coloured by the latest
/// verdict (slate while waiting). Windows notifications go through <see cref="WindowsNotifier"/>,
/// with tray balloons as the fallback.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly WindowsNotifier? _toasts;
    private IntPtr _currentHicon;
    private StatusKind _status = StatusKind.Neutral;

    public event Action? OpenRequested;
    public event Action? RecheckRequested;
    public event Action? ExitRequested;
    public event Action? TestNotificationRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open CertWatch", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Re-check now", null, (_, _) => RecheckRequested?.Invoke());
        menu.Items.Add("Send test notification", null, (_, _) => TestNotificationRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "CertWatch" };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        try
        {
            _toasts = new WindowsNotifier();
            _toasts.Activated += () => OpenRequested?.Invoke();
        }
        catch (Exception)
        {
            _toasts = null; // Toasts unavailable on this system: fall back to tray balloons.
        }
        Redraw();
    }

    public void SetState(StatusKind status, string tooltip)
    {
        _status = status;
        _icon.Text = tooltip.Length > 127 ? tooltip[..124] + "..." : tooltip; // NotifyIcon limit
        Redraw();
    }

    public void Notify(string title, string text, bool important)
    {
        try
        {
            if (_toasts is not null)
            {
                _toasts.Show(title, text, important ? "CertWatch · alert" : "CertWatch");
                return;
            }
        }
        catch (Exception)
        {
            // fall through to the balloon
        }
        _icon.ShowBalloonTip(5000, title, text, important ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private static Color TileColor(StatusKind s) => s switch
    {
        StatusKind.Inspected => Color.FromArgb(0x5B, 0x3F, 0xC4),
        StatusKind.Steered => Color.FromArgb(0x0A, 0x5F, 0xB4),
        StatusKind.Direct => Color.FromArgb(0x0E, 0x6B, 0x5C),
        StatusKind.Untrusted => Color.FromArgb(0xB3, 0x26, 0x1E),
        _ => Color.FromArgb(0x1F, 0x2A, 0x44),
    };

    /// <summary>Draws the symbol at 32px from the same 64-unit geometry as Assets/CertWatch.svg.</summary>
    private void Redraw()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(0.5f, 0.5f);
            using (var tile = RoundedRect(0, 0, 64, 64, 14))
            using (var fill = new SolidBrush(TileColor(_status)))
                g.FillPath(fill, tile);

            using var pen = new Pen(Color.White, 5.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using (var shield = new GraphicsPath())
            {
                shield.AddLine(32, 9, 50, 16);
                shield.AddLine(50, 16, 50, 31);
                shield.AddBezier(50, 31, 50, 43, 42, 51, 32, 55);
                shield.AddBezier(32, 55, 22, 51, 14, 43, 14, 31);
                shield.AddLine(14, 31, 14, 16);
                shield.CloseFigure();
                g.DrawPath(pen, shield);
            }
            g.DrawLine(pen, 32, 46, 32, 37);
            g.DrawLine(pen, 32, 37, 24, 28);
            g.DrawLine(pen, 32, 37, 40, 28);
        }

        var hicon = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(hicon);
        old?.Dispose();
        if (_currentHicon != IntPtr.Zero) Native.DestroyIcon(_currentHicon);
        _currentHicon = hicon;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        var d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentHicon != IntPtr.Zero) Native.DestroyIcon(_currentHicon);
    }
}
