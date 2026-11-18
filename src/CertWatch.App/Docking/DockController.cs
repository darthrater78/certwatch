using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CertWatch.Core.Docking;

namespace CertWatch.App.Docking;

/// <summary>
/// Places CertWatch's own window for the docked modes and tells the shell which window has focus.
/// <list type="bullet">
/// <item>Docked to the browser: watches the followed browser window's position (out-of-context window
/// events, nothing loaded into the browser) and moves CertWatch beside it.</item>
/// <item>Docked to the screen edge: registers CertWatch as an app bar, so Windows reserves the strip and
/// fits maximised windows around it, the same way it does for the taskbar.</item>
/// </list>
/// Observe-only: the browser window is never moved, resized, re-parented or activated.
/// Every member runs on the UI thread (window events arrive through its message loop).
/// </summary>
internal sealed unsafe class DockController : IDisposable
{
    /// <summary>Size of the docked layouts, in device-independent pixels.</summary>
    public const int SideWidth = 380, TopHeight = 64;

    public const string CoversHint = "No room beside the browser, so CertWatch is covering its edge. Make the browser window smaller, or dock to the screen edge.";
    public const string WaitingHint = "CertWatch moves next to the browser when its window appears.";

    private const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E;

    private static DockController? _instance;

    private readonly Window _window;
    private readonly Func<IntPtr> _fallbackTarget;
    private readonly DispatcherTimer _watchdog;
    private readonly uint _appBarMessage;
    private IntPtr _hwnd;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private uint _locationPid;
    private IntPtr _target;
    private bool _appBarRegistered;
    private IntPtr _edgeMonitorSource;
    private bool _hiddenForMinimized;
    private bool _keepOnTop;
    private bool _fullscreenApp;
    private bool _suspended;
    private string? _hint;

