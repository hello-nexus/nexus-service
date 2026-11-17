using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>Shared helpers for the relay test suite.</summary>
internal static class RelayTestHelpers
{
    /// <summary>
    /// A <see cref="RelayHttpDispatcher"/> backed by an empty service provider,
    /// for relay tests that exercise the runtime / pair legs and never peer up
    /// the rid_http channel (so the dispatcher is never actually invoked). The
    /// HTTP-tunnel dispatch path is covered end-to-end in
    /// <see cref="RelayHttpDispatcherTests"/>.
    /// </summary>
    public static RelayHttpDispatcher InertHttpDispatcher()
        => new(new ServiceCollection().BuildServiceProvider());
}

/// <summary>
/// Loopback fake relay that accepts MANY host connections and routes by rid (the
/// host hello's <c>rid</c>). Acts as the relay AND the simulated client: the test
/// peers a client up against a given rid, forwards sealed client frames to that
/// rid's host socket, and captures host→client frames per rid.
/// </summary>
internal sealed class MultiRidFakeRelay : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    private readonly object _gate = new();
    private readonly Dictionary<string, WebSocket> _hostsByRid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _hostWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConcurrentQueue<byte[]>> _hostFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<byte[]>> _frameWaiters = new(StringComparer.Ordinal);

    public Uri Uri { get; }

    public MultiRidFakeRelay()
    {
        var port = GetFreePort();
        Uri = new Uri($"ws://127.0.0.1:{port}/relay");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/relay/");
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try { await HostReadLoopAsync(wsCtx.WebSocket, ct).ConfigureAwait(false); }
                finally { UnregisterHost(wsCtx.WebSocket); }
            });
        }
    }

    private async Task HostReadLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new System.IO.MemoryStream();
        string? rid = null;
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(message.ToArray());
                rid = doc.RootElement.GetProperty("rid").GetString();
                if (!string.IsNullOrEmpty(rid))
                    RegisterHost(rid, socket);
            }
            else if (result.MessageType == WebSocketMessageType.Binary && rid is not null)
            {
                DeliverHostFrame(rid, message.ToArray());
            }
        }
    }

    private readonly Dictionary<string, int> _registrations = new(StringComparer.Ordinal);
    private readonly List<(string Rid, int AtLeast, TaskCompletionSource<bool> Tcs)> _registrationWaiters = new();
    private readonly List<(string Rid, TaskCompletionSource<bool> Tcs)> _goneWaiters = new();

    /// <summary>Completes once <paramref name="rid"/> has registered a host socket at least <paramref name="atLeast"/> times in total.</summary>
    public Task WaitForRegistrationsAsync(string rid, int atLeast, TimeSpan timeout)
    {
        lock (_gate)
        {
            _registrations.TryGetValue(rid, out var count);
            if (count >= atLeast)
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrationWaiters.Add((rid, atLeast, tcs));
            return tcs.Task.WaitAsync(timeout);
        }
    }

    public int RegistrationCount(string rid)
    {
        lock (_gate)
            return _registrations.TryGetValue(rid, out var count) ? count : 0;
    }

    /// <summary>Completes once no host socket is registered for <paramref name="rid"/> (the host disconnected).</summary>
    public Task WaitForHostGoneAsync(string rid, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (!_hostsByRid.ContainsKey(rid))
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _goneWaiters.Add((rid, tcs));
            return tcs.Task.WaitAsync(timeout);
        }
    }

    private void UnregisterHost(WebSocket socket)
    {
        lock (_gate)
        {
            foreach (var rid in _hostsByRid.Where(kv => ReferenceEquals(kv.Value, socket)).Select(kv => kv.Key).ToList())
            {
                _hostsByRid.Remove(rid);
                foreach (var w in _goneWaiters.Where(g => g.Rid == rid).ToList())
                {
                    w.Tcs.TrySetResult(true);
                    _goneWaiters.Remove(w);
                }
            }
        }
    }

    private void RegisterHost(string rid, WebSocket socket)
    {
        lock (_gate)
        {
            _hostsByRid[rid] = socket;
            _registrations[rid] = _registrations.GetValueOrDefault(rid) + 1;
            foreach (var waiter in _registrationWaiters.Where(r => r.Rid == rid && _registrations[rid] >= r.AtLeast).ToList())
            {
                waiter.Tcs.TrySetResult(true);
                _registrationWaiters.Remove(waiter);
            }
            if (_hostWaiters.TryGetValue(rid, out var w))
                w.TrySetResult(true);
        }
    }

    private void DeliverHostFrame(string rid, byte[] frame)
    {
        lock (_gate)
        {
            if (_frameWaiters.TryGetValue(rid, out var w) && w.TrySetResult(frame))
            {
                _frameWaiters.Remove(rid);
                return;
            }
            if (!_hostFrames.TryGetValue(rid, out var q))
            {
                q = new ConcurrentQueue<byte[]>();
                _hostFrames[rid] = q;
            }
            q.Enqueue(frame);
        }
    }

    /// <summary>Failure deadline for a host to appear; the wait completes the moment it does, so this only bounds a genuine failure and must survive a loaded parallel suite.</summary>
    public static readonly TimeSpan HostWait = TimeSpan.FromSeconds(30);

    public Task WaitForHostAsync(string rid, TimeSpan timeout)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (_hostsByRid.ContainsKey(rid))
                return Task.CompletedTask;
            if (!_hostWaiters.TryGetValue(rid, out tcs!))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _hostWaiters[rid] = tcs;
            }
        }
        return tcs.Task.WaitAsync(timeout);
    }

    public Task<byte[]> WaitForHostFrameAsync(string rid, TimeSpan timeout)
    {
        TaskCompletionSource<byte[]> tcs;
        lock (_gate)
        {
            if (_hostFrames.TryGetValue(rid, out var q) && q.TryDequeue(out var frame))
                return Task.FromResult(frame);
            tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _frameWaiters[rid] = tcs;
        }
        return tcs.Task.WaitAsync(timeout);
    }

    public async Task SendPeerUpAsync(string rid, byte[] connSalt)
    {
        var socket = HostFor(rid);
        var saltB64 = RelayCrypto.Base64UrlNoPad(connSalt);
        var json = Encoding.UTF8.GetBytes($"{{\"e\":\"peer-up\",\"salt\":\"{saltB64}\"}}");
        await socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task ForwardToHostAsync(string rid, byte[] frame)
    {
        var socket = HostFor(rid);
        await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private WebSocket HostFor(string rid)
    {
        lock (_gate)
        {
            if (!_hostsByRid.TryGetValue(rid, out var socket))
                throw new InvalidOperationException($"no host registered for rid {rid}");
            return socket;
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        lock (_gate)
        {
            foreach (var s in _hostsByRid.Values)
            {
                try { s.Abort(); } catch { }
                try { s.Dispose(); } catch { }
            }
        }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
