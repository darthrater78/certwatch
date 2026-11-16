using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CertWatch.App.Tracking;
using CertWatch.Core.Trust;

namespace CertWatch.App.Presentation;

/// <summary>
/// One tray icon per CertWatch instance: a verdict-coloured disc with the followed browser's initial,
/// so two instances (e.g. Edge and Island) are told apart at a glance.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private IntPtr _currentHicon;
    private string _initial = "?";
    private Verdict? _verdict;
    private readonly WindowsNotifier? _toasts;

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

    public void SetBrowser(string displayName)
    {
        _initial = displayName.Length > 0 ? displayName[..1].ToUpperInvariant() : "?";
        Redraw();
    }

    public void SetState(Verdict? verdict, string tooltip)
    {
        _verdict = verdict;
        // NotifyIcon tooltips are limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..124] + "..." : tooltip;
        Redraw();
    }

    /// <summary>
    /// Shows a Windows notification (Action Center toast). Falls back to a tray balloon if toasts
    /// aren't available. Windows still hides both while Do not disturb / Focus is on.
    /// </summary>
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

    private void Redraw()
    {
        var c = VerdictStyle.ColorOf(_verdict);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(c.R, c.G, c.B));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(_initial, font);
            g.DrawString(_initial, font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }

        var hicon = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(hicon);
        old?.Dispose();
        if (_currentHicon != IntPtr.Zero) Native.DestroyIcon(_currentHicon);
        _currentHicon = hicon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentHicon != IntPtr.Zero) Native.DestroyIcon(_currentHicon);
    }
}
