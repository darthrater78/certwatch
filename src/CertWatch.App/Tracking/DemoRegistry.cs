using System.Collections.Concurrent;

namespace CertWatch.App.Tracking;

/// <summary>
/// Process IDs of running demo browsers. A demo tab follows only its own process; normal tabs skip
/// these, so the demo window never shows up under the normal tab for the same browser.
/// </summary>
internal static class DemoRegistry
{
    private static readonly ConcurrentDictionary<int, byte> Pids = new();

    public static void Add(int pid) => Pids[pid] = 0;
    public static void Remove(int pid) => Pids.TryRemove(pid, out _);
    public static bool IsDemo(int pid) => Pids.ContainsKey(pid);
}
