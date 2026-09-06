using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// The pending pair-request frame carries the request id and SAS the host uses
/// to approve a NEW device. A phone-session socket (LAN, relay, or direct) must
/// never receive it; a desktop socket must.
/// </summary>
public sealed class MultiplexHubDesktopOnlyTopicsTests
{
    [Fact]
    public async Task Phone_session_socket_cannot_subscribe_to_the_pair_request_topic()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();
        var phone = new ScriptedWebSocket("{\"sub\":[\"" + PanelTopics.PairCodeRequest + "\",\"volume\"]}");

        var loop = hub.HandleClientAsync(phone, "sess-phone", MultiplexHub.ClientTransport.Lan, cts.Token);
        await WaitUntilAsync(() => hub.TopicHasSubscribers("volume"), TimeSpan.FromSeconds(5),
            "the phone's ordinary subscription never registered");

        Assert.False(hub.TopicHasSubscribers(PanelTopics.PairCodeRequest));

        cts.Cancel();
        phone.Unblock();
        await loop;
    }

    [Fact]
    public async Task Desktop_socket_can_subscribe_to_the_pair_request_topic()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();
        var desktop = new ScriptedWebSocket("{\"sub\":[\"" + PanelTopics.PairCodeRequest + "\"]}");

        var loop = hub.HandleClientAsync(desktop, phoneSessionId: null, MultiplexHub.ClientTransport.Lan, cts.Token);
        await WaitUntilAsync(() => hub.TopicHasSubscribers(PanelTopics.PairCodeRequest), TimeSpan.FromSeconds(5),
            "the desktop subscription never registered");

        cts.Cancel();
        desktop.Unblock();
        await loop;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException(failure);
            await Task.Delay(10);
        }
    }

    /// <summary>Delivers one text message, then blocks in ReceiveAsync until cancelled or unblocked.</summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly byte[] _message;
        private bool _delivered;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedWebSocket(string message) => _message = Encoding.UTF8.GetBytes(message);

        public void Unblock() => _release.TrySetResult();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_delivered)
            {
                _delivered = true;
                _message.CopyTo(buffer.Array!, buffer.Offset);
                return new WebSocketReceiveResult(_message.Length, WebSocketMessageType.Text, endOfMessage: true);
            }
            using var reg = cancellationToken.Register(() => _release.TrySetResult());
            await _release.Task;
            _state = WebSocketState.Closed;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true, WebSocketCloseStatus.NormalClosure, "done");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }
}
