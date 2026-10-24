using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Telemetry;

public sealed class AppErrorLogTests
{
    [Fact]
    public void Limiter_admits_the_cap_per_minute_then_refuses_until_the_window_rolls()
    {
        var limiter = new AppErrorLogLimiter(3);
        Assert.True(limiter.TryAcquire(1_000_000));
        Assert.True(limiter.TryAcquire(1_000_100));
        Assert.True(limiter.TryAcquire(1_059_000));
        Assert.False(limiter.TryAcquire(1_059_500));
        Assert.True(limiter.TryAcquire(1_060_000));
    }

    [Fact]
    public void Format_neutralizes_line_breaks_separators_and_bidi_overrides()
    {
        var line = TelemetryRoutes.FormatAppError("a@1 panel x\r\n", "one\ntwo\u0085three\u007Ffour\u2028five\u2029six\u202Eseven\u2066eight");
        Assert.Equal("[app-error] a@1 panel x  : one two three four five six seven eight", line);
    }

    [Fact]
    public void Format_scrubs_paths_and_addresses_like_the_upload()
    {
        var line = TelemetryRoutes.FormatAppError("a@1 panel x", "failed for me@example.com at 192.168.1.50 in C:\\Users\\bob\\AppData\\x");
        Assert.DoesNotContain("me@example.com", line);
        Assert.DoesNotContain("192.168.1.50", line);
        Assert.DoesNotContain("bob", line);
    }

    [Fact]
    public void Format_caps_the_message_and_marks_empty_fields()
    {
        Assert.Equal("[app-error] ?: ?", TelemetryRoutes.FormatAppError(null, ""));
        Assert.True(TelemetryRoutes.FormatAppError("t", new string('a', 5000)).Length <= "[app-error] t: ".Length + 500);
    }
}
