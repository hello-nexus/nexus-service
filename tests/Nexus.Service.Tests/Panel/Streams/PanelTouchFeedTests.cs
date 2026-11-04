using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class PanelTouchFeedTests
{
    private static readonly PanelTouchContact Tap = new(1, TouchPhase.Down, 0.5, 0.25);

    [Fact]
    public void Contacts_with_no_subscriber_are_dropped()
    {
        var feed = new PanelTouchFeed();

        feed.Publish(Tap);
        var reader = feed.Subscribe()!;

        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void A_new_subscriber_supersedes_the_old_one()
    {
        var feed = new PanelTouchFeed();
        var first = feed.Subscribe()!;
        var second = feed.Subscribe()!;

        feed.Publish(Tap);

        Assert.True(first.Completion.IsCompleted);
        Assert.True(second.TryRead(out var contact));
        Assert.Equal(Tap, contact);
    }

    [Fact]
    public void A_stale_unsubscribe_leaves_the_live_subscriber()
    {
        var feed = new PanelTouchFeed();
        var first = feed.Subscribe()!;
        var second = feed.Subscribe()!;

        feed.Unsubscribe(first);
        feed.Publish(Tap);

        Assert.True(second.TryRead(out _));
    }

    [Fact]
    public void A_closed_feed_ends_its_subscriber_and_refuses_new_ones()
    {
        var feed = new PanelTouchFeed();
        var reader = feed.Subscribe()!;

        feed.Close();

        Assert.True(reader.Completion.IsCompleted);
        Assert.Null(feed.Subscribe());
    }

    [Fact]
    public void Lines_carry_the_pointer_phase_and_invariant_fractions()
    {
        Assert.Equal("1 down 0.5 0.25", StreamedPanelRoutes.FormatTouch(Tap));
        Assert.Equal("2 move 0 1", StreamedPanelRoutes.FormatTouch(new PanelTouchContact(2, TouchPhase.Move, 0, 1)));
        Assert.Equal("3 up 0.12346 0.9", StreamedPanelRoutes.FormatTouch(new PanelTouchContact(3, TouchPhase.Up, 0.123456, 0.9)));
    }
}
