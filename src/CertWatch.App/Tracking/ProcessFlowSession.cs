using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertWatch.App.Logging;
using CertWatch.App.Presentation;
using CertWatch.Core.Network;
using CertWatch.Core.ProcessFlow;
using CertWatch.Core.Tls;
using CertWatch.Core.Trust;

namespace CertWatch.App.Tracking;

/// <summary>The process to follow; with <see cref="AllNamed"/>, every process of that name.</summary>
internal sealed record ProcessTarget(string Name, int ProcessId, bool AllNamed);

/// <summary>One certificate-flow step shown under the table.</summary>
internal sealed record FlowNode(string Role, string Title, string Detail, bool IsFirst);

/// <summary>A display-ready copy of one destination, safe to hand to the UI thread.</summary>
internal sealed record FlowSnapshot(string Key, StatusKind Status, string Chip, string Host, string HostSource, string Remote,
    string Tls, string Issuer, string PathText, string Evidence, int Open, IReadOnlyList<FlowNode> Nodes, string Reaction);

internal sealed record FlowFrame(IReadOnlyList<FlowSnapshot> Rows, int ProcessCount, string? CaptureError);

/// <summary>
/// Follows one process: reads its connections each second, makes CertWatch's own handshakes to where
/// it connects and, if turned on, reads its handshakes. Results live in memory only and are never
/// written to history. Sends nothing to the process and never touches its connections.
/// </summary>
internal sealed class ProcessFlowSession : IDisposable
{
    private const int MaxConcurrent = 4;
    private static readonly TimeSpan NameTimeout = TimeSpan.FromSeconds(3);

    private readonly ProcessTarget _target;
    private readonly ICertProbe _probe;
    private readonly InspectionClassifier _classifier;
    private readonly IRouteLookup _routes;
    private readonly Action<FlowFrame> _publish;
    private readonly ProcessFlowTracker _tracker = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConditionalWeakTable<object, IReadOnlyList<FlowNode>> _certNodes = [];
    private RawHandshakeCapture? _capture;
    private volatile HashSet<int> _pids = [];
    private volatile string? _captureError;
    private int _active;

    public ProcessFlowSession(ProcessTarget target, ICertProbe probe, InspectionClassifier classifier, IRouteLookup routes, Action<FlowFrame> publish)
    {
        _target = target;
        _probe = probe;
        _classifier = classifier;
        _routes = routes;
        _publish = publish;
    }

    public void Start()
    {
        _pids = ResolvePids();
        LogService.Info("process", $"Following {_target.Name}");
        _ = Task.Run(RunAsync);
    }

    /// <summary>Turns reading the process's handshakes on or off. Returns why it couldn't start, if so.</summary>
    public string? SetCapture(bool on)
    {
        _capture?.Dispose();
        _capture = null;
        _captureError = null;
        if (!on)
        {
            LogService.Info("process", "Handshake reading off");
            return null;
        }
        var capture = new RawHandshakeCapture(IsTargetFlow, OnHandshake, e => _captureError = e);
        var error = capture.Start();
        if (error is not null)
        {
            capture.Dispose();
            _captureError = error;
            return error;
        }
        _capture = capture;
        LogService.Info("process", $"Reading handshakes of {_target.Name} only");
        return null;
    }

    private bool IsTargetFlow(FlowKey key)
    {
        try
        {
            return TcpConnections.FlowsFor(_pids).Any(c => c.LocalPort == key.LocalPort && c.RemotePort == key.RemotePort
                && Same(c.Remote, key.Remote) && Same(c.Local, key.Local));
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return false; // Unknown owner: don't follow it.
        }
    }

