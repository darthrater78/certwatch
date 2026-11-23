using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using CertWatch.Core.ProcessFlow;

namespace CertWatch.App.Tracking;

/// <summary>
/// Optional, off by default, and only possible when CertWatch was started as administrator. Reads a
/// copy of IPv4 packets (a raw socket with SIO_RCVALL at IP level, so the adapter isn't put in
/// promiscuous mode) and hands them straight to a <see cref="HandshakeAssembler"/>, which keeps only
/// the cleartext handshake of the targeted process's connections. It can't block, alter or inject
/// traffic, holds no keys, and writes no packets anywhere.
/// </summary>
internal sealed class RawHandshakeCapture : IDisposable
{
    private const int RcvAllIpLevel = 3;
    private const int BufferSize = 64 * 1024;

    private readonly HandshakeAssembler _assembler;
    private readonly Action<HandshakeUpdate> _onUpdate;
    private readonly Action<string> _onError;
    private readonly List<Socket> _sockets = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

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

    /// <summary>Opens one socket per local IPv4 address. Returns null on success, or why it couldn't start.</summary>
    public string? Start()
    {
        string? lastError = null;
        foreach (var address in LocalAddresses())
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            try
            {
                socket.Bind(new IPEndPoint(address, 0));
                socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(RcvAllIpLevel), new byte[4]);
                _sockets.Add(socket);
            }
            catch (SocketException ex)
            {
                lastError = ex.Message;
                socket.Dispose();
            }
        }
        if (_sockets.Count == 0) return lastError ?? "No IPv4 network address to read from.";
        foreach (var socket in _sockets) _ = Task.Run(() => ReadLoopAsync(socket));
        return null;
    }

    private static List<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .Distinct()
        .ToList();

    private async Task ReadLoopAsync(Socket socket)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!_stop.IsCancellationRequested)
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
            // Stopped.
        }
        catch (SocketException ex)
        {
            if (!_stop.IsCancellationRequested) _onError(ex.Message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        _sockets.Clear();
        _stop.Dispose();
    }
}
