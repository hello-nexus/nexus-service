using System.Text;
using Nexus.Service.Lifecycle;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class OobeSessionPublisherTests
{
    private static (OobeSessionPublisher publisher, MultiplexHub hub, List<string> frames) Rig(Func<bool> detect)
    {
        var hub = new MultiplexHub();
        var frames = new List<string>();
        hub.OnBroadcastForTest += (topic, payload) =>
        {
            if (topic == PanelTopics.SystemSession) frames.Add(Encoding.UTF8.GetString(payload.ToArray()));
        };
        return (new OobeSessionPublisher(hub, detect), hub, frames);
    }

    [Fact]
    public void Snapshot_CarriesCurrentValue()
    {
        var oobe = true;
        var (_, hub, _) = Rig(() => oobe);

        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out var env));
        Assert.Equal("{\"t\":\"system/session\",\"d\":{\"oobe\":true}}", Encoding.UTF8.GetString(env.ToArray()));

        oobe = false;
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out env));
        Assert.Contains("\"oobe\":false", Encoding.UTF8.GetString(env.ToArray()));
    }

    [Fact]
    public void Publish_BroadcastsOnlyOnChange()
    {
        var oobe = true;
        var (publisher, hub, frames) = Rig(() => oobe);
        using var sub = hub.AddTestSubscription(PanelTopics.SystemSession);

        publisher.Publish();
        publisher.Publish();
        Assert.Single(frames);
        Assert.Contains("\"oobe\":true", frames[0]);

        oobe = false;
        publisher.Publish();
        Assert.Equal(2, frames.Count);
        Assert.Contains("\"oobe\":false", frames[1]);
    }

    [Fact]
    public void SnapshotForNewSubscriber_DoesNotSwallowChangeForExistingOnes()
    {
        var oobe = true;
        var (publisher, hub, frames) = Rig(() => oobe);
        using var a = hub.AddTestSubscription(PanelTopics.SystemSession);
        publisher.Publish();
        frames.Clear();

        oobe = false;
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out _));
        publisher.Publish();

        Assert.Single(frames);
        Assert.Contains("\"oobe\":false", frames[0]);
    }

    [Fact]
    public void DetectorThrowing_ReportsFalse()
    {
        var (publisher, _, _) = Rig(() => throw new InvalidOperationException());
        Assert.False(publisher.Current());
    }

#if DEV_TOOLS
    [Fact]
    public void DevForce_OverridesDetector()
    {
        var (publisher, hub, frames) = Rig(() => false);
        using var sub = hub.AddTestSubscription(PanelTopics.SystemSession);
        publisher.Publish();
        try
        {
            OobeSessionPublisher.DevForce = true;
            publisher.Publish();
            Assert.Contains("\"oobe\":true", frames[^1]);
        }
        finally { OobeSessionPublisher.DevForce = false; }
        publisher.Publish();
        Assert.Contains("\"oobe\":false", frames[^1]);
    }
#endif

    [Fact]
    public void Detector_NonWindows_IsFalse()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(OobeDetector.IsInOobe());
    }
}
