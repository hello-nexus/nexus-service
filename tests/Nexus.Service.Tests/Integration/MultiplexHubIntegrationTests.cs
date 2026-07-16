using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the real <see cref="MultiplexHub"/> over an actual TestServer
/// WebSocket - the fan-out / subscribe-lifecycle / disconnect-cleanup path
/// that the unit suite (snapshot-provider registration only) never exercised.
/// </summary>
[Collection("NexusHost")]
public sealed class MultiplexHubIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public MultiplexHubIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;
    private MultiplexHub Hub => _factory.Services.GetRequiredService<MultiplexHub>();

    private async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        // The /ws upgrade goes through PathAuthMiddleware; present the desktop
        // token on the handshake so it authenticates as a trusted local client.
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = "Bearer " + Token;
        return await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), ct);
    }

    private static Task Subscribe(WebSocket ws, string topic, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"sub\":[\"{topic}\"]}}"),
            WebSocketMessageType.Text, endOfMessage: true, ct);

    private static Task Unsubscribe(WebSocket ws, string topic, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"unsub\":[\"{topic}\"]}}"),
            WebSocketMessageType.Text, endOfMessage: true, ct);

    // Subscription registration happens asynchronously on the server's receive
    // loop; poll the hub's own state (the real signal) rather than sleeping a
    // fixed interval. Returns when the predicate holds or throws on timeout.
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static async Task<string> ReceiveText(WebSocket ws, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task AssertNoMessage(WebSocket ws, int windowMs = 750)
    {
        using var cts = new CancellationTokenSource(windowMs);
        var buffer = new byte[8192];
        try
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return; // close is fine; a data frame is not
            Assert.Fail($"Unexpected message delivered: " +
                Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        catch (OperationCanceledException)
        {
            // Expected - nothing arrived in the window.
        }
    }

    [Fact]
    public async Task Broadcast_reaches_subscriber()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        const string topic = "itest-fanout";
        var envelope = Encoding.UTF8.GetBytes("{\"t\":\"itest-fanout\",\"d\":{\"v\":42}}");

        using var ws = await ConnectAsync(ct);
        await Subscribe(ws, topic, ct);
        await WaitFor(() => Hub.TopicSubscriberCount(topic) == 1, "subscriber registered");

        await Hub.BroadcastTopicAsync(topic, envelope);

        Assert.Equal(Encoding.UTF8.GetString(envelope), await ReceiveText(ws));
    }

    [Fact]
    public async Task Broadcast_skips_non_subscriber()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var envelope = Encoding.UTF8.GetBytes("{\"t\":\"topic-a\",\"d\":1}");

        using var wsA = await ConnectAsync(ct);
        using var wsB = await ConnectAsync(ct);
        await Subscribe(wsA, "topic-a", ct);
        await Subscribe(wsB, "topic-b", ct);
        await WaitFor(() => Hub.TopicSubscriberCount("topic-a") == 1
                         && Hub.TopicSubscriberCount("topic-b") == 1, "both subscribed");

        await Hub.BroadcastTopicAsync("topic-a", envelope);

        Assert.Equal(Encoding.UTF8.GetString(envelope), await ReceiveText(wsA));
        await AssertNoMessage(wsB); // B subscribed to a different topic
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        const string topic = "itest-unsub";

        using var ws = await ConnectAsync(ct);
        await Subscribe(ws, topic, ct);
        await WaitFor(() => Hub.TopicSubscriberCount(topic) == 1, "subscribed");
        await Unsubscribe(ws, topic, ct);
        await WaitFor(() => Hub.TopicSubscriberCount(topic) == 0, "unsubscribed");

        await Hub.BroadcastTopicAsync(topic, Encoding.UTF8.GetBytes("{\"x\":1}"));

        await AssertNoMessage(ws);
    }

    [Fact]
    public async Task Disconnect_removes_client_and_subscription()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        const string topic = "itest-disconnect";

        // The hub is shared across this class's tests and server-side client
        // cleanup runs in the receive loop's finally, lagging the client's
        // close - a prior test's disconnect can still be draining here. Wait
        // for the hub to quiesce (no persistent clients exist, so that means
        // zero) before measuring, so the baseline is deterministic.
        await WaitFor(() => Hub.ClientCount == 0, "hub quiescent before baseline");
        var ws = await ConnectAsync(ct);
        await Subscribe(ws, topic, ct);
        await WaitFor(() => Hub.TopicSubscriberCount(topic) == 1, "subscribed");
        await WaitFor(() => Hub.ClientCount == 1, "client registered");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        ws.Dispose();

        // The server's receive loop sees the close and runs its finally-cleanup.
        await WaitFor(() => Hub.TopicSubscriberCount(topic) == 0, "subscription cleaned up");
        await WaitFor(() => Hub.ClientCount == 0, "client entry removed (no leak)");
    }
}
