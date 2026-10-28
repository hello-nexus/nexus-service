using System.Text;
using Nexus.Service.Lifecycle;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class RestrictedModePublisherTests
{
    private static (RestrictedModePublisher publisher, MultiplexHub hub, List<string> frames) Rig(Func<bool> detect)
    {
        var hub = new MultiplexHub();
        var frames = new List<string>();
        hub.OnBroadcastForTest += (topic, payload) =>
        {
            if (topic == PanelTopics.SystemSession) frames.Add(Encoding.UTF8.GetString(payload.ToArray()));
        };
        return (new RestrictedModePublisher(hub, detect), hub, frames);
    }

    [Fact]
    public void Snapshot_CarriesCurrentValue()
    {
        var restricted = true;
        var (_, hub, _) = Rig(() => restricted);

        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out var env));
        Assert.Equal("{\"t\":\"system/session\",\"d\":{\"restricted\":true}}", Encoding.UTF8.GetString(env.ToArray()));

        restricted = false;
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out env));
        Assert.Contains("\"restricted\":false", Encoding.UTF8.GetString(env.ToArray()));
    }

    [Fact]
    public void Publish_BroadcastsOnlyOnChange()
    {
        var restricted = true;
        var (publisher, hub, frames) = Rig(() => restricted);
        using var sub = hub.AddTestSubscription(PanelTopics.SystemSession);

        publisher.Publish();
        publisher.Publish();
        Assert.Single(frames);
        Assert.Contains("\"restricted\":true", frames[0]);

        restricted = false;
        publisher.Publish();
        Assert.Equal(2, frames.Count);
        Assert.Contains("\"restricted\":false", frames[1]);
    }

    [Fact]
    public void SnapshotForNewSubscriber_DoesNotSwallowChangeForExistingOnes()
    {
        var restricted = true;
        var (publisher, hub, frames) = Rig(() => restricted);
        using var a = hub.AddTestSubscription(PanelTopics.SystemSession);
        publisher.Publish();
        frames.Clear();

        restricted = false;
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.SystemSession, out _));
        publisher.Publish();

        Assert.Single(frames);
        Assert.Contains("\"restricted\":false", frames[0]);
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
            RestrictedModePublisher.DevForce = true;
            publisher.Publish();
            Assert.Contains("\"restricted\":true", frames[^1]);
        }
        finally { RestrictedModePublisher.DevForce = false; }
        publisher.Publish();
        Assert.Contains("\"restricted\":false", frames[^1]);
    }
#endif

    [Fact]
    public void Detector_NonWindows_IsFalse()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(OobeDetector.IsInOobe());
    }
}
