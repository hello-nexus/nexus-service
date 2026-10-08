using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Telemetry;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class FleetEventServiceTests
{
    private sealed class FakeFleetEventTransport : IFleetEventTransport
    {
        public List<FleetEventPayload> Sent { get; } = new();
        public Func<FleetEventPayload, bool> Respond { get; set; } = _ => true;

        public Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.FromResult(Respond(payload));
        }
    }

    private sealed class FakeTelemetrySink : ITelemetrySink
    {
        public bool Enabled { get; set; } = true;
        public List<TelemetryEvent> Received { get; } = new();

        public Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct)
        {
            Received.AddRange(batch);
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal ISensorProvider double; every non-configured member returns an empty placeholder.</summary>
    private sealed class StubSensors : ISensorProvider
    {
        public string Cpu { get; init; } = "";
        public IReadOnlyList<string> GpuModels { get; init; } = Array.Empty<string>();
        public string MemoryFormatted { get; init; } = "";
        public string Motherboard { get; init; } = "";

        public string GetCpuModel() => Cpu;
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => GpuModels;
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => MemoryFormatted;
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => Motherboard;
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static FleetEventService MakeService(
        IConfigStore store,
        FakeFleetEventTransport transport,
        ITelemetry? telemetry = null,
        IEnumerable<ITelemetrySink>? sinks = null,
        StubSensors? sensors = null,
        TimeProvider? clock = null,
        IUsbEnumerator? usb = null,
        Func<bool>? y70Connected = null,
        bool ibuypowerSystem = false) =>
        new(store, transport, telemetry ?? new TelemetryClient(store),
            sinks ?? Array.Empty<ITelemetrySink>(), new SystemSpecsCollector(sensors ?? new StubSensors()),
            usb ?? new StubUsbEnumerator(), _ => y70Connected?.Invoke() ?? false, () => ibuypowerSystem,
            clock ?? TimeProvider.System);

    private sealed class StubUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Entries { get; init; } = new();
        public List<UsbDeviceEntry> Enumerate() => Entries;
    }

    private static InMemoryConfigStore OptedInStore(bool installDelivered = true)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true;
            s.Telemetry.FleetInstallDelivered = installDelivered;
        });
        return store;
    }

    [Fact]
    public async Task Install_event_delivered_once_and_not_resent()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.True(store.Load().Telemetry.FleetInstallDelivered);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install); // not resent
    }

    [Fact]
    public async Task Install_event_stays_pending_until_transport_succeeds()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);
        var transport = new FakeFleetEventTransport { Respond = _ => false };
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.False(store.Load().Telemetry.FleetInstallDelivered);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install);

        transport.Respond = _ => true; // the server recovers
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.True(store.Load().Telemetry.FleetInstallDelivered);

        var attemptsAfterDelivered = transport.Sent.Count(p => p.Type == TelemetryEvents.Install);
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(attemptsAfterDelivered, transport.Sent.Count(p => p.Type == TelemetryEvents.Install));
    }

    [Fact]
    public async Task Specs_event_skipped_when_hardware_inventory_not_ready()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, sensors: new StubSensors());

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.DoesNotContain(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        Assert.Equal("", store.Load().Telemetry.FleetSpecsHash);
    }

    [Fact]
    public async Task Specs_event_sent_once_then_skipped_until_the_hash_changes()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var cpuA = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };

        var svc1 = MakeService(store, transport, sensors: cpuA);
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        var hash1 = store.Load().Telemetry.FleetSpecsHash;
        Assert.NotEqual("", hash1);

        // Same service, same snapshot again - unchanged hash, no resend.
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);

        // Next boot with different hardware - hash differs, resend.
        var cpuB = new StubSensors { Cpu = "CPU B", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc2 = MakeService(store, transport, sensors: cpuB);
        await svc2.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.NotEqual(hash1, store.Load().Telemetry.FleetSpecsHash);

        var specsPayload = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs;
        Assert.NotNull(specsPayload);
        Assert.Equal("CPU B", specsPayload!.Cpu);
        Assert.Equal(32L * 1024 * 1024 * 1024, specsPayload.RamBytes);
        Assert.Equal("Board A", specsPayload.Motherboard);
    }

    [Fact]
    public async Task Specs_delivery_that_fails_at_boot_retries_on_a_later_pass_then_stops()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport
        {
            Respond = p => p.Type != TelemetryEvents.Specs,
        };
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc = MakeService(store, transport, sensors: sensors);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        Assert.Equal("", store.Load().Telemetry.FleetSpecsHash);

        // Undelivered, so the hourly pass tries again even though boot is over.
        transport.Respond = _ => true;
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.NotEqual("", store.Load().Telemetry.FleetSpecsHash);

        // Delivered and unchanged, so later passes do not resend.
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
    }

    [Fact]
    public async Task Y70_detected_after_the_boot_pass_resends_specs_on_a_later_pass()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var y70 = false;
        var svc = MakeService(store, transport, sensors: sensors, y70Connected: () => y70);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.False(transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.Y70Seen);

        y70 = true;
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.True(transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs!.Y70Seen);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
    }

    [Fact]
    public async Task Y70_not_yet_detected_at_the_next_boot_does_not_resend_false()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };

        await MakeService(store, transport, sensors: sensors, y70Connected: () => true).RunPendingRetriesAsync(CancellationToken.None);
        Assert.True(transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.Y70Seen);

        // Next start: the panel is not detected yet on the boot pass.
        await MakeService(store, transport, sensors: sensors, y70Connected: () => false).RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
    }

    [Fact]
    public async Task Specs_event_carries_the_ibuypower_system_flag()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc = MakeService(store, transport, sensors: sensors, ibuypowerSystem: true);

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.True(transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.IbuypowerSystem);
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_opt_out_bypasses_capture_and_hits_the_sink_directly()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false; // already flipped by the route before this call
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var telemetry = new TelemetryClient(store); // _enabled=false: Capture() would no-op
        var sink = new FakeTelemetrySink();
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, telemetry, new ITelemetrySink[] { sink });

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.OptOut && p.InstallId == "install-1");
        Assert.Single(sink.Received, e => e.Name == TelemetryEvents.OptOut);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent); // cleared on success
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_opt_in_uses_the_normal_capture_pipeline()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true; // already flipped by the route before this call
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptIn;
        });
        var telemetry = new TelemetryClient(store);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, telemetry);

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptIn, CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.OptIn);
        Assert.Contains(telemetry.DrainBatch(10), e => e.Name == TelemetryEvents.OptIn);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_opt_out_does_not_resend_to_posthog_on_a_retry()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var sink = new FakeTelemetrySink();
        var transport = new FakeFleetEventTransport { Respond = _ => false }; // nexus-api unreachable
        var svc = MakeService(store, transport, sinks: new ITelemetrySink[] { sink });

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);
        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count); // nexus-api retried both times
        Assert.Single(sink.Received); // PostHog attempted only once for this pending type
        Assert.Equal(TelemetryEvents.OptOut, store.Load().Telemetry.FleetPendingConsentEvent); // still pending
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_does_not_clear_a_marker_a_newer_toggle_already_overwrote()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true;
            s.Telemetry.InstallId = "install-1";
            // A newer opt_in toggle already overwrote the marker while a stale opt_out attempt (below) was in flight.
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptIn;
        });
        var transport = new FakeFleetEventTransport { Respond = _ => true };
        var svc = MakeService(store, transport);

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        // The stale call delivered, but the marker no longer names "opt_out" - clearing it would drop the newer retry.
        Assert.Equal(TelemetryEvents.OptIn, store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task Opt_out_pending_event_expires_after_seven_days_and_goes_fully_silent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
            s.Telemetry.FleetPendingConsentSince = clock.GetUtcNow().ToString("o");
        });
        var transport = new FakeFleetEventTransport { Respond = _ => false }; // nexus-api never reachable
        var svc = MakeService(store, transport, clock: clock);

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Empty(transport.Sent); // gave up before attempting delivery at all
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentSince);
    }

    [Fact]
    public async Task Opt_out_delivery_success_inside_the_expiry_window_still_clears_normally()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
            s.Telemetry.FleetPendingConsentSince = clock.GetUtcNow().ToString("o");
        });
        var transport = new FakeFleetEventTransport { Respond = _ => true };
        var svc = MakeService(store, transport, clock: clock);

        clock.Advance(TimeSpan.FromDays(6)); // within the 7-day bound
        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Single(transport.Sent);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentSince);
    }

    [Fact]
    public async Task Opt_in_pending_event_never_expires()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptIn;
            s.Telemetry.FleetPendingConsentSince = clock.GetUtcNow().ToString("o");
        });
        var transport = new FakeFleetEventTransport { Respond = _ => false }; // never delivered
        var svc = MakeService(store, transport, clock: clock);

        clock.Advance(TimeSpan.FromDays(30)); // far past the opt_out-only bound
        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptIn, CancellationToken.None);

        Assert.Single(transport.Sent); // opt_in keeps retrying unbounded
        Assert.Equal(TelemetryEvents.OptIn, store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task FleetPendingConsentSince_survives_a_settings_reload_and_the_expiry_still_applies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-test-fleetexp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var armedAt = DateTimeOffset.UtcNow.AddDays(-8); // already past the bound
            using (var store = new JsonConfigStore(path))
            {
                store.Update(s =>
                {
                    s.Telemetry.CollectAnonymousData = false;
                    s.Telemetry.InstallId = "install-1";
                    s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
                    s.Telemetry.FleetPendingConsentSince = armedAt.ToString("o");
                });
                store.FlushNow();
            }

            // Reopen from disk - simulates a process restart.
            using var reopened = new JsonConfigStore(path);
            var transport = new FakeFleetEventTransport();
            var svc = MakeService(reopened, transport);

            await svc.RunPendingRetriesAsync(CancellationToken.None);

            Assert.Empty(transport.Sent);
            Assert.Equal("", reopened.Load().Telemetry.FleetPendingConsentEvent);
            Assert.Equal("", reopened.Load().Telemetry.FleetPendingConsentSince);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_with_no_install_id_clears_the_marker_without_sending()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Empty(transport.Sent);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task RunPendingRetriesAsync_retries_a_pending_opt_out_while_opted_out_but_nothing_else()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Single(transport.Sent); // only the opt_out retry - no install/specs traffic while opted out.
        Assert.Equal(TelemetryEvents.OptOut, transport.Sent[0].Type);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
        Assert.False(store.Load().Telemetry.FleetInstallDelivered);
    }
}
