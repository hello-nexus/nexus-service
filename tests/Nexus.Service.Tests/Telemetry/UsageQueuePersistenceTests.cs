using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public sealed class UsageQueuePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "usage-queue-" + Guid.NewGuid().ToString("N"));
    private string QueuePath => Path.Combine(_dir, UsageQueueFile.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TelemetryEvent Ev(string name, params (string, object?)[] props) => new()
    {
        Name = name,
        Timestamp = DateTimeOffset.UtcNow,
        Properties = props.Select(p => new KeyValuePair<string, object?>(p.Item1, p.Item2)).ToArray(),
    };

    private static async Task<UsageTelemetrySink> Filled(params TelemetryEvent[] events)
    {
        var sink = new UsageTelemetrySink(true);
        await sink.SendAsync("i", events, CancellationToken.None);
        return sink;
    }

    [Fact]
    public async Task Persist_then_restore_round_trips_ids_names_and_typed_props()
    {
        var sink = await Filled(Ev("a", ("s", "x"), ("n", 7), ("d", 1.5), ("b", true), ("l", new[] { "p", "q" })), Ev("b"));
        var original = sink.Take(10);
        sink.Requeue(original);

        sink.Persist(QueuePath, optedOut: false);
        var fresh = new UsageTelemetrySink(true);
        Assert.Equal(2, fresh.Restore(QueuePath));

        var back = fresh.Take(10);
        Assert.Equal(original.Select(e => e.Id), back.Select(e => e.Id));
        Assert.Equal(original.Select(e => e.Name), back.Select(e => e.Name));
        var props = back[0].Props.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("x", props["s"]);
        Assert.Equal(7L, Convert.ToInt64(props["n"]));
        Assert.Equal(1.5, props["d"]);
        Assert.Equal(true, props["b"]);
        Assert.Equal(new[] { "p", "q" }, (string[])props["l"]!);
    }

    [Fact]
    public async Task Restore_deletes_the_file_so_a_delivered_batch_is_not_resent_next_start()
    {
        var sink = await Filled(Ev("a"));
        sink.Persist(QueuePath, optedOut: false);
        new UsageTelemetrySink(true).Restore(QueuePath);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Persist_of_an_empty_queue_removes_a_stale_file()
    {
        var sink = await Filled(Ev("a"));
        sink.Persist(QueuePath, optedOut: false);
        sink.Take(10);
        sink.Persist(QueuePath, optedOut: false);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Opted_out_persist_keeps_only_a_pending_opt_out()
    {
        var sink = await Filled(Ev("a"), Ev(TelemetryEvents.OptOut));
        sink.Persist(QueuePath, optedOut: true);
        var back = new UsageTelemetrySink(true);
        back.Restore(QueuePath);
        Assert.Equal(new[] { TelemetryEvents.OptOut }, back.Take(10).Select(e => e.Name));
    }

    [Fact]
    public async Task Undrained_client_events_are_mirrored_into_the_snapshot()
    {
        var sink = await Filled(Ev("queued"));
        sink.Persist(QueuePath, optedOut: false, new[] { Ev("undrained") });
        var back = new UsageTelemetrySink(true);
        back.Restore(QueuePath);
        Assert.Equal(new[] { "queued", "undrained" }, back.Take(10).Select(e => e.Name));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"events\":[{\"id\":1}]}")]
    [InlineData("")]
    public void Corrupt_file_is_dropped_and_deleted(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(QueuePath, content);
        var sink = new UsageTelemetrySink(true);
        Assert.Equal(0, sink.Restore(QueuePath));
        Assert.False(File.Exists(QueuePath));
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Oversized_file_is_dropped_unread()
    {
        Directory.CreateDirectory(_dir);
        using (var f = File.Create(QueuePath))
            f.SetLength(UsageQueueFile.MaxFileBytes + 1);
        Assert.Equal(0, new UsageTelemetrySink(true).Restore(QueuePath));
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Restored_events_go_ahead_of_events_queued_meanwhile()
    {
        var old = await Filled(Ev("old"));
        old.Persist(QueuePath, optedOut: false);
        var sink = await Filled(Ev("new"));
        sink.Restore(QueuePath);
        Assert.Equal(new[] { "old", "new" }, sink.Take(10).Select(e => e.Name));
    }

    [Fact]
    public async Task Persisted_queue_is_bounded()
    {
        var sink = new UsageTelemetrySink(true);
        await sink.SendAsync("i", Enumerable.Range(0, UsageTelemetrySink.MaxQueued).Select(i => Ev("e" + i)).ToArray(), CancellationToken.None);
        sink.Persist(QueuePath, optedOut: false, Enumerable.Range(0, 50).Select(i => Ev("x" + i)).ToArray());
        var back = new UsageTelemetrySink(true);
        Assert.Equal(UsageTelemetrySink.MaxQueued, back.Restore(QueuePath));
    }

    [Fact]
    public async Task Worker_stop_persists_what_it_could_not_deliver_and_start_restores_it()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => { s.Telemetry.CollectAnonymousData = true; s.Telemetry.InstallId = "install-1"; });
        var sink = await Filled(Ev("stuck"));
        var offline = new OfflineTransport();
        var worker = new UsageTelemetryWorker(sink, offline, store, _ => { }, QueuePath);

        await worker.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(QueuePath));

        var next = new UsageTelemetrySink(true);
        var restarted = new UsageTelemetryWorker(next, offline, store, _ => { }, QueuePath);
        await restarted.StartAsync(CancellationToken.None);
        await restarted.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { "stuck" }, next.Take(10).Select(e => e.Name));
    }

    private sealed class OfflineTransport : IUsageTransport
    {
        public Task<UsageSendResult> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct)
            => Task.FromResult(new UsageSendResult(UsageDelivery.Retry, 0));
    }
}
