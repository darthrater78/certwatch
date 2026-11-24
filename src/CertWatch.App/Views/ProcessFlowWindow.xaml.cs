using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.Core.Network;
using CertWatch.Core.ProcessFlow;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App.Views;

/// <summary>Targets one process and answers, per destination, whether it looks pinned and what to do next.</summary>
public partial class ProcessFlowWindow : Window
{
    private const string IdleText = "Choose a process and press Start.";

    private readonly ICertProbe _probe;
    private readonly InspectionClassifier _classifier;
    private readonly IRouteLookup _routes;
    private readonly ObservableCollection<FlowRow> _rows = [];
    private readonly ListCollectionView _view;
    private readonly bool _elevated = RawHandshakeCapture.IsElevated;
    // Names typed this run. They stay in the picker whether or not the process is running.
    private readonly List<string> _addedNames = [];
    private ProcessFlowSession? _session;
    private List<ProcessChoice> _choices = [];
    private ProcessChoice? _choice;
    private PinningGroup? _groupFilter;

    internal ProcessFlowWindow(ICertProbe probe, InspectionClassifier classifier, IRouteLookup routes)
    {
        InitializeComponent();
        _probe = probe;
        _classifier = classifier;
        _routes = routes;
        _view = BuildView();
        Flows.ItemsSource = _view;
        CaptureBox.IsEnabled = _elevated;
        CaptureHint.Text = _elevated
            ? "Off by default. Reads only this process's TLS handshakes, in memory. Never decrypts or alters traffic."
            : "Start CertWatch as administrator to use this. Without it, CertWatch can't tell whether a process is pinned.";
        UpdateFilterButtons();
        LoadProcesses();
        Closed += (_, _) => StopSession();
        ThemeManager.Track(this);
    }

    // What needs attention stays on top, and rows move as their answers change.
    private ListCollectionView BuildView()
    {
        var view = new ListCollectionView(_rows) { Filter = Matches, IsLiveSorting = true, IsLiveFiltering = true };
        view.SortDescriptions.Add(new SortDescription(nameof(FlowRow.Group), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(FlowRow.Host), ListSortDirection.Ascending));
        view.LiveSortingProperties.Add(nameof(FlowRow.Group));
        view.LiveSortingProperties.Add(nameof(FlowRow.Host));
        view.LiveFilteringProperties.Add(nameof(FlowRow.Group));
        view.LiveFilteringProperties.Add(nameof(FlowRow.Open));
        view.LiveFilteringProperties.Add(nameof(FlowRow.Issuer));
        return view;
    }

