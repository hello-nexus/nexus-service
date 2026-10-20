using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Routes;
using Nexus.Service.Telemetry;
using Xunit;

public class CoolingIntentTelemetryTests
{
    private sealed class RecordingTelemetry : ITelemetry
    {
        public List<(string Event, Dictionary<string, object?> Props)> Events { get; } = new();
        public void Capture(string @event, params (string Key, object? Value)[] properties) =>
            Events.Add((@event, properties.ToDictionary(p => p.Key, p => p.Value)));
        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user")]
    [InlineData("USER")]
    [InlineData("unknown")]
    public void A_user_or_missing_source_captures_fan_speed_set(string? source)
    {
        var t = new RecordingTelemetry();
        CoolingRoutes.CaptureFanWrite(t, new SetFanSpeedBody { Speed = 70, Source = source }, 68);
        var e = Assert.Single(t.Events);
        Assert.Equal(TelemetryEvents.FanSpeedSet, e.Event);
        Assert.Equal(68, e.Props["speed"]);
    }

    [Fact]
    public void An_undo_replay_captures_nothing()
    {
        var t = new RecordingTelemetry();
        CoolingRoutes.CaptureFanWrite(t, new SetFanSpeedBody { Speed = 40, Source = "undo" }, 40);
        Assert.Empty(t.Events);
    }

    [Theory]
    [InlineData("curve", "curve")]
    [InlineData("manual", "manual")]
    [InlineData(null, "manual")]
    [InlineData("anything else", "manual")]
    public void A_seed_write_reports_fan_control_taken_with_the_target(string? target, string expected)
    {
        var t = new RecordingTelemetry();
        CoolingRoutes.CaptureFanWrite(t, new SetFanSpeedBody { Speed = 50, Source = "seed", Target = target }, 50);
        var e = Assert.Single(t.Events);
        Assert.Equal(TelemetryEvents.FanControlTaken, e.Event);
        Assert.Equal(expected, e.Props["target"]);
        Assert.False(e.Props.ContainsKey("speed"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("user", true)]
    [InlineData("exit-off", false)]
    [InlineData("undo", false)]
    [InlineData("unknown", true)]
    public void Preset_capture_skips_only_the_automatic_sources(string? source, bool captured)
    {
        Assert.Equal(captured, CoolingRoutes.ShouldCapturePreset(source));
    }
}
