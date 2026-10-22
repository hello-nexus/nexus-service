using Nexus.Service.Hotkeys;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class ConsoleSessionTrackerTests
{
    [Fact]
    public void Reports_a_change_only_when_the_console_session_differs_from_the_last_seen()
    {
        var tracker = new ConsoleSessionTracker();
        Assert.True(tracker.HasChanged(1));   // first sighting
        Assert.False(tracker.HasChanged(1));
        Assert.True(tracker.HasChanged(3));   // user switched away
        Assert.False(tracker.HasChanged(3));
        Assert.True(tracker.HasChanged(1));   // and back: the set must be pushed to this helper again
    }
}
