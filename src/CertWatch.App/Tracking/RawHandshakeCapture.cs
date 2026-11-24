using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using CertWatch.Core.ProcessFlow;

namespace CertWatch.App.Tracking;

/// <summary>What handshake reading has seen, so an empty result can be told from a blind capture.</summary>
internal sealed record CaptureStats(int Adapters, long Packets, long TlsConnections, long Followed);

/// <summary>
/// Optional, off by default, and only possible when CertWatch was started as administrator. Reads a
/// copy of IPv4 packets (a raw socket with SIO_RCVALL at IP level, so the adapter isn't put in
/// promiscuous mode) and hands them straight to a <see cref="HandshakeAssembler"/>, which keeps only
/// the cleartext handshake of the targeted process's connections. It can't block, alter or inject
/// traffic, holds no keys, and writes no packets anywhere. Follows addresses as they come and go,
/// since a ZTNA client turning on or off adds and removes an adapter.
/// </summary>
internal sealed class RawHandshakeCapture : IDisposable
{
    private const int RcvAllIpLevel = 3;
    private const int BufferSize = 64 * 1024;

    private readonly HandshakeAssembler _assembler;
    private readonly Action<HandshakeUpdate> _onUpdate;
    private readonly Action<string> _onError;
    private readonly Dictionary<IPAddress, Socket> _sockets = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Lock _socketGate = new();
    private volatile bool _stopped;

    public RawHandshakeCapture(Func<FlowKey, bool> isTarget, Action<HandshakeUpdate> onUpdate, Action<string> onError)
    {
        _assembler = new HandshakeAssembler(isTarget);
        _onUpdate = onUpdate;
        _onError = onError;
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public CaptureStats Stats
    {
        get
        {
            int adapters;
            lock (_socketGate) adapters = _sockets.Count;
            lock (_gate) return new CaptureStats(adapters, _assembler.PacketsSeen, _assembler.ClientHellosSeen, _assembler.ConnectionsFollowed);
        }
    }

    /// <summary>Opens one socket per local IPv4 address. Returns null on success, or why it couldn't start.</summary>
    public string? Start()
    {
        var lastError = SyncSockets();
        lock (_socketGate)
        {
            if (_sockets.Count == 0) return lastError ?? "No IPv4 network address to read from.";
        }
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        return null;
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        if (_stopped) return;
        try
        {
            SyncSockets();
        }
        catch (NetworkInformationException ex)
        {
            _onError(ex.Message);
        }
    }

    // Closes sockets for addresses that are gone and opens them for new ones.
    private string? SyncSockets()
    {
        var current = LocalAddresses();
        string? lastError = null;
        lock (_socketGate)
        {
            if (_stopped) return null;
            foreach (var gone in _sockets.Keys.Where(a => !current.Contains(a)).ToList())
            {
                _sockets[gone].Dispose();
                _sockets.Remove(gone);
            }
            foreach (var address in current.Where(a => !_sockets.ContainsKey(a)))
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                try
                {
                    socket.Bind(new IPEndPoint(address, 0));
                    socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(RcvAllIpLevel), new byte[4]);
                    _sockets[address] = socket;
                    _ = Task.Run(() => ReadLoopAsync(address, socket));
                }
                catch (SocketException ex)
                {
                    lastError = ex.Message;
                    socket.Dispose();
                }
            }
        }
        return lastError;
    }

    private static HashSet<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .ToHashSet();

    private async Task ReadLoopAsync(IPAddress address, Socket socket)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!_stopped)
            {
                var length = await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                HandshakeUpdate? update;
                lock (_gate) update = _assembler.Process(buffer.AsSpan(0, length));
                if (update is not null) _onUpdate(update);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (ObjectDisposedException)
        {
            // Stopped, or this address went away.
        }
        catch (SocketException ex)
        {
            OnSocketFailed(address, socket, ex);
        }
    }

    // One adapter going away is normal. Only losing the last one stops handshake reading.
    private void OnSocketFailed(IPAddress address, Socket socket, SocketException ex)
    {
        bool noneLeft;
        lock (_socketGate)
        {
            if (_sockets.TryGetValue(address, out var known) && ReferenceEquals(known, socket)) _sockets.Remove(address);
            noneLeft = _sockets.Count == 0;
        }
        socket.Dispose();
        if (noneLeft && !_stopped) _onError(ex.Message);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_socketGate)
        {
            _stopped = true;
            _stop.Cancel();
            foreach (var socket in _sockets.Values) socket.Dispose();
            _sockets.Clear();
        }
        _stop.Dispose();
    }
}
