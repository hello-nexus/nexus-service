using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class UsageTelemetryTests
{
    private sealed class FakeUsageTransport : IUsageTransport
    {
        public List<(string InstallId, string Version, List<UsageEvent> Batch)> Sent { get; } = new();
        public Queue<UsageDelivery> Results { get; } = new();

        public Task<UsageSendResult> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct)
        {
            Sent.Add((installId, version, batch.ToList()));
            var d = Results.Count > 0 ? Results.Dequeue() : UsageDelivery.Delivered;
            return Task.FromResult(new UsageSendResult(d, d == UsageDelivery.Drop ? 400 : 200));
        }
    }

    private static InMemoryConfigStore Store(bool collect, string installId = "install-1")
    {
        var s = new InMemoryConfigStore();
        s.Update(x =>
        {
            x.Telemetry.CollectAnonymousData = collect;
            x.Telemetry.InstallId = installId;
        });
        return s;
    }

    private static TelemetryEvent Ev(string name, DateTimeOffset? ts = null, params (string, object?)[] props) => new()
    {
        Name = name,
        Timestamp = ts ?? DateTimeOffset.UtcNow,
        Properties = props.Select(p => new KeyValuePair<string, object?>(p.Item1, p.Item2)).ToArray(),
    };

    private static async Task Feed(UsageTelemetrySink sink, params TelemetryEvent[] events) =>
        await sink.SendAsync("install-1", events, CancellationToken.None);

    [Fact]
    public async Task Sink_mirrors_events_and_includes_app_started()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, Ev(TelemetryEvents.AppStarted), Ev("fan_curve_applied", null, ("preset", "quiet")));

        var batch = sink.Take(10);
        Assert.Equal(new[] { "app_started", "fan_curve_applied" }, batch.Select(e => e.Name));
        Assert.All(batch, e => Assert.True(Guid.TryParse(e.Id, out _)));
        Assert.Equal(2, batch.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task Identify_is_never_mirrored_as_identify_and_becomes_a_profile_event_with_allowlisted_fields_only()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, new TelemetryEvent
        {
            Name = "$identify",
            Timestamp = DateTimeOffset.UtcNow,
            Set = new[]
            {
                new KeyValuePair<string, object?>("cpu", "Ryzen"),
                new KeyValuePair<string, object?>("os_build", "Windows 11"),
                new KeyValuePair<string, object?>("devices", new[] { "A", "B" }),
                new KeyValuePair<string, object?>("widget_count", 3),
            },
        });

        var e = Assert.Single(sink.Take(10));
        Assert.Equal("profile", e.Name);
        Assert.Equal(new[] { "os_build", "devices", "widget_count" }, e.Props.Select(p => p.Key));
    }

    [Fact]
    public async Task Identify_without_profile_fields_is_skipped()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, new TelemetryEvent
        {
            Name = "$identify",
            Timestamp = DateTimeOffset.UtcNow,
            Set = new[] { new KeyValuePair<string, object?>("cpu", "Ryzen") },
        });
        Assert.Empty(sink.Take(10));
    }

    [Fact]
    public async Task Queue_is_bounded_and_drops_oldest()
    {
        var sink = new UsageTelemetrySink(true);
        for (var i = 0; i < UsageTelemetrySink.MaxQueued + 5; i++)
            await Feed(sink, Ev("e" + i));

        Assert.Equal(UsageTelemetrySink.MaxQueued, sink.Count);
        Assert.Equal("e5", sink.Take(1).Single().Name);
    }

    [Fact]
    public async Task Flush_batches_at_200_and_sends_install_id_and_version()
    {
        var sink = new UsageTelemetrySink(true);
        for (var i = 0; i < 450; i++)
            await Feed(sink, Ev("e" + i));
        var transport = new FakeUsageTransport();
        var worker = new UsageTelemetryWorker(sink, transport, Store(true));

        await worker.FlushAsync(CancellationToken.None);

        Assert.Equal(new[] { 200, 200, 50 }, transport.Sent.Select(s => s.Batch.Count));
        Assert.All(transport.Sent, s =>
        {
            Assert.Equal("install-1", s.InstallId);
            Assert.Equal(BuildInfo.Version, s.Version);
        });
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task Retry_keeps_the_batch_in_order_with_the_same_ids_for_the_next_flush()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, Ev("a"), Ev("b"));
        var transport = new FakeUsageTransport();
        transport.Results.Enqueue(UsageDelivery.Retry);
        var worker = new UsageTelemetryWorker(sink, transport, Store(true));

        await worker.FlushAsync(CancellationToken.None);
        await Feed(sink, Ev("c"));
        await worker.FlushAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count);
        var firstIds = transport.Sent[0].Batch.Select(e => e.Id).ToArray();
        Assert.Equal(firstIds, transport.Sent[1].Batch.Take(2).Select(e => e.Id));
        Assert.Equal(new[] { "a", "b", "c" }, transport.Sent[1].Batch.Select(e => e.Name));
    }

    [Fact]
    public async Task Drop_discards_the_batch_and_continues()
    {
        var sink = new UsageTelemetrySink(true);
        for (var i = 0; i < 250; i++)
            await Feed(sink, Ev("e" + i));
        var transport = new FakeUsageTransport();
        transport.Results.Enqueue(UsageDelivery.Drop);
        var worker = new UsageTelemetryWorker(sink, transport, Store(true));

        await worker.FlushAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(0, sink.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Delivered")]
    [InlineData(HttpStatusCode.Accepted, "Delivered")]
    [InlineData(HttpStatusCode.BadRequest, "Drop")]
    [InlineData(HttpStatusCode.Unauthorized, "Drop")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Drop")]
    [InlineData(HttpStatusCode.TooManyRequests, "Retry")]
    [InlineData(HttpStatusCode.RequestTimeout, "Retry")]
    [InlineData(HttpStatusCode.InternalServerError, "Retry")]
    [InlineData(HttpStatusCode.BadGateway, "Retry")]
    public void Status_codes_map_to_the_delivery_rules(HttpStatusCode status, string expected) =>
        Assert.Equal(expected, UsageTransport.Classify(status).ToString());

    [Fact]
    public async Task Opted_out_clears_the_queue_and_sends_nothing()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, Ev("a"));
        var transport = new FakeUsageTransport();
        var worker = new UsageTelemetryWorker(sink, transport, Store(false));

        await worker.FlushAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task Opted_out_still_delivers_a_queued_opt_out_with_the_stored_install_id()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, Ev("a"), Ev(TelemetryEvents.OptOut, null, ("version", "1.0.0")));
        var transport = new FakeUsageTransport();
        var worker = new UsageTelemetryWorker(sink, transport, Store(false, "stored-id"));

        await worker.FlushAsync(CancellationToken.None);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("stored-id", sent.InstallId);
        Assert.Equal(new[] { "opt_out" }, sent.Batch.Select(e => e.Name));
    }

    [Fact]
    public async Task Oversized_props_are_trimmed_to_fit_the_cap()
    {
        var sink = new UsageTelemetrySink(true);
        var huge = Enumerable.Range(0, 400).Select(i => "widget-type-number-" + i + new string('x', 60)).ToArray();
        await Feed(sink, Ev("layout_snapshot", null, ("widget_types", huge), ("widget_count", 400), ("note", new string('n', 5000))));

        var e = Assert.Single(sink.Take(10));
        var buffer = new ArrayBufferWriter<byte>();
        UsageTransport.WriteBody(buffer, "i", "v", new List<UsageEvent> { e });
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var props = doc.RootElement.GetProperty("events")[0].GetProperty("props");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(props.GetRawText()) <= UsageTelemetrySink.MaxPropsBytes);
        Assert.Equal(400, props.GetProperty("widget_count").GetInt32());
        Assert.True(props.GetProperty("widget_types").GetArrayLength() is > 0 and < 400);
    }

    private sealed class ThrowingSink : ITelemetrySink
    {
        public bool Enabled => true;
        public Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct) =>
            throw new System.Net.Http.HttpRequestException("blocked");
    }

    [Fact]
    public async Task A_throwing_posthog_sink_does_not_stop_the_usage_mirror()
    {
        var store = Store(true);
        var client = new TelemetryClient(store);
        client.Capture("fan_curve_applied");
        var usage = new UsageTelemetrySink(true);
        var flush = new TelemetryFlushService(client, store, new ITelemetrySink[] { new ThrowingSink(), usage });

        await flush.FlushAsync(CancellationToken.None);

        Assert.Equal(new[] { "fan_curve_applied" }, usage.Take(10).Select(e => e.Name));
    }

    [Fact]
    public async Task Repeated_drop_with_the_same_status_is_logged_once()
    {
        var sink = new UsageTelemetrySink(true);
        await Feed(sink, Ev("a"));
        var transport = new FakeUsageTransport();
        transport.Results.Enqueue(UsageDelivery.Drop);
        var lines = new List<string>();
        var worker = new UsageTelemetryWorker(sink, transport, Store(true), lines.Add);

        await worker.FlushAsync(CancellationToken.None);
        await Feed(sink, Ev("b"));
        transport.Results.Enqueue(UsageDelivery.Drop);
        await worker.FlushAsync(CancellationToken.None);

        Assert.Single(lines); // same status twice logs once
    }

    [Fact]
    public void Body_matches_the_contract_shape()
    {
        var ts = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
        var batch = new List<UsageEvent>
        {
            new()
            {
                Id = "id-1",
                Name = "fan_curve_applied",
                Timestamp = ts,
                Props = new[]
                {
                    new KeyValuePair<string, object?>("preset", "quiet"),
                    new KeyValuePair<string, object?>("channels", 3),
                    new KeyValuePair<string, object?>("tags", new[] { "x", "y" }),
                },
            },
        };
        var buffer = new ArrayBufferWriter<byte>();

        UsageTransport.WriteBody(buffer, "install-1", "1.2.3", batch);

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var root = doc.RootElement;
        Assert.Equal("install-1", root.GetProperty("installId").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        var e = Assert.Single(root.GetProperty("events").EnumerateArray());
        Assert.Equal("id-1", e.GetProperty("id").GetString());
        Assert.Equal("fan_curve_applied", e.GetProperty("event").GetString());
        Assert.Equal("2026-03-04T03:06:07.0000000Z", e.GetProperty("ts").GetString());
        var props = e.GetProperty("props");
        Assert.Equal("quiet", props.GetProperty("preset").GetString());
        Assert.Equal(3, props.GetProperty("channels").GetInt32());
        Assert.Equal(2, props.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Profile_refresh_reaches_the_usage_sink_but_not_posthog()
    {
        var refresh = Ev(TelemetryEvents.ProfileRefresh, null, ("cpu", "Ryzen"), ("os_build", "Windows 11"), ("widget_count", 2));
        var usage = new UsageTelemetrySink(true);
        await Feed(usage, refresh);
        var e = Assert.Single(usage.Take(10));
        Assert.Equal("profile", e.Name);
        Assert.Equal(new[] { "os_build", "widget_count" }, e.Props.Select(p => p.Key));

        var handler = new CapturingHandler();
        var posthog = new PostHogSink(new SingleClientFactory(handler), new PostHogOptions { ProjectApiKey = "k" });
        await posthog.SendAsync("id", new[] { refresh }, CancellationToken.None);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task PostHog_sink_drops_app_started_but_sends_the_rest()
    {
        var handler = new CapturingHandler();
        var sink = new PostHogSink(new SingleClientFactory(handler), new PostHogOptions { ProjectApiKey = "k" });

        await sink.SendAsync("id", new[] { Ev(TelemetryEvents.AppStarted), Ev("fan_curve_applied") }, CancellationToken.None);
        await sink.SendAsync("id", new[] { Ev(TelemetryEvents.AppStarted) }, CancellationToken.None);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("fan_curve_applied", body);
        Assert.DoesNotContain("app_started", body);
    }

    private sealed class CapturingHandler : System.Net.Http.HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly System.Net.Http.HttpMessageHandler _handler;
        public SingleClientFactory(System.Net.Http.HttpMessageHandler handler) => _handler = handler;
        public System.Net.Http.HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
