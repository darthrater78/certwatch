using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CertWatch.App.Presentation;

namespace CertWatch.App.Views;

/// <summary>One pop-up card. <see cref="PopupNotifier"/> places, stacks and closes them.</summary>
public partial class PopupWindow : Window
{
    private readonly DispatcherTimer _timer;

    internal PopupWindow(PopupContent content, TimeSpan lifetime)
    {
        InitializeComponent();
        DataContext = content;
        _timer = new DispatcherTimer { Interval = lifetime };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) => _timer.Start();
        // Stays while the mouse is over it, so it can be read; the countdown restarts on leaving.
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        MouseLeftButtonUp += OnClick;
        SourceInitialized += (_, _) => RoundCorners();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Raised when the card (not its close button) is clicked.</summary>
    internal event Action? Opened;

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        Opened?.Invoke();
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();

    // Windows 11 rounds the corners of a borderless window when asked; older Windows ignores it.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void RoundCorners()
    {
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
