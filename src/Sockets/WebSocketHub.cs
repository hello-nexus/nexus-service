using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Sockets;

/// <summary>
/// WebSocket fan-out helper. Each WS endpoint gets its own singleton instance
/// holding the connected-client set and BroadcastBinary helpers. JSON
/// broadcasts go through WsEnvelope.Build with an explicit
/// AppJsonContext.Default.&lt;T&gt;; never serialize inside the hub.
///
/// OnFirstClient / OnAllClientsGone let the controller start and stop expensive
/// subscriptions on demand.
///
/// ConcurrentDictionary keeps the client set safe; each client has its own
/// SemaphoreSlim guarding writes (WebSocket disallows concurrent SendAsync on
/// the same socket).
/// </summary>
public class WebSocketHub
{
    private readonly ConcurrentDictionary<Guid, ClientEntry> _clients = new();

    public event Action? OnFirstClient;
    public event Action? OnAllClientsGone;

    public int ClientCount => _clients.Count;

    /// <summary>
    /// Take ownership of a newly-upgraded WebSocket and pump it until the client
    /// disconnects. Optional onMessage callback receives any text frames the client
    /// sends (used for command WS where the client invokes methods).
    /// </summary>
    public async Task HandleClientAsync(
        WebSocket socket,
        Func<string, Task>? onMessage = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new ClientEntry(socket);
        var id = Guid.NewGuid();
        _clients[id] = entry;

        try
        {
            if (_clients.Count == 1)
            {
                try
                { OnFirstClient?.Invoke(); }
                catch { /* swallow */ }
            }

            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text && onMessage is not null)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (message == "ping")
                    {
                        continue; // keepalive
                    }

                    try
                    { await onMessage(message); }
                    catch { /* swallow */ }
                }
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            entry.Dispose();
            try
            {
                // CloseOutputAsync: CloseAsync waits for the peer's reply with no
                // deadline, and a dead peer would hold the host stop.
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
            }
            catch { /* swallow */ }

            if (_clients.IsEmpty)
            {
                try
                { OnAllClientsGone?.Invoke(); }
                catch { /* swallow */ }
            }
        }
    }

    /// <summary>Send raw binary data to every connected client.</summary>
    public Task BroadcastBinaryAsync(byte[] payload) =>
        _clients.IsEmpty
            ? Task.CompletedTask
            : BroadcastAsync(new ReadOnlyMemory<byte>(payload), WebSocketMessageType.Binary);

    public Task BroadcastBinaryAsync(ReadOnlyMemory<byte> payload) =>
        _clients.IsEmpty
            ? Task.CompletedTask
            : BroadcastAsync(payload, WebSocketMessageType.Binary);

    private async Task BroadcastAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
    {
        foreach (var (_, entry) in _clients)
        {
            await entry.SendAsync(payload, type);
        }
    }

    private sealed class ClientEntry : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientEntry(WebSocket socket) { _socket = socket; }

        public async Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await _writeLock.WaitAsync();
            try
            {
                await _socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
            }
            catch { /* peer dead, will be reaped on next ReceiveAsync */ }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose() => _writeLock.Dispose();
    }
}
