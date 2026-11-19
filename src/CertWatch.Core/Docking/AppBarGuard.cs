namespace CertWatch.Core.Docking;

/// <summary>
/// Budget for screen-edge docking. Reserving the edge (ABM_SETPOS) changes the desktop work area, and
/// Windows then resizes every maximised window of every app, so a reserve/notify feedback loop can
/// freeze the whole computer even at a few rounds a second. Anything that isn't the user's own action
/// gets a small rolling budget; once it is spent the guard trips and stays tripped for the session.
/// A real app bar re-reserves only when the taskbar, another app bar or a display changes.
/// </summary>
public sealed class AppBarGuard
{
    /// <summary>Edge reservations allowed: a short burst (display change, DPI change) and a slow drip.</summary>
    public static readonly (int Max, long WindowMs)[] ReservationBudget = [(4, 10_000), (20, 300_000)];

    /// <summary>Moves of CertWatch's own window while it holds the edge (it should stay put).</summary>
    public static readonly (int Max, long WindowMs)[] MoveBudget = [(12, 2_000)];

    private readonly Queue<long> _reservations = new();
    private readonly Queue<long> _moves = new();

    public bool Tripped { get; private set; }

    /// <summary>Why the guard tripped, for the log and the docked layout's note; null until it trips.</summary>
    public string? Reason { get; private set; }

    /// <summary>True when the edge may be reserved now; false (and tripped) when the budget is spent.</summary>
    public bool AllowReservation(long nowMs) => Allow(_reservations, ReservationBudget, nowMs, "reserved the screen edge");

    /// <summary>True when CertWatch's window may be moved now; false (and tripped) when the budget is spent.</summary>
    public bool AllowMove(long nowMs) => Allow(_moves, MoveBudget, nowMs, "moved its docked window");

    /// <summary>
    /// The user docked (or re-docked) on purpose: start counting afresh. A trip is not undone; it lasts
    /// until CertWatch restarts. Never call this from the notification path, or a loop resets its own budget.
    /// </summary>
    public void Restart()
    {
        _reservations.Clear();
        _moves.Clear();
    }

    private bool Allow(Queue<long> log, (int Max, long WindowMs)[] budget, long now, string what)
    {
        if (Tripped) return false;
        var longest = budget.Max(b => b.WindowMs);
        while (log.Count > 0 && now - log.Peek() >= longest) log.Dequeue();
        foreach (var (max, windowMs) in budget)
        {
            var recent = log.Count(t => now - t < windowMs);
            if (recent < max) continue;
            Tripped = true;
            Reason = $"it {what} {recent + 1} times in {windowMs / 1000} s";
            return false;
        }
        log.Enqueue(now);
        return true;
    }
}
