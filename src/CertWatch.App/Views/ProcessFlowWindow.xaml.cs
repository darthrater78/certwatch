using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CertWatch.App.Presentation;
using CertWatch.App.Tracking;
using CertWatch.Core.Network;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App.Views;

/// <summary>Targets one running process and shows where it connects and which certificates are involved.</summary>
public partial class ProcessFlowWindow : Window
{
    private readonly ICertProbe _probe;
    private readonly InspectionClassifier _classifier;
    private readonly IRouteLookup _routes;
    private readonly ObservableCollection<FlowRow> _rows = [];
    private readonly bool _elevated = RawHandshakeCapture.IsElevated;
    private ProcessFlowSession? _session;
    private FlowRow? _selected;
    private List<ProcessChoice> _choices = [];
    private ProcessChoice? _choice;

    internal ProcessFlowWindow(ICertProbe probe, InspectionClassifier classifier, IRouteLookup routes)
    {
        InitializeComponent();
        _probe = probe;
        _classifier = classifier;
        _routes = routes;
        Flows.ItemsSource = _rows;
        CaptureBox.IsEnabled = _elevated;
        CaptureHint.Text = _elevated
            ? "Off by default. Reads only TLS handshakes of this process, in memory. Never decrypts or alters traffic."
            : "Needs CertWatch started as administrator. Without it, hosts are inferred and certificates come from CertWatch's own handshakes.";
        LoadProcesses();
        Closed += (_, _) => StopSession();
        ThemeManager.Track(this);
    }

    private void LoadProcesses()
    {
        var previous = _choice?.ProcessId;
        var own = Environment.ProcessId;
        var choices = new List<ProcessChoice>();
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
        foreach (var (pid, count) in counts)
        {
            if (pid is 0 or 4 || pid == own) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                choices.Add(new ProcessChoice(process.ProcessName, pid, count));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // It exited while listing.
            }
        }
        _choices = choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ProcessId).ToList();
        Choose(_choices.FirstOrDefault(c => c.ProcessId == previous));
        StatusText.Text = $"{_choices.Count} processes have open connections.";
    }

    private void Choose(ProcessChoice? choice)
    {
        _choice = choice;
        ProcessButton.Content = choice?.Label ?? "Choose a process…";
    }

    private void OnChooseProcess(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProcessButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 420 };
        foreach (var choice in _choices)
        {
            var c = choice;
            // Process names are data: no access-key parsing of an underscore in the name.
            var item = new MenuItem { Header = new TextBlock { Text = c.Label }, IsChecked = c == _choice };
            item.Click += (_, _) => Choose(c);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No processes have open connections", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadProcesses();

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            StopSession();
            StatusText.Text = "Stopped. The rows stay until the next Start.";
            return;
        }
        if (_choice is not { } choice)
        {
            StatusText.Text = "Choose a process first.";
            return;
        }
        _rows.Clear();
        Select(null);
        var target = new ProcessTarget(choice.Name, choice.ProcessId, AllNamedBox.IsChecked == true);
        ProcessFlowSession? session = null;
        session = new ProcessFlowSession(target, _probe, _classifier, _routes,
            frame => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session)) Show(frame, target); }));
        _session = session;
        session.Start();
        if (CaptureBox.IsChecked == true) ApplyCapture();
        StartButton.Content = "_Stop";
        ProcessButton.IsEnabled = AllNamedBox.IsEnabled = false;
        EmptyText.Text = $"Waiting for connections from {choice.Name}…";
    }

    private void StopSession()
    {
        _session?.Dispose();
        _session = null;
        StartButton.Content = "_Start";
        ProcessButton.IsEnabled = AllNamedBox.IsEnabled = true;
        if (_rows.Count == 0) EmptyText.Text = "Choose a process and press Start.";
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

    private void Show(FlowFrame frame, ProcessTarget target)
    {
        var existing = _rows.ToDictionary(r => r.Key);
        var keys = frame.Rows.Select(r => r.Key).ToHashSet();
        foreach (var gone in _rows.Where(r => !keys.Contains(r.Key)).ToList()) _rows.Remove(gone);
        foreach (var snapshot in frame.Rows)
        {
            if (!existing.TryGetValue(snapshot.Key, out var row))
            {
                row = new FlowRow(snapshot.Key);
                _rows.Add(row);
            }
            row.Update(snapshot);
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (frame.ProcessCount == 0) EmptyText.Text = $"{target.Name} isn't running.";
        if (_selected is not null) ShowDetail(_selected);

        var open = _rows.Sum(r => r.Open);
        var running = frame.ProcessCount == 0 ? $"{target.Name} isn't running" : $"{target.Name}: {frame.ProcessCount} process{(frame.ProcessCount == 1 ? "" : "es")}, {open} open connection{(open == 1 ? "" : "s")}, {_rows.Count} destination{(_rows.Count == 1 ? "" : "s")}";
        StatusText.Text = frame.CaptureError is { } error ? $"{running}. Handshake reading stopped: {error}" : running;
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e) => Select(Flows.SelectedItem as FlowRow);

    private void Select(FlowRow? row)
    {
        _selected = row;
        if (row is null)
        {
            NodesList.ItemsSource = null;
            EvidenceText.Text = "Select a row to see its path and certificate chain.";
            return;
        }
        ShowDetail(row);
    }

    private void ShowDetail(FlowRow row)
    {
        if (!ReferenceEquals(NodesList.ItemsSource, row.Nodes)) NodesList.ItemsSource = row.Nodes;
        EvidenceText.Text = row.Evidence;
    }
}
