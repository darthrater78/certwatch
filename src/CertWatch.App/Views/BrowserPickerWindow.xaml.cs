using System.ComponentModel;
using System.Windows;
using CertWatch.App.Presentation;

namespace CertWatch.App.Views;

/// <summary>
/// Picks several browsers at once; nothing changes until Apply, so tabs don't come and go while
/// the user is still choosing. Works on copies of the choices; Cancel leaves everything as it was.
/// </summary>
public partial class BrowserPickerWindow : Window
{
    private readonly List<BrowserChoice> _items;

    internal BrowserPickerWindow(IEnumerable<BrowserChoice> choices)
    {
        InitializeComponent();
        _items = choices.Select(c => new BrowserChoice(c.Id, c.Name, c.IsMonitored, c.IsRunning)).ToList();
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;
        DataContext = _items;
        UpdateApplyText();
        ThemeManager.Track(this);
    }

    /// <summary>The ticked browser ids after Apply; null after Cancel.</summary>
    public IReadOnlyList<string>? Selected { get; private set; }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserChoice.IsMonitored)) UpdateApplyText();
    }

    private void UpdateApplyText()
    {
        var n = _items.Count(i => i.IsMonitored);
        ApplyButton.Content = n switch { 0 => "Monitor none", 1 => "Monitor 1 browser", _ => $"Monitor {n} browsers" };
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var i in _items) i.IsMonitored = true; }
    private void OnClear(object sender, RoutedEventArgs e) { foreach (var i in _items) i.IsMonitored = false; }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Selected = _items.Where(i => i.IsMonitored).Select(i => i.Id).ToList();
        DialogResult = true;
    }
}
