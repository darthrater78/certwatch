using CertWatch.Core.Docking;

namespace CertWatch.Core.Tests;

public class AppBarGuardTests
{
    [Fact]
    public void A_short_burst_of_reservations_is_allowed()
    {
        var g = new AppBarGuard();
        for (var i = 0; i < 4; i++) Assert.True(g.AllowReservation(i * 100));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void A_fifth_reservation_within_ten_seconds_trips()
    {
        var g = new AppBarGuard();
        for (var i = 0; i < 4; i++) g.AllowReservation(i * 1000);
        Assert.False(g.AllowReservation(9_000));
        Assert.True(g.Tripped);
        Assert.Contains("reserved the screen edge 5 times in 10 s", g.Reason);
    }

    [Fact]
    public void A_slow_loop_that_a_per_second_limit_misses_still_trips()
    {
        // The lockup: a loop well under the old 20-a-second limit, running forever.
        var g = new AppBarGuard();
        var tripped = false;
        for (var t = 0L; t < 60_000 && !tripped; t += 70) tripped = !g.AllowReservation(t);
        Assert.True(tripped);
    }

    [Fact]
    public void A_slow_drip_trips_the_five_minute_budget()
    {
        var g = new AppBarGuard();
        var allowed = 0;
        for (var t = 0L; t < 300_000 && g.AllowReservation(t); t += 3_000) allowed++;
        Assert.Equal(20, allowed);
        Assert.True(g.Tripped);
    }

    [Fact]
    public void Occasional_reservations_never_trip()
    {
        var g = new AppBarGuard();
        for (var t = 0L; t < 24 * 3_600_000L; t += 20_000) Assert.True(g.AllowReservation(t));
    }

    [Fact]
    public void A_tripped_guard_stays_tripped()
    {
        var g = new AppBarGuard();
        for (var i = 0; i < 5; i++) g.AllowReservation(i);
        g.Restart();
        Assert.True(g.Tripped);
        Assert.False(g.AllowReservation(10_000_000));
        Assert.False(g.AllowMove(10_000_000));
    }

    [Fact]
    public void Restart_gives_a_deliberate_redock_a_fresh_budget()
    {
        var g = new AppBarGuard();
        for (var i = 0; i < 4; i++) g.AllowReservation(i);
        g.Restart();
        for (var i = 0; i < 4; i++) Assert.True(g.AllowReservation(10 + i));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void Window_moves_while_holding_the_edge_have_their_own_budget()
    {
        var g = new AppBarGuard();
        for (var i = 0; i < 12; i++) Assert.True(g.AllowMove(i * 100));
        Assert.False(g.AllowMove(1_500));
        Assert.True(g.Tripped);
        Assert.Contains("moved its docked window", g.Reason);
    }
}
