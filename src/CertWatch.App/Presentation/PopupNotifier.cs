using System.Windows;
using CertWatch.App.Views;

namespace CertWatch.App.Presentation;

/// <summary>What one pop-up shows. <see cref="Status"/> picks the tile's colour and symbol.</summary>
public sealed record PopupContent(string Title, string Text, StatusKind Status);

/// <summary>
/// CertWatch's own notifications: cards in the bottom-right corner of the primary screen's work area,
/// newest at the bottom, stacked upward, at most <see cref="MaxVisible"/> at once. They are ordinary
/// topmost windows, so Windows' notification settings and Do not disturb don't hide them.
/// </summary>
internal sealed class PopupNotifier
{
    private const int MaxVisible = 4;
    private const double Gap = 8, Edge = 12;

    /// <summary>The durations offered, in seconds, in the order the header button cycles them; 0 = until dismissed.</summary>
    private static readonly int[] Choices = [0, 10, 30, 60];

    public static int Next(int seconds) => Choices[(Array.IndexOf(Choices, seconds) + 1) % Choices.Length];

    /// <summary>Unknown values (a hand-edited settings file) fall back to "until dismissed".</summary>
    public static TimeSpan? LifetimeFor(int seconds) => Choices.Contains(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    public static string Describe(int seconds) => LifetimeFor(seconds) switch
    {
        null => "Pop-ups: until dismissed",
        { TotalSeconds: 60 } => "Pop-ups: 1 min",
        { } t => $"Pop-ups: {t.TotalSeconds:0} s",
    };

    /// <summary>How long each new pop-up stays; null keeps it until dismissed.</summary>
    public TimeSpan? Lifetime { get; set; }

    private readonly List<PopupWindow> _open = [];

    /// <summary>Raised when a pop-up is clicked.</summary>
    public event Action? OpenRequested;

    public void Show(PopupContent content)
    {
        while (_open.Count >= MaxVisible) _open[0].Close(); // Closed handler removes it

        var popup = new PopupWindow(content, Lifetime);
        popup.Opened += () => OpenRequested?.Invoke();
        popup.Closed += (_, _) =>
        {
            _open.Remove(popup);
            Arrange();
        };
        ThemeManager.Track(popup);
        _open.Add(popup);
        popup.Left = -10000; // measured off-screen first, then placed by Arrange
        popup.Show();
        Arrange();
    }

    public void CloseAll()
    {
        foreach (var popup in _open.ToArray()) popup.Close();
    }

    private void Arrange()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - Edge;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var popup = _open[i];
            popup.UpdateLayout();
            popup.Left = work.Right - popup.ActualWidth - Edge;
            popup.Top = bottom - popup.ActualHeight;
            bottom = popup.Top - Gap;
        }
    }
}
