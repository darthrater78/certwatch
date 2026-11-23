using CertWatch.App.Tracking;

namespace CertWatch.App.Presentation;

/// <summary>A running process the user can target.</summary>
internal sealed record ProcessChoice(string Name, int ProcessId, int Connections)
{
    public string Label => $"{Name}  ·  PID {ProcessId}  ·  {Connections} connection{(Connections == 1 ? "" : "s")}";
}

/// <summary>One destination in the process view. Kept across refreshes so the selection holds.</summary>
internal sealed class FlowRow(string key) : Observable
{
    public string Key { get; } = key;

    private StatusKind _status;
    public StatusKind Status { get => _status; private set => Set(ref _status, value); }
    private string _chip = "";
    public string Chip { get => _chip; private set => Set(ref _chip, value); }
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
    private IReadOnlyList<FlowNode> _nodes = [];
    public IReadOnlyList<FlowNode> Nodes
    {
        get => _nodes;
        private set { if (!_nodes.SequenceEqual(value)) { _nodes = value; OnChanged(nameof(Nodes)); } }
    }

    public void Update(FlowSnapshot s)
    {
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
        Nodes = s.Nodes;
    }
}