    private static bool Same(IPAddress a, IPAddress b) => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    private void OnHandshake(HandshakeUpdate update)
    {
        lock (_gate) _tracker.Apply(update.Key, update.View, DateTimeOffset.UtcNow, update.Outcome);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    Tick();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
                {
                    // One bad read of the process list shouldn't end the session.
                    LogService.Info("process", $"Couldn't refresh {_target.Name} ({ex.Message})");
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var pids = ResolvePids();
        _pids = pids;
        List<FlowConnection> connections;
        try
        {
            connections = TcpConnections.FlowsFor(pids);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            LogService.Info("process", $"Couldn't read connections ({ex.Message})");
            connections = [];
        }

        IReadOnlyList<FlowDestination> names;
        IReadOnlyList<FlowProbeRequest> probes;
        IReadOnlyList<FlowSnapshot> rows;
        lock (_gate)
        {
            _tracker.Update(connections, now);
            names = _tracker.TakeNameLookups(MaxConcurrent);
            probes = _tracker.TakeProbes(now, Math.Max(0, MaxConcurrent - Volatile.Read(ref _active)));
            rows = _tracker.Destinations.OrderByDescending(d => d.OpenConnections > 0).ThenBy(d => d.Host ?? d.Remote.ToString(), StringComparer.Ordinal).Select(Snapshot).ToList();
        }
        foreach (var row in names) _ = Task.Run(() => LookUpNameAsync(row));
        foreach (var request in probes)
        {
            Interlocked.Increment(ref _active);
            _ = Task.Run(() => ProbeAsync(request));
        }
        _publish(new FlowFrame(rows, pids.Count, _captureError));
    }

    private HashSet<int> ResolvePids()
    {
        var ids = new HashSet<int>();
        if (_target.AllNamed)
        {
            foreach (var p in Process.GetProcessesByName(_target.Name))
            {
                using (p) ids.Add(p.Id);
            }
            return ids;
        }
        try
        {
            using var process = Process.GetProcessById(_target.ProcessId);
            // A reused process id is a different program: stop following it.
            if (string.Equals(process.ProcessName, _target.Name, StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It exited.
        }
        return ids;
    }

    private async Task LookUpNameAsync(FlowDestination row)
    {
        string? name = null;
        try
        {
            var entry = await Dns.GetHostEntryAsync(row.Remote).WaitAsync(NameTimeout, _stop.Token);
            name = entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException or OperationCanceledException or ObjectDisposedException)
        {
            // No reverse name: the handshake goes without SNI.
        }
        lock (_gate) _tracker.ApplyInferredName(row, name, DateTimeOffset.UtcNow);
    }

    private async Task ProbeAsync(FlowProbeRequest request)
    {
        ProbeResult? result = null;
        Classification? classification = null;
        EgressInfo? egress = null;
        try
        {
            result = await _probe.ProbeAtAsync(request.Endpoint, request.Address, _stop.Token);
            classification = _classifier.Classify(result);
            egress = _routes.Lookup(request.Address) is { } facts ? EgressClassifier.Classify(facts) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ArgumentException or InvalidOperationException or CryptographicException or ExternalException)
        {
            LogService.Debug("process", () => $"Handshake to {request.Address} failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            lock (_gate) _tracker.CompleteProbe(request.Destination, result, classification, egress, DateTimeOffset.UtcNow);
        }
    }

    private FlowSnapshot Snapshot(FlowDestination d)
    {
        var different = d.CertEvidence == CertEvidence.DifferentFromSeen;
        var (status, chip) = StatusOf(d, different);
        var certNodes = CertNodes(d, different);
        var hostSource = d.HostEvidence switch
        {
            HostEvidence.Observed => d.EncryptedClientHello ? "Observed (outer name)" : "Observed",
            HostEvidence.Inferred => "Inferred",
            _ => "Unknown",
        };
        var host = d.Host ?? "(unknown host)";
        var remote = d.Remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{d.Remote}]:{d.Port}" : $"{d.Remote}:{d.Port}";
        var pathText = d.Egress is { } e ? $"{e.InterfaceLabel} ({e.Kind})" : "Route unknown";
        var issuer = certNodes.Count > 0 ? IssuerOf(certNodes[0]) : d.Classification?.IssuerName ?? "";

        var nodes = new List<FlowNode>
        {
            new("Process", _target.Name, _target.AllNamed ? "all processes of this name" : $"PID {_target.ProcessId}", true),
            new("Path", d.Egress?.InterfaceLabel ?? "Route unknown", d.Egress?.Kind.ToString() ?? "", false),
            new("Server", host, remote, false),
        };
        nodes.AddRange(certNodes);
        return new FlowSnapshot($"{d.Remote}|{d.Port}|{d.Host}", status, chip, host, hostSource, remote, TlsLabel(d), issuer, pathText, d.EvidenceText, d.OpenConnections, nodes, ReactionLabel(d));
    }

    private static string ReactionLabel(FlowDestination d) => (d.Accepted, d.Refused) switch
    {
        (0, 0) => "",
        (var a, 0) => $"Accepted {a}",
        (0, var r) => $"Refused {r}",
        (var a, var r) => $"Accepted {a}, refused {r}",
    };

    private static (StatusKind, string) StatusOf(FlowDestination d, bool different)
    {
        if (d.Pinning == PinningSignal.Likely) return (StatusKind.Neutral, "Pinning likely");
        if (different) return (StatusKind.Neutral, "Different cert");
        if (d.Classification is not { } c) return (StatusKind.Neutral, d.CertEvidence == CertEvidence.SeenInHandshake ? "Seen" : "Not checked");
        var kind = d.Egress?.Kind.ToString();
        return (Status.Of(c.Verdict, kind), VerdictStyle.Short(PathTreatment.Of(c.Verdict, kind), c.Verdict));
    }

    private static string TlsLabel(FlowDestination d)
    {
        if (d.ObservedVersion is { } v) return v switch { TlsHandshakeParser.Tls13 => "TLS 1.3", TlsHandshakeParser.Tls12 => "TLS 1.2", _ => $"0x{v:X4}" };
        return d.Probe?.Protocol switch { SslProtocols.Tls13 => "TLS 1.3", SslProtocols.Tls12 => "TLS 1.2", _ => "" };
    }

    private static string IssuerOf(FlowNode leaf)
    {
        const string prefix = "Issued by ";
        var detail = leaf.Detail;
        if (!detail.StartsWith(prefix, StringComparison.Ordinal)) return "";
        var end = detail.IndexOf(" · ", StringComparison.Ordinal);
        return end < 0 ? detail[prefix.Length..] : detail[prefix.Length..end];
    }

    // The process's own chain when it differs or is all we have; otherwise CertWatch's fuller chain.
    private IReadOnlyList<FlowNode> CertNodes(FlowDestination d, bool different)
    {
        if ((different || d.Probe?.Leaf is null) && d.ObservedCertificates.Count > 0)
            return _certNodes.GetValue(d.ObservedCertificates, _ => BuildObserved(d.ObservedCertificates));
        if (d.Probe is { Chain.Count: > 0 } probe)
            return _certNodes.GetValue(probe, _ => Build(probe.Chain));
        return [];
    }

    private static IReadOnlyList<FlowNode> BuildObserved(IReadOnlyList<byte[]> der)
    {
        var certs = new List<X509Certificate2>();
        try
        {
            foreach (var bytes in der) certs.Add(X509CertificateLoader.LoadCertificate(bytes));
            return Build(certs);
        }
        catch (CryptographicException)
        {
            return [new FlowNode("Certificate", "Couldn't be read", "The certificate in the handshake is malformed", false)];
        }
        finally
        {
            foreach (var cert in certs) cert.Dispose();
        }
    }

    private static IReadOnlyList<FlowNode> Build(IReadOnlyList<X509Certificate2> chain)
    {
        var nodes = new List<FlowNode>(chain.Count);
        for (var i = 0; i < chain.Count; i++)
        {
            var cert = chain[i];
            var selfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);
            var role = i == 0 ? "Certificate" : selfSigned ? "Root" : "Intermediate";
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            var issuer = cert.GetNameInfo(X509NameType.SimpleName, true);
            var hash = ProbeResult.Sha256Hex(cert)[..12];
            nodes.Add(new FlowNode(role, string.IsNullOrEmpty(name) ? "(no name)" : name, $"Issued by {issuer} · SHA-256 {hash}…", false));
        }
        return nodes;
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
        _stop.Cancel();
        LogService.Info("process", $"Stopped following {_target.Name}");
    }
}
