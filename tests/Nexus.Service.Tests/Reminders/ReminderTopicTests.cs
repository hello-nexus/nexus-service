using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Reminders;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Reminders;

/// <summary>Exercises the production AddNexusReminders wiring: a mutation reaches a socket subscribed to the reminders topic.</summary>
public sealed class ReminderTopicTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public ReminderTopicTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task A_mutation_pings_subscribers_of_the_reminders_topic()
    {
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        var ws = _factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = req => req.Headers["Authorization"] = "Bearer " + token;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost/ws"), cts.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"sub":["reminders"]}"""), WebSocketMessageType.Text, true, cts.Token);
        var hub = _factory.Services.GetRequiredService<Nexus.Service.Sockets.MultiplexHub>();
        while (!hub.TopicHasSubscribers("reminders"))
        {
            await Task.Delay(10, cts.Token);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listId = _factory.Services.GetRequiredService<ReminderService>().GetAll().Lists[0].Id;
        var res = await client.PostAsync("/api/reminders/items", new StringContent($$"""{"listId":"{{listId}}","title":"ping"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, cts.Token);
        var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Contains("reminders", text);
        Assert.Contains("revision", text);
    }
}
