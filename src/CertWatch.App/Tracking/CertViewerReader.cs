using System.Windows.Automation;
using CertWatch.Core.Browsers;

namespace CertWatch.App.Tracking;

/// <summary>
/// Reads the text of a browser's certificate viewer that the user opened, through UI Automation,
/// the same read-only way the address bar is read: it reads element names and never clicks,
/// focuses, types or switches tabs. Call off the UI thread (MTA).
/// </summary>
internal static class CertViewerReader
{
    private const int MaxElements = 400;
    private const int Attempts = 4;
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(400);

    /// <summary>The viewer's fingerprint and issuer for <paramref name="host"/>; null when it can't be read.</summary>
    public static CertViewerReading? Read(IntPtr window, string host)
    {
        // The dialog's content loads just after the window appears: try a few times.
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(Retry);
            var texts = Texts(window);
            if (texts is null) return null;
            var (sha, cn, o) = CertViewerText.Parse(texts);
            if (sha is not null) return new CertViewerReading(host, sha, cn, o, DateTimeOffset.Now);
        }
        return null;
    }

    private static List<string>? Texts(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var found = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var texts = new List<string>(Math.Min(found.Count, MaxElements));
            foreach (AutomationElement e in found)
            {
                if (texts.Count >= MaxElements) break;
                var name = e.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) texts.Add(name);
            }
            return texts;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null; // the viewer closed or was rebuilt
        }
    }
}
