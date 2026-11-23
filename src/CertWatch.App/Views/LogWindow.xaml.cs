using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CertWatch.App.Logging;

namespace CertWatch.App.Views;

public partial class LogWindow : Window
{
    private readonly ICollectionView _view;
    private readonly Action<bool, bool> _saveOptions;

    internal LogWindow(Action<bool, bool> saveOptions)
    {
        InitializeComponent();
        _saveOptions = saveOptions;
        _view = CollectionViewSource.GetDefaultView(LogService.Entries);
        _view.Filter = Matches;
        DataContext = _view;
        DebugBox.IsChecked = LogService.DebugDetail;
        FileBox.IsChecked = LogService.WriteToFile;
        LogService.Entries.CollectionChanged += OnNewEntries;
        Closed += (_, _) => LogService.Entries.CollectionChanged -= OnNewEntries;
        ThemeManager.Track(this);
    }

    private bool Matches(object o)
    {
        var text = FilterBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var e = (LogEntry)o;
        return e.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // Keep the newest entry in view.
    private void OnNewEntries(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && Grid.Items.Count > 0) Grid.ScrollIntoView(Grid.Items[^1]);
    }

    private void OnDebug(object sender, RoutedEventArgs e) { LogService.DebugDetail = DebugBox.IsChecked == true; Save(); }
    private void OnFile(object sender, RoutedEventArgs e) { LogService.WriteToFile = FileBox.IsChecked == true; Save(); }
    private void Save() => _saveOptions(LogService.DebugDetail, LogService.WriteToFile);
    private void OnFilter(object sender, TextChangedEventArgs e) => _view.Refresh();
    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(LogService.AllText());

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear the log, including any log files?", "CertWatch", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            LogService.Clear();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogService.LogDirectory);
        // explorer.exe with a folder path we built ourselves (no user input).
        // By full path: a bare name would also search CertWatch's own folder, which matters when it runs elevated.
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{LogService.LogDirectory}\"") { UseShellExecute = false });
    }
}
