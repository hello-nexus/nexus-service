#if DEV_TOOLS
using System.Text.Json;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Tests.Telemetry;

public class AppEventTelemetryTests
{
    private static AppTelemetryRequest Req(string json) =>
        JsonSerializer.Deserialize<AppTelemetryRequest>(json)!;

    [Fact]
    public void Accepts_enum_like_properties_and_prefixes_them()
    {
        var props = AppTelemetryValidator.Validate(Req(
            """{"event":"tab_viewed","surface":"page","properties":{"tab":"orders","count":3,"on":true,"v":"a.b:c-d_1"}}"""),
            out var error)!;

        Assert.Null(error);
        var map = props.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("orders", map["p_tab"]);
        Assert.Equal(3d, map["p_count"]);
        Assert.Equal(true, map["p_on"]);
        Assert.Equal("a.b:c-d_1", map["p_v"]);
    }

    [Theory]
    [InlineData("""{"event":"Tab"}""")]
    [InlineData("""{"event":"1tab"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"event":"a_very_long_event_name_that_goes_past_forty_chars"}""")]
    [InlineData("""{"event":"e","surface":"popup"}""")]
    [InlineData("""{"event":"e","properties":{"Bad":"x"}}""")]
    [InlineData("""{"event":"e","properties":{"k":"has space"}}""")]
    [InlineData("""{"event":"e","properties":{"k":"a@b.com"}}""")]
    [InlineData("""{"event":"e","properties":{"k":"https://x.com"}}""")]
    [InlineData("""{"event":"e","properties":{"k":"UPPER"}}""")]
    [InlineData("""{"event":"e","properties":{"k":""}}""")]
    [InlineData("""{"event":"e","properties":{"k":null}}""")]
    [InlineData("""{"event":"e","properties":{"k":["a"]}}""")]
    [InlineData("""{"event":"e","properties":{"k":{"a":1}}}""")]
    [InlineData("""{"event":"e","properties":{"k":1e999}}""")]
    public void Rejects_free_text_and_bad_shapes(string json)
    {
        Assert.Null(AppTelemetryValidator.Validate(Req(json), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_more_than_ten_properties_and_overlong_values()
    {
        var eleven = string.Join(",", Enumerable.Range(0, 11).Select(i => $"\"k{i}\":1"));
        Assert.Null(AppTelemetryValidator.Validate(Req($$$"""{"event":"e","properties":{ {{{eleven}}} }}"""), out _));
        var ten = string.Join(",", Enumerable.Range(0, 10).Select(i => $"\"k{i}\":1"));
        Assert.NotNull(AppTelemetryValidator.Validate(Req($$$"""{"event":"e","properties":{ {{{ten}}} }}"""), out _));

        var longValue = new string('a', 65);
        Assert.Null(AppTelemetryValidator.Validate(Req($$$"""{"event":"e","properties":{"k":"{{{longValue}}}"}}"""), out _));
        Assert.NotNull(AppTelemetryValidator.Validate(Req($$$"""{"event":"e","properties":{"k":"{{{longValue[..64]}}}"}}"""), out _));
    }

    [Fact]
    public void Compose_puts_server_owned_fields_first_and_omits_absent_surface()
    {
        var body = Req("""{"event":"e","properties":{"k":"v"}}""");
        var props = AppTelemetryValidator.Compose("a.b.c", "2.0.0", body,
            AppTelemetryValidator.Validate(body, out _)!).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("a.b.c", props["app_id"]);
        Assert.Equal("2.0.0", props["app_version"]);
        Assert.Equal("e", props["event"]);
        Assert.Equal(true, props["dev_tools"]);
        Assert.Equal("v", props["p_k"]);
        Assert.DoesNotContain("surface", props.Keys);
    }

    [Theory]
    [InlineData(12345, 12d)]
    [InlineData(12500, 13d)]
    [InlineData(-50, 0d)]
    [InlineData(1e12, 86400d)]
    [InlineData(double.NaN, 0d)]
    public void Page_closed_duration_is_rounded_and_clamped(double ms, double expected)
    {
        var props = AppTelemetryValidator.ComposePageClosed("a.b.c", "1.0.0", ms).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(expected, props["duration_s"]);
        Assert.Equal("1.0.0", props["app_version"]);
    }

    private sealed class FakeTelemetry : ITelemetry
    {
        public List<string> Sent { get; } = new();
        public void Capture(string @event, params (string Key, object? Value)[] properties) => Sent.Add(@event);
        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    [Fact]
    public void Recorder_keeps_only_app_events_newest_first_and_caps_at_200()
    {
        var inner = new FakeTelemetry();
        var recorder = new AppEventRecorder(inner, () => "off");

        recorder.Capture(TelemetryEvents.FanSpeedSet, ("x", 1));
        recorder.Capture(TelemetryEvents.AppStarted);
        for (var i = 0; i < 205; i++) recorder.Capture(TelemetryEvents.AppEvent, ("n", i));

        var recent = recorder.Recent();
        Assert.Equal(AppEventRecorder.Capacity, recent.Count);
        Assert.Equal(204, recent[0].Properties[0].Value);
        Assert.Equal(5, recent[^1].Properties[0].Value);
        Assert.Equal(207, inner.Sent.Count);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("opted_out")]
    public void Recent_json_reports_status_and_the_exact_properties(string status)
    {
        var recorder = new AppEventRecorder(new FakeTelemetry(), () => status,
            () => new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        recorder.Capture(TelemetryEvents.AppEvent, ("app_id", "a.b.c"), ("p_n", 3d), ("dev_tools", true));

        using var doc = JsonDocument.Parse(recorder.RecentJson());
        Assert.Equal(status, doc.RootElement.GetProperty("posthog").GetString());
        var e = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("app_event", e.GetProperty("event").GetString());
        Assert.Equal("2026-10-03T12:00:00.0000000Z", e.GetProperty("at").GetString());
        var p = e.GetProperty("properties");
        Assert.Equal("a.b.c", p.GetProperty("app_id").GetString());
        Assert.Equal(3, p.GetProperty("p_n").GetDouble());
        Assert.True(p.GetProperty("dev_tools").GetBoolean());
    }
}
#endif
