using System.Text;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class CloudAccountsBroadcastTests
{
    [Fact]
    public void BroadcastCloudAccounts_NoSubscribers_DoesNotHitWire()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        PanelTopics.BroadcastCloudAccounts(hub);

        Assert.Empty(captured);
    }

    [Fact]
    public void BroadcastCloudAccounts_WithSubscriber_EmitsRevisionEnvelope()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.CloudAccounts);

        PanelTopics.BroadcastCloudAccounts(hub);

        var frame = captured.Single(c => c.Topic == PanelTopics.CloudAccounts);
        var json = Encoding.UTF8.GetString(frame.Payload);
        Assert.StartsWith("{\"t\":\"cloud/accounts\"", json);
        Assert.Contains("\"revision\":", json);
    }
}
