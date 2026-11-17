using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CertWatch.Core.Diagnostics;

namespace CertWatch.Core.Cdp;

/// <summary>One target (tab, worker, …) in the demo browser.</summary>
public sealed record CdpTarget(string Id, string Type, string Title, string Url)
{
    public bool IsPage => Type == "page";
}

/// <summary>What CertWatch needs from a browser's DevTools connection.</summary>
public interface ICdpClient
{
    Task<bool> IsAliveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(CancellationToken ct = default);
    /// <summary>The chain (leaf first, DER) the browser has for <paramref name="origin"/>, read through the given tab.</summary>
    Task<IReadOnlyList<byte[]>> GetCertificateAsync(CdpTarget page, string origin, CancellationToken ct = default);
}

public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// Chrome DevTools Protocol over --remote-debugging-pipe: two anonymous pipes held only by CertWatch
/// and the browser it started, so no other program on the PC can reach the DevTools endpoint.
/// Messages are JSON, each terminated by a NUL byte.
/// </summary>
public sealed class CdpPipeClient : ICdpClient, IAsyncDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _toBrowser;
    private readonly Stream _fromBrowser;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private int _nextId;
    private volatile bool _closed;

    public CdpPipeClient(Stream toBrowser, Stream fromBrowser)
    {
        _toBrowser = toBrowser;
        _fromBrowser = fromBrowser;
        _reader = Task.Run(ReadLoopAsync);
    }

    public bool IsClosed => _closed;

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        if (_closed) return false;
        try
        {
            await CallAsync("Browser.getVersion", null, null, ct);
            return true;
        }
        catch (Exception ex) when (ex is CdpException or TimeoutException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("Target.getTargets", null, null, ct);
        if (!result.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array) return [];
        return infos.EnumerateArray()
            .Select(t => new CdpTarget(Str(t, "targetId"), Str(t, "type"), Str(t, "title"), Str(t, "url")))
            .ToList();
    }

    public async Task<IReadOnlyList<byte[]>> GetCertificateAsync(CdpTarget page, string origin, CancellationToken ct = default)
    {
        var attached = await CallAsync("Target.attachToTarget",
            new Dictionary<string, object> { ["targetId"] = page.Id, ["flatten"] = true }, null, ct);
        var session = Str(attached, "sessionId");
        if (session.Length == 0) return [];
        try
        {
            await CallAsync("Network.enable", null, session, ct);
            var result = await CallAsync("Network.getCertificate", new Dictionary<string, object> { ["origin"] = origin }, session, ct);
            if (!result.TryGetProperty("tableNames", out var names) || names.ValueKind != JsonValueKind.Array) return [];
            return names.EnumerateArray()
                .Select(n => n.GetString())
                .Where(b64 => !string.IsNullOrEmpty(b64))
                .Select(b64 => Convert.FromBase64String(b64!))
                .ToList();
        }
        finally
        {
            try { await CallAsync("Target.detachFromTarget", new Dictionary<string, object> { ["sessionId"] = session }, null, CancellationToken.None); }
            catch (Exception ex) when (ex is CdpException or TimeoutException or IOException or ObjectDisposedException) { }
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private async Task<JsonElement> CallAsync(string method, object? parameters, string? sessionId, CancellationToken ct)
    {
        if (_closed) throw new CdpException("The demo browser's DevTools pipe is closed.");

        var id = Interlocked.Increment(ref _nextId);
        Diag.Debug("devtools", () => $"→ {id} {method}{(sessionId is null ? "" : $" [session {sessionId}]")}");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new Dictionary<string, object> { ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;
            if (sessionId is not null) message["sessionId"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\0");

            await _writeLock.WaitAsync(ct);
            try
            {
                await _toBrowser.WriteAsync(bytes, ct);
                await _toBrowser.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(CallTimeout, ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _fromBrowser.ReadAsync(buffer, _stop.Token);
                if (read == 0) break; // browser closed its end
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 0) continue;
                    message.Write(buffer, start, i - start);
                    Dispatch(message.ToArray());
                    message.SetLength(0);
                    start = i + 1;
                }
                message.Write(buffer, start, read - start);
                if (message.Length > MaxMessageBytes) throw new CdpException("DevTools message too large.");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or CdpException)
        {
            // treated as closed below
        }
        finally
        {
            _closed = true;
            foreach (var tcs in _pending.Values) tcs.TrySetException(new CdpException("The demo browser closed."));
        }
    }

    private void Dispatch(byte[] json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            // Events (no id) are ignored; only replies to our calls matter.
            if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id)) return;
            if (!_pending.TryGetValue(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var error))
                tcs.TrySetException(new CdpException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "DevTools error" : "DevTools error"));
            else
                tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _toBrowser.DisposeAsync();
        await _fromBrowser.DisposeAsync();
        try { await _reader; } catch (OperationCanceledException) { }
        _stop.Dispose();
        _writeLock.Dispose();
    }
}
