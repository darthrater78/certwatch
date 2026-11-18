using System.Diagnostics;
using System.Windows.Automation;
using CertWatch.Core.Browsers;

namespace CertWatch.App.Tracking;

/// <summary>
/// Reads the address bar of one browser through UI Automation, without a browser extension.
/// Follows the browser's foreground window when it has focus, otherwise the last window of that
/// browser that did, so each CertWatch instance stays live even when its browser is in the background.
/// Must be called from a background (MTA) thread, never the UI thread.
/// </summary>
internal sealed class AddressBarReader(BrowserDefinition browser, Func<int, bool> acceptProcess)
{
    private readonly HashSet<string> _processNames = new(browser.ProcessNames, StringComparer.OrdinalIgnoreCase);
    // Written by the polling thread, read by the UI thread for docking.
    private volatile IntPtr _window;
    private AutomationElement? _addressBar;
    private string? _lastValue;
    private int _reads;
    private const int RefindEvery = 20; // about every 14 s at the default poll rate

    /// <summary>The browser window being followed (zero when none). Read-only use: docking places CertWatch beside it.</summary>
    public IntPtr Window => _window;

    /// <summary>Address-bar text of the followed window; null when the browser isn't running.</summary>
    public string? Read()
    {
        var window = PickWindow();
        if (window == IntPtr.Zero)
        {
            Reset();
            return null;
        }

        if (window != _window)
        {
            _window = window;
            _addressBar = null;
        }

        try
        {
            // Browsers sometimes rebuild the toolbar; look the address bar up again every so often.
            if (++_reads % RefindEvery == 0) _addressBar = null;
            _addressBar ??= FindAddressBar(window);
            if (_addressBar is null) return _lastValue;

            if (!_addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return _lastValue;
            var value = ((ValuePattern)pattern).Current.Value;

            // While the user is typing in the bar the text is a partial address or search, not the loaded
            // page. Accept focused text only when it is a full https:// URL (the bar kept focus after a load).
            if (_addressBar.Current.HasKeyboardFocus && AddressParser.ParseFocused(value) is null) return _lastValue;

            _lastValue = value;
            return _lastValue;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // The window or toolbar was rebuilt (e.g. tab dragged out, fullscreen). Find it again next time.
            _addressBar = null;
            return _lastValue;
        }
    }

    private void Reset()
    {
        _window = IntPtr.Zero;
        _addressBar = null;
        _lastValue = null;
    }

    private IntPtr PickWindow()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero && BelongsToBrowser(foreground)) return foreground;

        if (_window != IntPtr.Zero && Native.IsWindow(_window) && Native.IsWindowVisible(_window) && BelongsToBrowser(_window)) return _window;

        foreach (var name in _processNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero && acceptProcess(process.Id)) return process.MainWindowHandle;
                }
            }
        }
        return IntPtr.Zero;
    }

    private bool BelongsToBrowser(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return _processNames.Contains(process.ProcessName) && acceptProcess((int)pid);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private AutomationElement? FindAddressBar(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window);

        if (browser.Engine == BrowserEngine.Firefox)
        {
            var byId = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
            if (byId is not null) return byId;
        }

        // Chromium browsers: the omnibox is the first editable text field in the window, ahead of page
        // content in tree order. Matching by control type avoids depending on the localized name
        // ("Address and search bar" in English).
        return root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
    }
}
