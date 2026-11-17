namespace CertWatch.Core.Browsers;

/// <summary>
/// Which catalog browsers the user chose to monitor. Saved as browser ids; anything that isn't a
/// catalog id (an old or hand-edited settings file) is dropped, so only known browsers are followed.
/// </summary>
public static class BrowserSelection
{
    /// <summary>The known ids among <paramref name="ids"/>, once each, in catalog order. Null means none chosen.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids)
    {
        if (ids is null) return [];
        var wanted = new HashSet<string>(ids.Where(id => id is not null), StringComparer.OrdinalIgnoreCase);
        return BrowserCatalog.All.Where(b => wanted.Contains(b.Id)).Select(b => b.Id).ToList();
    }
}
