using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class DisplayIdentifyServiceTests
{
    private sealed class FakeHost : IOverlayHost
    {
        public bool Running;
        public int StartCalls;
        public List<string> Identifies = new();
        public bool IsRunning => Running;
        public bool Start() { StartCalls++; Running = true; return true; }
        public void Stop() { Running = false; }
        public void NotifyDisplayAssignmentsChanged() { }
        public void NotifyIdentify(string payloadJson) => Identifies.Add(payloadJson);
    }

    private static IdentifyDisplayEntry Entry(string id, string label, string? name = null)
        => new() { DisplayId = id, Label = label, Name = name };

    [Fact]
    public void Begin_exposes_the_labels_until_the_duration_elapses()
    {
        long now = 1000;
        var svc = new DisplayIdentifyService(new FakeHost(), null, () => now);

        svc.Begin(new[] { Entry("a", "1"), Entry("b", "2") });

        var cur = svc.Current();
        Assert.NotNull(cur);
        Assert.Equal(2, cur!.Displays.Count);
        Assert.Equal(DisplayIdentifyService.DurationMs, cur.RemainingMs);

        now += DisplayIdentifyService.DurationMs - 1;
        Assert.Equal(1, svc.Current()!.RemainingMs);

        now += 1;
        Assert.Null(svc.Current());
    }

    [Fact]
    public void A_second_call_restarts_the_timer_with_a_new_token_and_labels()
    {
        long now = 0;
        var svc = new DisplayIdentifyService(new FakeHost(), null, () => now);
        svc.Begin(new[] { Entry("a", "1") });
        var first = svc.Current()!;

        now += 2000;
        svc.Begin(new[] { Entry("a", "7") });
        var second = svc.Current()!;

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal("7", second.Displays[0].Label);
        Assert.Equal(DisplayIdentifyService.DurationMs, second.RemainingMs);
    }

    [Fact]
    public void Begin_starts_a_stopped_host_pushes_the_payload_and_wakes()
    {
        var host = new FakeHost();
        var woke = 0;
        var svc = new DisplayIdentifyService(host, () => woke++, () => 0);

        svc.Begin(new[] { Entry("a", "1", "Dell") });

        Assert.Equal(1, host.StartCalls);
        Assert.Equal(1, woke);
        var line = Assert.Single(host.Identifies);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"displayId\":\"a\"", line);
        Assert.Contains("\"name\":\"Dell\"", line);
    }

    [Fact]
    public void An_empty_request_clears_the_identify_and_touches_no_host()
    {
        var host = new FakeHost();
        var svc = new DisplayIdentifyService(host, null, () => 0);
        svc.Begin(new[] { Entry("a", "1") });
        host.Identifies.Clear();

        svc.Begin(new List<IdentifyDisplayEntry>());

        Assert.Null(svc.Current());
        Assert.False(svc.IsActive);
        Assert.Empty(host.Identifies);
    }

    [Fact]
    public void Sanitize_drops_oversized_and_unknown_ids()
    {
        var known = new HashSet<string> { "a", new string('i', 200) };

        var kept = DisplayIdentifyService.Sanitize(new[] { Entry("a", "1"), Entry("zz", "2"), Entry(new string('i', 200), "3") }, known);

        Assert.Equal("a", Assert.Single(kept).DisplayId);
    }

    [Fact]
    public void Sanitize_clips_by_text_element_never_splitting_a_surrogate_pair()
    {
        var kept = DisplayIdentifyService.Sanitize(new[] { Entry("a", new string('x', 7) + "\U0001F600\U0001F600", "\U0001F600" + new string('n', 60)) });

        Assert.Equal(new string('x', 7) + "\U0001F600", kept[0].Label);
        Assert.Equal("\U0001F600" + new string('n', 39), kept[0].Name);
    }

    [Fact]
    public void Begin_drops_ids_the_topology_does_not_know()
    {
        var host = new FakeHost();
        var svc = new DisplayIdentifyService(host, null, () => 0, () => new[] { "a" });

        var kept = svc.Begin(new[] { Entry("a", "1"), Entry("ghost", "2") });

        Assert.Equal("a", Assert.Single(kept).DisplayId);
        Assert.DoesNotContain("ghost", Assert.Single(host.Identifies));
    }

    [Fact]
    public void Sanitize_drops_blank_and_duplicate_ids_and_clips_text()
    {
        var kept = DisplayIdentifyService.Sanitize(new[]
        {
            Entry("a", "123456789012", new string('n', 80)),
            Entry("a", "dup"),
            Entry("  ", "x"),
            Entry("b", " 2 ", "  "),
        });

        Assert.Equal(2, kept.Count);
        Assert.Equal(8, kept[0].Label.Length);
        Assert.Equal(40, kept[0].Name!.Length);
        Assert.Equal("2", kept[1].Label);
        Assert.Null(kept[1].Name);
    }
}