    /// <param name="window">CertWatch's main window, already shown or at least with a handle.</param>
    /// <param name="fallbackTarget">The selected browser's last known window, used when no window has been seen yet or the followed one closed.</param>
    public DockController(Window window, Func<IntPtr> fallbackTarget)
    {
        _window = window;
        _fallbackTarget = fallbackTarget;
        _appBarMessage = DockNative.RegisterWindowMessage("CertWatch.AppBar");
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnWatchdog(), window.Dispatcher);
        _window.DpiChanged += (_, _) => Place();
        _instance = this;
    }

    public DockPosition Position { get; private set; } = DockPosition.Floating;
    public DockAnchor Anchor { get; private set; } = DockAnchor.Browser;
    public bool IsDocked => Position != DockPosition.Floating;

    /// <summary>Raised when another process's window takes the foreground (its top-level window).</summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>A short note for the docked layout (covering the browser, waiting for it); null when there is nothing to say.</summary>
    public string? Hint
    {
        get => _hint;
        private set
        {
            if (_hint == value) return;
            _hint = value;
            HintChanged?.Invoke(value);
        }
    }
    public event Action<string?>? HintChanged;

    public bool KeepOnTop
    {
        get => _keepOnTop;
        set
        {
            _keepOnTop = value;
            ApplyTopmost();
        }
    }

    /// <summary>Hooks the window's messages and starts reporting the foreground window. Call once the window has a handle.</summary>
    public void Attach()
    {
        if (_hwnd != IntPtr.Zero) return;
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _foregroundHook = DockNative.SetWinEventHook(DockNative.EVENT_SYSTEM_FOREGROUND, DockNative.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            &OnWinEvent, 0, 0, DockNative.WINEVENT_OUTOFCONTEXT | DockNative.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>The browser window to dock beside. Zero means "use the fallback".</summary>
    public IntPtr Target
    {
        get => _target;
        set
        {
            var root = value == IntPtr.Zero ? IntPtr.Zero : DockNative.GetAncestor(value, DockNative.GA_ROOT);
            if (root == _target) return;
            _target = root;
            if (Position != DockPosition.Floating && Anchor == DockAnchor.Browser && !_suspended)
            {
                HookTargetLocation();
                Place();
            }
        }
    }

    /// <summary>
    /// A browser's main window: unowned and not a tool window, so a dialog, menu or extension pop-up
    /// of the browser never becomes the window CertWatch docks beside.
    /// </summary>
    public static bool IsDockable(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Tracking.Native.IsWindow(hwnd)
        && DockNative.GetWindow(hwnd, DockNative.GW_OWNER) == IntPtr.Zero
        && (DockNative.GetWindowLongPtr(hwnd, DockNative.GWL_EXSTYLE) & DockNative.WS_EX_TOOLWINDOW) == 0;

    /// <summary>
    /// CertWatch was hidden to the tray or minimised: stop placing it and give back a reserved edge,
    /// so no empty strip is left on screen. <see cref="Resume"/> docks it again.
    /// </summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        _watchdog.Stop();
        RemoveAppBar();
        UnhookTargetLocation();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        if (IsDocked) Apply(Position, Anchor);
    }

    /// <summary>Switches mode. The shell sets the window chrome first (no border when docked).</summary>
    public void Apply(DockPosition position, DockAnchor anchor)
    {
        _suspended = false;
        Position = DockLayout.Validate(position);
        Anchor = DockLayout.Validate(anchor);
        _fullscreenApp = false;
        ShowIfHiddenForMinimized();

        if (Position == DockPosition.Floating)
        {
            RemoveAppBar();
            UnhookTargetLocation();
            _watchdog.Stop();
            Hint = null;
        }
        else if (Anchor == DockAnchor.ScreenEdge)
        {
            UnhookTargetLocation();
            _watchdog.Stop();
            Hint = null;
            // Reserve the edge of the monitor the browser is on; after that, wherever CertWatch sits.
            _edgeMonitorSource = ValidTarget() is var t && t != IntPtr.Zero ? t : _hwnd;
            RemoveAppBar(); // the edge may have changed: register afresh
            RegisterAppBar();
            Place();
        }
        else
        {
            RemoveAppBar();
            HookTargetLocation();
            _watchdog.Start();
            Place();
        }
        ApplyTopmost();
    }

    /// <summary>Re-applies the placement for the current mode (after a theme or DPI change, a display change, a new target).</summary>
    public void Place()
    {
        if (_hwnd == IntPtr.Zero || Position == DockPosition.Floating || _suspended) return;
        try
        {
            if (Anchor == DockAnchor.ScreenEdge) PlaceAppBar();
            else PlaceBesideBrowser();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // A window vanished mid-call; the next event or the watchdog places it again.
        }
    }

    private void PlaceBesideBrowser()
    {
        var target = ValidTarget();
        if (target == IntPtr.Zero)
        {
            ShowIfHiddenForMinimized();
            Hint = WaitingHint;
            return;
        }
        if (target != _target)
        {
            _target = target;
            HookTargetLocation();
        }

        // The browser was minimised: get out of the way until it comes back.
        if (DockNative.IsIconic(target))
        {
            if (_window.IsVisible)
            {
                _hiddenForMinimized = true;
                _window.Hide();
            }
            return;
        }
        ShowIfHiddenForMinimized();

        if (DockNative.FrameOf(target) is not { } frame || DockNative.MonitorOf(target) is not { } monitor) return;
        var (side, top) = PixelSizes();
        var placement = DockLayout.Beside(frame, monitor.Work, Position, side, top);
        Move(placement.Bounds);
        Hint = placement.CoversBrowser ? CoversHint : null;
    }

    private void PlaceAppBar()
    {
        if (!_appBarRegistered) RegisterAppBar();
        if (DockNative.MonitorOf(_edgeMonitorSource) is not { } monitor) return;
        var (side, top) = PixelSizes();
        var data = NewAppBarData();
        data.uEdge = Position switch { DockPosition.Left => DockNative.ABE_LEFT, DockPosition.Top => DockNative.ABE_TOP, _ => DockNative.ABE_RIGHT };
        data.rc = DockNative.RECT.From(DockLayout.Edge(monitor.Monitor, Position, side, top));
        DockNative.SHAppBarMessage(DockNative.ABM_QUERYPOS, ref data);
        var bounds = DockLayout.Refit(data.rc.ToPixelRect(), Position, side, top);
        data.rc = DockNative.RECT.From(bounds);
        DockNative.SHAppBarMessage(DockNative.ABM_SETPOS, ref data);
        Move(bounds);
        _edgeMonitorSource = _hwnd;
    }

    private (int Side, int Top) PixelSizes()
    {
        var dpi = DockNative.GetDpiForWindow(_hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        return ((int)Math.Round(SideWidth * scale), (int)Math.Round(TopHeight * scale));
    }

    private void Move(PixelRect r) =>
        DockNative.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, DockNative.SWP_NOZORDER | DockNative.SWP_NOACTIVATE);

    private IntPtr ValidTarget()
    {
        if (_target != IntPtr.Zero && Tracking.Native.IsWindow(_target)) return _target;
        var fallback = _fallbackTarget();
        return fallback != IntPtr.Zero && Tracking.Native.IsWindow(fallback) ? DockNative.GetAncestor(fallback, DockNative.GA_ROOT) : IntPtr.Zero;
    }

    private void ShowIfHiddenForMinimized()
    {
        if (!_hiddenForMinimized) return;
        _hiddenForMinimized = false;
        _window.ShowActivated = false;
        _window.Show();
        _window.ShowActivated = true;
    }

    private void ApplyTopmost()
    {
        // An app bar stays on top like the taskbar, except while a full-screen app (a video, a slideshow) is up.
        var edge = Position != DockPosition.Floating && Anchor == DockAnchor.ScreenEdge;
        _window.Topmost = edge ? !_fullscreenApp : _keepOnTop;
    }

    private void OnWatchdog()
    {
        if (Position == DockPosition.Floating || Anchor != DockAnchor.Browser) return;
        // Catches a closed browser window, a missed event, or a browser that has started since.
        Place();
    }

    // ---- window events ----

    private void HookTargetLocation()
    {
        var pid = 0u;
        if (_target != IntPtr.Zero) Tracking.Native.GetWindowThreadProcessId(_target, out pid);
        if (pid == _locationPid && _locationHook != IntPtr.Zero) return;
        UnhookTargetLocation();
        if (pid == 0) return;
        // Only that browser process's location changes, so CertWatch isn't woken for every caret and tooltip on screen.
        _locationHook = DockNative.SetWinEventHook(DockNative.EVENT_OBJECT_LOCATIONCHANGE, DockNative.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            &OnWinEvent, pid, 0, DockNative.WINEVENT_OUTOFCONTEXT);
        _locationPid = pid;
    }

    private void UnhookTargetLocation()
    {
        if (_locationHook != IntPtr.Zero) DockNative.UnhookWinEvent(_locationHook);
        _locationHook = IntPtr.Zero;
        _locationPid = 0;
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            _instance?.OnEvent(eventType, hwnd, idObject, idChild);
        }
        catch (Exception)
        {
            // Never let an exception cross back into Windows' callback; the watchdog recovers.
        }
    }

    private void OnEvent(uint eventType, IntPtr hwnd, int idObject, int idChild)
    {
        if (hwnd == IntPtr.Zero || idObject != DockNative.OBJID_WINDOW || idChild != DockNative.CHILDID_SELF) return;
        if (eventType == DockNative.EVENT_SYSTEM_FOREGROUND)
        {
            var root = DockNative.GetAncestor(hwnd, DockNative.GA_ROOT);
            ForegroundChanged?.Invoke(root);
            // The followed browser came forward over a panel that covers its edge: keep the panel visible above it.
            if (root == _target && Hint == CoversHint && !_window.Topmost)
                DockNative.SetWindowPos(_hwnd, DockNative.HWND_TOP, 0, 0, 0, 0,
                    DockNative.SWP_NOMOVE | DockNative.SWP_NOSIZE | DockNative.SWP_NOACTIVATE);
        }
        else if (eventType == DockNative.EVENT_OBJECT_LOCATIONCHANGE && hwnd == _target && Anchor == DockAnchor.Browser && IsDocked)
        {
            Place();
        }
    }

    // ---- app bar ----

    private DockNative.APPBARDATA NewAppBarData() => new()
    {
        cbSize = (uint)sizeof(DockNative.APPBARDATA),
        hWnd = _hwnd,
        uCallbackMessage = _appBarMessage,
    };

    private void RegisterAppBar()
    {
        if (_appBarRegistered || _hwnd == IntPtr.Zero) return;
        var data = NewAppBarData();
        _appBarRegistered = DockNative.SHAppBarMessage(DockNative.ABM_NEW, ref data) != 0;
    }

    private void RemoveAppBar()
    {
        if (!_appBarRegistered) return;
        var data = NewAppBarData();
        DockNative.SHAppBarMessage(DockNative.ABM_REMOVE, ref data);
        _appBarRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBarRegistered && msg == (int)_appBarMessage)
        {
            switch ((int)wParam)
            {
                case DockNative.ABN_POSCHANGED: // the taskbar or another app bar moved
                    Place();
                    break;
                case DockNative.ABN_FULLSCREENAPP:
                    _fullscreenApp = lParam != IntPtr.Zero;
                    ApplyTopmost();
                    break;
            }
            handled = true;
        }
        else if (_appBarRegistered && msg is WM_ACTIVATE or WM_WINDOWPOSCHANGED)
        {
            var data = NewAppBarData();
            DockNative.SHAppBarMessage(msg == WM_ACTIVATE ? DockNative.ABM_ACTIVATE : DockNative.ABM_WINDOWPOSCHANGED, ref data);
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            _window.Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Gives the reserved screen edge back if CertWatch is crashing. Without it Windows would keep the
    /// strip reserved after the process is gone, until Explorer restarts.
    /// </summary>
    public static void ReleaseScreenEdge()
    {
        try
        {
            _instance?.RemoveAppBar();
        }
        catch (Exception)
        {
            // Best effort while crashing.
        }
    }

    public void Dispose()
    {
        _watchdog.Stop();
        RemoveAppBar();
        UnhookTargetLocation();
        if (_foregroundHook != IntPtr.Zero) DockNative.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        if (_instance == this) _instance = null;
    }
}
