using CertWatch.App.Tracking;
using CertWatch.Core.ProcessFlow;

namespace CertWatch.App.Presentation;

/// <summary>A process the user can target: a running one, or a name added by hand.</summary>
internal sealed record ProcessChoice(string Name, int ProcessId, int Connections, bool IsAdded = false, bool IsAll = false, string? Identity = null)
{
    private string Shown => Identity ?? Name;

    public const string AllName = "All processes";

    private string ConnectionText => $"{Connections} connection{(Connections == 1 ? "" : "s")}";

    public string Label =>
        IsAll ? $"{Name}  ·  {ConnectionText}"
        : IsAdded ? $"{Shown}  ·  added  ·  {(Connections == 0 ? "no connections yet" : ConnectionText)}"
        : $"{Shown}  ·  PID {ProcessId}  ·  {ConnectionText}";
}

/// <summary>One destination in the process view. Kept across refreshes so the selection holds.</summary>
internal sealed class FlowRow(string key) : Observable
{
    public string Key { get; } = key;

    private StatusKind _status;
    public StatusKind Status { get => _status; private set => Set(ref _status, value); }
    private string _chip = "";
    public string Chip { get => _chip; private set => Set(ref _chip, value); }
    private string _processDisplay = "";
    public string ProcessDisplay { get => _processDisplay; private set => Set(ref _processDisplay, value); }
    private string _exe = "";
    public string Exe { get => _exe; private set => Set(ref _exe, value); }
    private string _host = "";
    public string Host { get => _host; private set => Set(ref _host, value); }
    private string _hostSource = "";
    public string HostSource { get => _hostSource; private set => Set(ref _hostSource, value); }
    private string _remote = "";
    public string Remote { get => _remote; private set => Set(ref _remote, value); }
    private string _tls = "";
    public string Tls { get => _tls; private set => Set(ref _tls, value); }
    private string _issuer = "";
    public string Issuer { get => _issuer; private set => Set(ref _issuer, value); }
    private string _pathText = "";
    public string PathText { get => _pathText; private set => Set(ref _pathText, value); }
    private string _evidence = "";
    public string Evidence { get => _evidence; private set => Set(ref _evidence, value); }
    private string _reaction = "";
    public string Reaction { get => _reaction; private set => Set(ref _reaction, value); }
    private int _open;
    public int Open { get => _open; private set => Set(ref _open, value); }
    private PinView? _pin;
    public PinView? Pin { get => _pin; private set => Set(ref _pin, value); }
    private PinningGroup _group;
    /// <summary>Also the sort order: what needs attention comes first.</summary>
    public PinningGroup Group { get => _group; private set => Set(ref _group, value); }
    private IReadOnlyList<FlowNode> _nodes = [];
    public IReadOnlyList<FlowNode> Nodes
    {
        get => _nodes;
        private set { if (!_nodes.SequenceEqual(value)) { _nodes = value; OnChanged(nameof(Nodes)); } }
    }

    public void Update(FlowSnapshot s)
    {
        ProcessDisplay = s.ProcessDisplay;
        Exe = s.Exe;
        Status = s.Status;
        Chip = s.Chip;
        Host = s.Host;
        HostSource = s.HostSource;
        Remote = s.Remote;
        Tls = s.Tls;
        Issuer = s.Issuer;
        PathText = s.PathText;
        Evidence = s.Evidence;
        Reaction = s.Reaction;
        Open = s.Open;
        Pin = s.Pin;
        Group = s.Group;
        Nodes = s.Nodes;
    }

    /// <summary>The row as plain text, for pasting into a ticket or bypass request.</summary>
    public string ToReport()
    {
        var lines = new List<string>
        {
            $"Process: {ProcessDisplay} ({Exe})",
            $"Host: {Host} ({HostSource})",
            $"Pinning: {Pin?.Headline}",
            $"Why: {Pin?.Why}",
            $"Next: {Pin?.Next}",
            $"Inspection: {Chip}",
            $"Address: {Remote}",
            $"TLS: {Tls}",
            $"Egress: {PathText}",
            $"Process response: {(Reaction.Length == 0 ? "not seen" : Reaction)}",
            "Certificate flow:",
        };
        lines.AddRange(Nodes.Select(n => $"  {n.Role}: {n.Title} ({n.Detail})"));
        lines.Add($"Evidence: {Evidence}");
        return string.Join(Environment.NewLine, lines);
    }
}
