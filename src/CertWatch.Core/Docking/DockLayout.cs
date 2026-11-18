namespace CertWatch.Core.Docking;

/// <summary>Where the CertWatch window sits. Saved in settings.json as a number, so keep the values.</summary>
public enum DockPosition { Floating = 0, Right = 1, Left = 2, Top = 3 }

/// <summary>What a docked CertWatch window is docked to.</summary>
public enum DockAnchor
{
    /// <summary>Sits beside the followed browser window and moves with it. Nothing is reserved.</summary>
    Browser = 0,
    /// <summary>Reserves a strip of the screen edge, like the taskbar; Windows fits maximised windows around it.</summary>
    ScreenEdge = 1,
}

/// <summary>A screen rectangle in physical pixels (Win32 RECT: right and bottom are exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(PixelRect other) =>
        !IsEmpty && !other.IsEmpty && Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>A docked window's bounds, and whether it had to cover part of the browser to stay on screen.</summary>
public readonly record struct DockPlacement(PixelRect Bounds, bool CoversBrowser);

/// <summary>
/// Pure geometry for the docked modes, so it can be tested without windows. CertWatch only ever moves
/// its own window: the browser window is read, never moved or resized.
/// </summary>
public static class DockLayout
{
    /// <summary>Shortest a side panel may be when the browser window is short.</summary>
    public const int MinSideHeight = 320;

    /// <summary>
    /// Beside the browser window (<paramref name="browser"/>, its visible frame), kept inside the work
    /// area of the browser's monitor. When there is no room beside it (a maximised browser) the panel
    /// stays on screen over the browser's edge and <see cref="DockPlacement.CoversBrowser"/> is true.
    /// </summary>
    public static DockPlacement Beside(PixelRect browser, PixelRect workArea, DockPosition position, int sideWidth, int topHeight)
    {
        if (position == DockPosition.Floating) throw new ArgumentOutOfRangeException(nameof(position), "Floating has no docked placement.");
        if (workArea.IsEmpty) throw new ArgumentException("Empty work area.", nameof(workArea));

        PixelRect bounds;
        if (position == DockPosition.Top)
        {
            var height = Math.Min(topHeight, workArea.Height);
            var left = Math.Max(browser.Left, workArea.Left);
            var right = Math.Min(browser.Right, workArea.Right);
            if (right - left < sideWidth) (left, right) = SpanWithin(left, sideWidth, workArea.Left, workArea.Right);
            var top = Clamp(browser.Top - height, workArea.Top, workArea.Bottom - height);
            bounds = new PixelRect(left, top, right, top + height);
        }
        else
        {
            var width = Math.Min(sideWidth, workArea.Width);
            var top = Math.Max(browser.Top, workArea.Top);
            var bottom = Math.Min(browser.Bottom, workArea.Bottom);
            if (bottom - top < MinSideHeight) (top, bottom) = SpanWithin(top, MinSideHeight, workArea.Top, workArea.Bottom);
            var wanted = position == DockPosition.Right ? browser.Right : browser.Left - width;
            var left = Clamp(wanted, workArea.Left, workArea.Right - width);
            bounds = new PixelRect(left, top, left + width, bottom);
        }
        return new DockPlacement(bounds, bounds.Intersects(browser));
    }

    /// <summary>
    /// The strip to ask Windows to reserve along one edge of a monitor (<paramref name="monitor"/>, the
    /// whole monitor, not its work area: Windows trims it around the taskbar and other app bars).
    /// </summary>
    public static PixelRect Edge(PixelRect monitor, DockPosition position, int sideWidth, int topHeight) => position switch
    {
        DockPosition.Left => monitor with { Right = monitor.Left + Math.Min(sideWidth, monitor.Width) },
        DockPosition.Right => monitor with { Left = monitor.Right - Math.Min(sideWidth, monitor.Width) },
        DockPosition.Top => monitor with { Bottom = monitor.Top + Math.Min(topHeight, monitor.Height) },
        _ => throw new ArgumentOutOfRangeException(nameof(position), "Floating has no screen edge."),
    };

    /// <summary>
    /// After Windows has adjusted a proposed strip (moved it clear of the taskbar), keep the edge it
    /// settled on and put the thickness back.
    /// </summary>
    public static PixelRect Refit(PixelRect adjusted, DockPosition position, int sideWidth, int topHeight) => position switch
    {
        DockPosition.Left => adjusted with { Right = adjusted.Left + sideWidth },
        DockPosition.Right => adjusted with { Left = adjusted.Right - sideWidth },
        DockPosition.Top => adjusted with { Bottom = adjusted.Top + topHeight },
        _ => throw new ArgumentOutOfRangeException(nameof(position), "Floating has no screen edge."),
    };

    /// <summary>Saved values from settings.json, with anything unknown treated as floating.</summary>
    public static DockPosition Validate(DockPosition value) => Enum.IsDefined(value) ? value : DockPosition.Floating;
    public static DockAnchor Validate(DockAnchor value) => Enum.IsDefined(value) ? value : DockAnchor.Browser;

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    // A span of the given length starting at start, moved back inside [min, max) when it would run past max.
    private static (int Start, int End) SpanWithin(int start, int length, int min, int max)
    {
        length = Math.Min(length, max - min);
        var s = Clamp(start, min, max - length);
        return (s, s + length);
    }
}
