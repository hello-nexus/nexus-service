using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Tests.LianLi;

public class LianLiWireTraceTests
{
    private readonly LianLiHub _hub = new();
    private readonly HubTransportSpy _spy = new();
    private readonly List<string> _lines = new();

    private void Attach(int pid)
    {
        _hub.Trace.Sink = _lines.Add;
        Assert.True(LianLiFanProfiles.TryGet(pid, out var profile));
        _hub.Attach(_spy, profile);
    }

    [Fact]
    public void An_sl_v2_traces_each_write_with_its_first_bytes_and_result()
    {
        Attach(0xA105);

        _hub.SendFrameSync();
        _hub.SetQuantity(1, 3);

        Assert.Contains(_lines, l => l.Contains("tracing every write"));
        Assert.Contains(_lines, l => l.Contains("feature E0600004") && l.EndsWith("ok"));
        Assert.Contains(_lines, l => l.Contains("feature E0106013") && l.Contains("len=7"));
    }

    [Fact]
    public void A_rejected_write_is_traced_as_failed()
    {
        Attach(0xA105);
        _spy.RejectWrites = true;

        _hub.SendFrameSync();

        Assert.Contains(_lines, l => l.EndsWith("FAIL"));
    }

    [Fact]
    public void Other_families_are_not_traced()
    {
        Attach(0xA102);

        _hub.SendFrameSync();

        Assert.Empty(_lines);
    }

    [Fact]
    public void The_trace_stops_at_its_line_budget()
    {
        Attach(0xA105);

        for (var i = 0; i < 2000; i++) _hub.SendFrameSync();

        Assert.InRange(_lines.Count, 100, 450);
    }

    [Fact]
    public void Only_the_first_applies_get_extra_lines_once_the_window_budget_is_spent()
    {
        Attach(0xA105);
        for (var i = 0; i < 2000; i++) _hub.SendFrameSync();
        var spent = _lines.Count;

        for (var i = 0; i < 40; i++)
        {
            _hub.Trace.BeginApply("mode=tide");
            for (var w = 0; w < 5; w++) _hub.SendFrameSync();
        }

        var applyLines = _lines.Skip(spent).Count(l => l.Contains(" apply mode=tide"));
        Assert.Equal(12, applyLines);
    }

    [Fact]
    public void Only_the_first_attaches_are_traced_so_a_flapping_hub_cannot_grow_the_log()
    {
        Assert.True(LianLiFanProfiles.TryGet(0xA105, out var profile));
        _hub.Trace.Sink = _lines.Add;

        for (var i = 0; i < 20; i++)
        {
            _hub.Attach(_spy, profile);
            _hub.SendFrameSync();
            _hub.Detach();
        }

        Assert.Equal(5, _lines.Count(l => l.Contains("tracing every write")));
        Assert.True(_lines.Count <= 5 * 2);
    }

    [Fact]
    public void Detach_stops_the_trace()
    {
        Attach(0xA105);
        _hub.Detach();
        _lines.Clear();

        _hub.Trace.Note("feature", new byte[] { 0xE0, 0x60 }, true);

        Assert.Empty(_lines);
    }
}
