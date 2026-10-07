using System.Text.Json;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class ErrorReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-err-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigStore _store = new();
    private readonly CapturingTransport _transport = new();
    private readonly TestClock _clock = new();

    public ErrorReporterTests() => _store.Update(s => s.Telemetry.CollectAnonymousData = true);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CrashFile => Path.Combine(_dir, "error-crash.json");

    private ErrorReporter Reporter() => new(_store, _transport, _clock, CrashFile);

    private static Exception Thrown(string msg = "boom")
    {
        try { Throw(msg); }
        catch (Exception e) { return e; }
        return null!;
    }

    private static void Throw(string msg) => throw new InvalidOperationException(msg);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingTransport : IErrorTransport
    {
        public List<ErrorReportPayload> Sent { get; } = new();
        public bool Result { get; set; } = true;

        public Task<bool> SendAsync(ErrorReportPayload payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Opted_out_reports_nothing_and_opt_out_clears_state_and_crash_file()
    {
        var r = Reporter();
        r.Report(Thrown(), "logged", null);
        ErrorReporter.WriteCrashFile(Thrown("crash"), CrashFile);
        Assert.Equal(1, r.PendingCount);
        Assert.True(File.Exists(CrashFile));

        _store.Update(s => s.Telemetry.CollectAnonymousData = false);
        Assert.Equal(0, r.PendingCount);
        Assert.False(File.Exists(CrashFile));

        r.Report(Thrown(), "logged", null);
        r.ReportClient("render", "fp", "T", "m", "s", null, 1);
        await r.FlushAsync(default);
        Assert.Equal(0, r.PendingCount);
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public void Fingerprint_ignores_line_numbers_and_offsets()
    {
        var a = "System.X: m\n   at Foo.Bar(String s) in /a/b.cs:line 10\n   at Foo.Baz() in /a/b.cs:line 20";
        var b = "System.X: m\n   at Foo.Bar(String s) in /a/b.cs:line 99\n   at Foo.Baz() + 0x2a";
        Assert.Equal(ErrorReporter.Fingerprint("service", "T", a), ErrorReporter.Fingerprint("service", "T", b));
        Assert.NotEqual(ErrorReporter.Fingerprint("service", "T", a), ErrorReporter.Fingerprint("web", "T", a));
        Assert.NotEqual(ErrorReporter.Fingerprint("service", "T", a), ErrorReporter.Fingerprint("service", "U", a));
        Assert.Equal(32, ErrorReporter.Fingerprint("service", "T", a).Length);
    }

    [Fact]
    public void Same_error_from_one_site_aggregates_into_one_item_with_count()
    {
        var r = Reporter();
        for (var i = 0; i < 3; i++)
            r.Report(Thrown(), "logged", null);
        Assert.Equal(1, r.PendingCount);
    }

    [Fact]
    public void Scrub_replaces_profile_path_user_email_and_ip()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        var scrubbed = ErrorReporter.Scrub($"open {profile}/x failed for me@example.com at 192.168.1.50 as {user}");
        Assert.DoesNotContain(profile, scrubbed);
        Assert.DoesNotContain("me@example.com", scrubbed);
        Assert.DoesNotContain("192.168.1.50", scrubbed);
        Assert.Contains("~/x", scrubbed);
        Assert.Contains("<email>", scrubbed);
        Assert.Contains("<ip>", scrubbed);
        if (user.Length >= 3)
            Assert.DoesNotContain(user, scrubbed.Replace("~", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Built_item_is_truncated_to_the_contract_caps()
    {
        var item = Reporter().BuildItem(Thrown(new string('m', 5000)), new string('k', 100), new string('c', 5000));
        Assert.True(item.Kind.Length <= 32);
        Assert.True(item.Message.Length <= 1000);
        Assert.True(item.Stack.Length <= 8000);
        Assert.True(item.Context.Length <= 500);
        Assert.True(item.Type.Length <= 128);
    }

    [Fact]
    public async Task Distinct_fingerprints_are_capped_per_process()
    {
        var r = Reporter();
        for (var i = 0; i < ErrorReporter.MaxDistinct + 50; i++)
            r.ReportClient("render", "fp" + i, "T", "m", "s", null, 1);
        Assert.Equal(ErrorReporter.MaxDistinct, r.PendingCount);
        await r.FlushAsync(default);
        Assert.Equal(ErrorReporter.MaxDistinct - ErrorReporter.MaxPerPost, r.PendingCount);
    }

    [Fact]
    public async Task Post_carries_at_most_twenty_items()
    {
        var r = Reporter();
        for (var i = 0; i < 30; i++)
            r.ReportClient("render", "fp" + i, "T", "m", "s", null, 1);
        await r.FlushAsync(default);
        Assert.Single(_transport.Sent);
        Assert.Equal(20, _transport.Sent[0].Errors.Count);
    }

    [Fact]
    public async Task A_fingerprint_is_sent_at_most_once_per_hour_and_keeps_its_count()
    {
        var r = Reporter();
        r.ReportClient("render", "fp", "T", "m", "s", null, 1);
        await r.FlushAsync(default);
        Assert.Single(_transport.Sent);

        r.ReportClient("render", "fp", "T", "m", "s", null, 2);
        r.ReportClient("render", "fp", "T", "m", "s", null, 3);
        await r.FlushAsync(default);
        Assert.Single(_transport.Sent);
        Assert.Equal(1, r.PendingCount);

        _clock.Now += TimeSpan.FromMinutes(61);
        await r.FlushAsync(default);
        Assert.Equal(2, _transport.Sent.Count);
        Assert.Equal(5, _transport.Sent[1].Errors.Single().Count);
        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public async Task Empty_web_type_defaults_to_Error_and_malformed_items_are_dropped_not_the_batch()
    {
        var r = Reporter();
        r.ReportClient("render", "fp1", "  ", "m", "s", null, 1);
        r.ReportClient("render", "", "T", "m", "s", null, 1);
        r.ReportClient("", "fp3", "T", "m", "s", null, 1);
        await r.FlushAsync(default);
        var item = Assert.Single(Assert.Single(_transport.Sent).Errors);
        Assert.Equal("Error", item.Type);
        Assert.Equal("fp1", item.Fingerprint);
    }

    [Fact]
    public async Task Crash_file_written_while_opted_out_is_never_sent_and_is_deleted_on_flush()
    {
        _store.Update(s => s.Telemetry.CollectAnonymousData = false);
        var r = Reporter();
        ErrorReporter.WriteCrashFile(Thrown(), CrashFile);
        Assert.True(File.Exists(CrashFile));
        await r.FlushAsync(default);
        Assert.Empty(_transport.Sent);
        Assert.False(File.Exists(CrashFile));
    }

    [Fact]
    public async Task A_failed_send_keeps_nothing()
    {
        _transport.Result = false;
        var r = Reporter();
        r.ReportClient("render", "fp", "T", "m", "s", null, 1);
        await r.FlushAsync(default);
        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public async Task Crash_file_is_written_delivered_on_next_flush_and_deleted()
    {
        ErrorReporter.WriteCrashFile(Thrown("dying"), CrashFile);
        Assert.True(File.Exists(CrashFile));
        Assert.True(new FileInfo(CrashFile).Length < ErrorReporter.MaxCrashFileBytes);

        var next = Reporter();
        await next.FlushAsync(default);
        var sent = Assert.Single(_transport.Sent);
        var item = Assert.Single(sent.Errors);
        Assert.Equal("crash", item.Kind);
        Assert.Equal("service", item.Source);
        Assert.False(File.Exists(CrashFile));
    }

    [Fact]
    public async Task Crash_file_survives_a_failed_send()
    {
        ErrorReporter.WriteCrashFile(Thrown(), CrashFile);
        _transport.Result = false;
        await Reporter().FlushAsync(default);
        Assert.True(File.Exists(CrashFile));
    }

    [Fact]
    public async Task Payload_json_matches_the_nexus_api_contract()
    {
        var r = Reporter();
        r.ReportClient("render", "fp", "TypeError", "msg", "stack", "ctx", 3);
        await r.FlushAsync(default);

        var json = JsonSerializer.Serialize(_transport.Sent[0], AppJsonContext.Default.ErrorReportPayload);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(
            new[] { "installId", "version", "os", "osVersion", "devTools", "errors" }.OrderBy(x => x),
            root.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        var e = root.GetProperty("errors")[0];
        Assert.Equal(
            new[] { "source", "kind", "fingerprint", "type", "message", "stack", "context", "count", "firstSeen", "lastSeen" }.OrderBy(x => x),
            e.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal("web", e.GetProperty("source").GetString());
        Assert.Equal(3, e.GetProperty("count").GetInt32());
        var first = e.GetProperty("firstSeen").GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", first);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", e.GetProperty("lastSeen").GetString()!);
        Assert.True(root.GetProperty("devTools").ValueKind is JsonValueKind.True or JsonValueKind.False);
    }
}
