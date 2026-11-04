using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the binary lighting hub (<see cref="LightingOutputHub"/>, the 60fps
/// /lighting/output stream) over a real TestServer WebSocket - replacing the
/// old WebSocketHubTests which only asserted ClientCount==0 and a
/// Task.CompletedTask identity on an empty hub.
/// </summary>
public sealed class WebSocketHubIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public WebSocketHubIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;
    private LightingOutputHub Hub => _factory.Services.GetRequiredService<LightingOutputHub>();

    private async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = "Bearer " + Token;
        return await wsClient.ConnectAsync(new Uri("ws://localhost/lighting/output"), ct);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (cts.IsCancellationRequested) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Binary_frame_reaches_client_and_lifecycle_hooks_fire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        int firstClient = 0, allGone = 0;
        Hub.OnFirstClient += () => Interlocked.Increment(ref firstClient);
        Hub.OnAllClientsGone += () => Interlocked.Increment(ref allGone);

        Assert.Equal(0, Hub.ClientCount);
        var ws = await ConnectAsync(ct);
        await WaitFor(() => Hub.ClientCount == 1, "client connected");
        // The hub registers the client before it raises the event, so the count can lead the hook.
        await WaitFor(() => Volatile.Read(ref firstClient) == 1, "OnFirstClient fired"); // 0 -> 1

        var frame = new byte[] { 1, 2, 3, 4, 5 };
        await Hub.BroadcastBinaryAsync(frame);

        var buf = new byte[64];
        using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var res = await ws.ReceiveAsync(buf, rcts.Token);
        Assert.Equal(WebSocketMessageType.Binary, res.MessageType);
        Assert.Equal(frame, buf[..res.Count]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        ws.Dispose();
        await WaitFor(() => Hub.ClientCount == 0, "client removed");
        await WaitFor(() => Volatile.Read(ref allGone) == 1, "OnAllClientsGone fired"); // 1 -> 0
    }
}
