using CertWatch.Core.Docking;

namespace CertWatch.Core.Tests;

public class DockLayoutTests
{
    // A 1920x1080 monitor with a 48 px taskbar at the bottom.
    private static readonly PixelRect Work = new(0, 0, 1920, 1032);
    private const int Side = 380, Strip = 64;

    [Fact]
    public void Right_sits_beside_a_browser_that_leaves_room()
    {
        var browser = new PixelRect(100, 50, 1400, 950);
        var p = DockLayout.Beside(browser, Work, DockPosition.Right, Side, Strip);
        Assert.Equal(new PixelRect(1400, 50, 1780, 950), p.Bounds);
        Assert.False(p.CoversBrowser);
    }

    [Fact]
    public void Left_sits_beside_a_browser_that_leaves_room()
    {
        var browser = new PixelRect(500, 50, 1800, 950);
        var p = DockLayout.Beside(browser, Work, DockPosition.Left, Side, Strip);
        Assert.Equal(new PixelRect(120, 50, 500, 950), p.Bounds);
        Assert.False(p.CoversBrowser);
    }

    [Fact]
    public void Top_spans_the_browser_width_above_it()
    {
        var browser = new PixelRect(200, 300, 1600, 1000);
        var p = DockLayout.Beside(browser, Work, DockPosition.Top, Side, Strip);
        Assert.Equal(new PixelRect(200, 236, 1600, 300), p.Bounds);
        Assert.False(p.CoversBrowser);
    }

    [Theory]
    [InlineData(DockPosition.Right)]
    [InlineData(DockPosition.Left)]
    [InlineData(DockPosition.Top)]
    public void A_maximised_browser_leaves_no_room_so_the_panel_stays_on_screen_and_says_it_covers(DockPosition position)
    {
        var p = DockLayout.Beside(Work, Work, position, Side, Strip);
        Assert.True(p.CoversBrowser);
        Assert.True(p.Bounds.Left >= Work.Left && p.Bounds.Right <= Work.Right);
        Assert.True(p.Bounds.Top >= Work.Top && p.Bounds.Bottom <= Work.Bottom);
    }

    [Fact]
    public void A_short_browser_still_gets_a_usable_side_panel()
    {
        var browser = new PixelRect(100, 900, 1000, 1000);
        var p = DockLayout.Beside(browser, Work, DockPosition.Right, Side, Strip);
        Assert.Equal(DockLayout.MinSideHeight, p.Bounds.Height);
        Assert.True(p.Bounds.Bottom <= Work.Bottom);
    }

    [Fact]
    public void A_browser_partly_off_screen_keeps_the_panel_inside_the_work_area()
    {
        var browser = new PixelRect(-300, -20, 900, 1200);
        var p = DockLayout.Beside(browser, Work, DockPosition.Left, Side, Strip);
        Assert.Equal(Work.Left, p.Bounds.Left);
        Assert.Equal(Work.Top, p.Bounds.Top);
        Assert.Equal(Work.Bottom, p.Bounds.Bottom);
    }

    [Fact]
    public void Floating_has_no_docked_placement()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DockLayout.Beside(Work, Work, DockPosition.Floating, Side, Strip));
        Assert.Throws<ArgumentOutOfRangeException>(() => DockLayout.Edge(Work, DockPosition.Floating, Side, Strip));
    }

    [Fact]
    public void Screen_edge_strips_take_the_monitor_edge()
    {
        var monitor = new PixelRect(1920, 0, 3840, 1080);
        Assert.Equal(new PixelRect(3460, 0, 3840, 1080), DockLayout.Edge(monitor, DockPosition.Right, Side, Strip));
        Assert.Equal(new PixelRect(1920, 0, 2300, 1080), DockLayout.Edge(monitor, DockPosition.Left, Side, Strip));
        Assert.Equal(new PixelRect(1920, 0, 3840, 64), DockLayout.Edge(monitor, DockPosition.Top, Side, Strip));
    }

    [Fact]
    public void Refit_keeps_the_edge_windows_chose_and_restores_the_thickness()
    {
        // Windows moved a right-edge strip clear of a taskbar docked on the right.
        var adjusted = new PixelRect(1700, 0, 1872, 1080);
        Assert.Equal(new PixelRect(1492, 0, 1872, 1080), DockLayout.Refit(adjusted, DockPosition.Right, Side, Strip));
        Assert.Equal(new PixelRect(0, 48, 1920, 112), DockLayout.Refit(new PixelRect(0, 48, 1920, 80), DockPosition.Top, Side, Strip));
    }

    [Fact]
    public void Unknown_saved_values_fall_back_to_floating_and_browser()
    {
        Assert.Equal(DockPosition.Floating, DockLayout.Validate((DockPosition)42));
        Assert.Equal(DockAnchor.Browser, DockLayout.Validate((DockAnchor)(-1)));
        Assert.Equal(DockPosition.Top, DockLayout.Validate(DockPosition.Top));
    }
}
