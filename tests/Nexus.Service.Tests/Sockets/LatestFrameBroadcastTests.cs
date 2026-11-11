using System.Net.WebSockets;
using System.Threading.Channels;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Sockets;

/// <summary>A slow client of the lighting output stream falls behind by at most one frame instead of queueing every one.</summary>
public sealed class LatestFrameBroadcastTests
{
    private sealed class StalledSocket : WebSocket
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<int> _incoming = Channel.CreateUnbounded<int>();
        private readonly List<byte> _sent = new();

        public TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = new();

        // Completes once the socket has fully sent this many frames.
        public Task WaitForSent(int count)
        {
            lock (_sent)
            {
                if (_sent.Count >= count)
                {
                    return Task.CompletedTask;
                }
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task;
            }
        }
        public IReadOnlyList<byte> Sent { get { lock (_sent) { return _sent.ToArray(); } } }

        public void Release() => _gate.TrySetResult();
        public void Close() => _incoming.Writer.TryComplete();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Registered.TrySetResult();
            await _incoming.Reader.WaitToReadAsync(cancellationToken);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            FirstSend.TrySetResult();
            await _gate.Task;
            lock (_sent)
            {
                _sent.Add(buffer.Span[0]);
                foreach (var (count, done) in _waiters)
                {
                    if (_sent.Count >= count)
                    {
                        done.TrySetResult();
                    }
                }
            }
        }
    }

    [Fact]
    public async Task A_stalled_client_holds_one_in_flight_frame_and_only_the_latest_pending()
    {
        var hub = new WebSocketHub();
        var socket = new StalledSocket();
        var loop = hub.HandleClientAsync(socket);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        hub.BroadcastLatestBinary(new byte[] { 1 });
        await socket.FirstSend.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (byte frame = 2; frame <= 50; frame++)
        {
            hub.BroadcastLatestBinary(new byte[] { frame });
        }

        socket.Release();
        await socket.WaitForSent(2).WaitAsync(TimeSpan.FromSeconds(5));
        // Once the pump has drained, a further frame goes out next and nothing older is left behind it.
        hub.BroadcastLatestBinary(new byte[] { 51 });
        await socket.WaitForSent(3).WaitAsync(TimeSpan.FromSeconds(5));

        // The frame in flight, then only the newest one, then the later one.
        Assert.Equal(new byte[] { 1, 50, 51 }, socket.Sent);
        socket.Close();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