    private bool Matches(object item)
    {
        var row = (FlowRow)item;
        if (_groupFilter is { } group && row.Group != group) return false;
        if (OpenOnlyBox.IsChecked == true && row.Open == 0) return false;
        var text = SearchBox.Text.Trim();
        return text.Length == 0
            || row.ProcessDisplay.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Exe.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Host.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Remote.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Issuer.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadProcesses()
    {
        var previous = _choice;
        var own = Environment.ProcessId;
        Dictionary<int, int> counts;
        try
        {
            counts = TcpConnections.CountsByProcess();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            StatusText.Text = $"Couldn't read the connection table: {ex.Message}";
            return;
        }
        var choices = new List<ProcessChoice>();
        foreach (var (pid, count) in counts)
        {
            if (pid is 0 or 4 || pid == own) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                var name = process.ProcessName;
                choices.Add(new ProcessChoice(name, pid, count, Identity: ProcessIdentityReader.Read(pid, name).Label));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // It exited while listing.
            }
        }
        var running = choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ProcessId).ToList();
        var countByName = running.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Connections), StringComparer.OrdinalIgnoreCase);
        var added = _addedNames.Select(n => new ProcessChoice(n, 0, countByName.GetValueOrDefault(n), true,
            Identity: running.Find(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))?.Identity));
        var all = new ProcessChoice(ProcessChoice.AllName, -1, running.Sum(c => c.Connections), IsAll: true);
        _choices = [all, .. added, .. running];
        Choose(previous is null ? null : _choices.FirstOrDefault(c => c.ProcessId == previous.ProcessId
            && string.Equals(c.Name, previous.Name, StringComparison.OrdinalIgnoreCase)));
        if (_session is null) StatusText.Text = $"{running.Count} processes have open connections.";
    }

    private void Choose(ProcessChoice? choice)
    {
        _choice = choice;
        ProcessButton.Content = choice?.Label ?? "Choose a process…";
        // A name has no single process id, so it always follows every process of that name.
        if (choice is { IsAdded: true } or { IsAll: true }) AllNamedBox.IsChecked = true;
        // The checkbox states exactly whose handshakes it reads.
        CaptureBox.Content = choice?.IsAll == true
            ? "Read _handshakes of all processes (needed to detect pinning)"
            : "Read this process's _handshakes (needed to detect pinning)";
        UpdateControls();
    }

    private void UpdateControls()
    {
        var idle = _session is null;
        ProcessButton.IsEnabled = NameBox.IsEnabled = AddButton.IsEnabled = idle;
        AllNamedBox.IsEnabled = idle && _choice is not ({ IsAdded: true } or { IsAll: true });
    }

    private void OnChooseProcess(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
        var menu = new ContextMenu { PlacementTarget = ProcessButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 420 };
        foreach (var choice in _choices)
        {
            var c = choice;
            // Process names are data: no access-key parsing of an underscore in the name.
            var item = new MenuItem { Header = new TextBlock { Text = c.Label }, IsChecked = c == _choice };
            item.Click += (_, _) => Choose(c);
            menu.Items.Add(item);
            if (c.IsAll) menu.Items.Add(new Separator());
        }
        if (_choices.Count == 1) menu.Items.Add(new MenuItem { Header = "No processes have open connections", IsEnabled = false });
        if (_addedNames.Count > 0)
        {
            var clearNames = new MenuItem { Header = "Clear added names" };
            clearNames.Click += (_, _) => ClearAddedNames();
            menu.Items.Add(new Separator());
            menu.Items.Add(clearNames);
        }
        menu.IsOpen = true;
    }

    private void OnNameKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        AddName();
        e.Handled = true;
    }

    private void OnAddName(object sender, RoutedEventArgs e) => AddName();

    private void AddName()
    {
        if (ProcessNameInput.Normalize(NameBox.Text) is not { } name)
        {
            StatusText.Text = "Enter a process name, such as java or app.exe, without a folder path.";
            return;
        }
        using (var self = Process.GetCurrentProcess())
        {
            if (string.Equals(name, self.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "CertWatch can't follow itself: its own handshakes would appear as the process's traffic.";
                return;
            }
        }
        var existing = _addedNames.Find(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _addedNames.Add(name);
        var key = existing ?? name;
        _choice = new ProcessChoice(key, 0, 0, true);
        LoadProcesses();
        NameBox.Clear();
        StatusText.Text = $"{key} added. Press Start; it doesn't need to be running yet.";
    }

    private void ClearAddedNames()
    {
        _addedNames.Clear();
        LoadProcesses();
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            StopSession();
            StatusText.Text = "Stopped. The results stay until the next Start or Clear list.";
            return;
        }
        if (_choice is not { } choice)
        {
            StatusText.Text = "Choose a process first.";
            return;
        }
        _rows.Clear();
        UpdateFilterButtons();
        var target = new ProcessTarget(choice.Name, choice.ProcessId, AllNamedBox.IsChecked == true, choice.IsAll);
        ProcessFlowSession? session = null;
        session = new ProcessFlowSession(target, _probe, _classifier, _routes,
            frame => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) Show(frame, target); }));
        _session = session;
        session.Start();
        if (CaptureBox.IsChecked == true) ApplyCapture();
        StartButton.Content = "_Stop";
        UpdateControls();
        EmptyText.Text = WaitingText(target);
        EmptyText.Visibility = Visibility.Visible;
    }

    private static string WaitingText(ProcessTarget target) =>
        target.AllProcesses ? "Waiting for connections…" : $"Waiting for connections from {target.Name}…";

    private void StopSession()
    {
        _session?.Dispose();
        _session = null;
        StartButton.Content = "_Start";
        UpdateControls();
        if (_rows.Count == 0) EmptyText.Text = IdleText;
    }

    private void OnCapture(object sender, RoutedEventArgs e) => ApplyCapture();

    private void ApplyCapture()
    {
        if (_session is null) return;
        var error = _session.SetCapture(CaptureBox.IsChecked == true);
        if (error is null) return;
        CaptureBox.IsChecked = false;
        StatusText.Text = $"Couldn't read handshakes: {error}";
    }

    private void OnClearList(object sender, RoutedEventArgs e)
    {
        _session?.ClearResults();
        _rows.Clear();
        UpdateFilterButtons();
        EmptyText.Text = _session is null ? IdleText : "List cleared. Waiting for connections…";
        EmptyText.Visibility = Visibility.Visible;
        StatusText.Text = _session is null ? "List cleared." : "List cleared. Connections still open reappear; counts start again from zero.";
    }

    private void Show(FlowFrame frame, ProcessTarget target)
    {
        if (frame.Generation != _session?.Generation) return; // Built before the list was cleared.
        var existing = _rows.ToDictionary(r => r.Key);
        var keys = frame.Rows.Select(r => r.Key).ToHashSet();
        foreach (var gone in _rows.Where(r => !keys.Contains(r.Key)).ToList()) _rows.Remove(gone);
        foreach (var snapshot in frame.Rows)
        {
            if (!existing.TryGetValue(snapshot.Key, out var row))
            {
                row = new FlowRow(snapshot.Key);
                row.Update(snapshot);
                _rows.Add(row);
            }
            else row.Update(snapshot);
        }
        UpdateFilterButtons();
        UpdateEmptyText(frame, target);
        if (Flows.SelectedItem is null && _view.Count > 0) Flows.SelectedIndex = 0;

        var open = _rows.Sum(r => r.Open);
        var running = frame.ProcessCount == 0 && !target.AllProcesses
            ? $"{target.Name} isn't running"
            : $"{target.Name}: {Plural(frame.ProcessCount, "process", "processes")}, {Plural(open, "open connection", "open connections")}, {Plural(_rows.Count, "destination", "destinations")}";
        StatusText.Text = frame.CaptureError is { } error ? $"{running}. Handshake reading stopped: {error}" : running + CaptureText(frame.Capture);
    }

    private static string CaptureText(CaptureStats? s) => s is null ? "" :
        $"  ·  Handshake reading: {Plural(s.Adapters, "adapter", "adapters")}, {s.Packets:N0} packets, {Plural((int)Math.Min(s.TlsConnections, int.MaxValue), "new TLS connection", "new TLS connections")}, {s.Followed:N0} followed";

    private void UpdateEmptyText(FlowFrame frame, ProcessTarget target)
    {
        EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count > 0) EmptyText.Text = "Nothing matches this filter.";
        else if (frame.ProcessCount == 0 && !target.AllProcesses) EmptyText.Text = target.AllNamed ? $"{target.Name} isn't running. Waiting for it to start…" : $"{target.Name} isn't running.";
        else EmptyText.Text = WaitingText(target);
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private void OnGroupFilter(object sender, RoutedEventArgs e)
    {
        _groupFilter = ReferenceEquals(sender, ShowAttention) ? PinningGroup.NeedsAttention
            : ReferenceEquals(sender, ShowCantTell) ? PinningGroup.CantTell
            : ReferenceEquals(sender, ShowNoSign) ? PinningGroup.NoSign
            : null;
        UpdateFilterButtons();
        RefreshFilter();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => RefreshFilter();
    private void OnSearch(object sender, TextChangedEventArgs e) => RefreshFilter();

    private void RefreshFilter()
    {
        if (_view is null) return; // The search box can fire while the window loads.
        _view.Refresh();
        if (Flows.SelectedItem is null && _view.Count > 0) Flows.SelectedIndex = 0;
        if (_rows.Count > 0)
        {
            EmptyText.Text = "Nothing matches this filter.";
            EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateFilterButtons()
    {
        SetFilterButton(ShowAll, $"All {_rows.Count}", _groupFilter is null);
        SetFilterButton(ShowAttention, $"Needs attention {CountIn(PinningGroup.NeedsAttention)}", _groupFilter == PinningGroup.NeedsAttention);
        SetFilterButton(ShowCantTell, $"Can't tell {CountIn(PinningGroup.CantTell)}", _groupFilter == PinningGroup.CantTell);
        SetFilterButton(ShowNoSign, $"No sign {CountIn(PinningGroup.NoSign)}", _groupFilter == PinningGroup.NoSign);
    }

    private int CountIn(PinningGroup group) => _rows.Count(r => r.Group == group);

    private void SetFilterButton(Button button, string text, bool active)
    {
        // Plain text: a count must never be read as an access key.
        if (button.Content is TextBlock block) block.Text = text;
        else button.Content = new TextBlock { Text = text };
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        button.SetResourceReference(BorderBrushProperty, active ? "Heading" : "LineStrong");
        button.BorderThickness = new Thickness(active ? 2 : 1);
        System.Windows.Automation.AutomationProperties.SetName(button, active ? $"{text}, selected" : text);
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = Flows.SelectedItem as FlowRow;
        Detail.DataContext = row;
        Detail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Flows.SelectedItem is not FlowRow row) return;
        try
        {
            Clipboard.SetText(row.ToReport());
            StatusText.Text = $"Copied the details for {row.Host}.";
        }
        catch (ExternalException)
        {
            StatusText.Text = "Couldn't copy: another app is using the clipboard. Try again.";
        }
    }
}
