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

    /// <summary>An older nexus-api: answers 400 to any specs body that carries the inventory keys.</summary>
    private sealed class OldApiTransport : IFleetEventTransport
    {
        public List<FleetEventPayload> Sent { get; } = new();

        /// <summary>Whether each specs body carried inventory keys when it was posted (the payload object is reused across the retry).</summary>
        public List<bool> SpecsHadInventory { get; } = new();
        public Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct) =>
            Task.FromResult(PostAsyncCore(payload) == FleetSendResult.Delivered);
        public Task<FleetSendResult> PostAsync(FleetEventPayload payload, CancellationToken ct) =>
            Task.FromResult(PostAsyncCore(payload));
        private FleetSendResult PostAsyncCore(FleetEventPayload payload)
        {
            Sent.Add(payload);
            var s = payload.Specs;
            var had = s is { RamModules: not null } or { StorageDrives: not null } or { RamModuleCount: not null } or { GpuCards: not null };
            if (payload.Type == TelemetryEvents.Specs)
                SpecsHadInventory.Add(had);
            return had ? FleetSendResult.Rejected : FleetSendResult.Delivered;
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
        public IReadOnlyList<GpuBoard> Boards { get; init; } = Array.Empty<GpuBoard>();
        public string MemoryFormatted { get; init; } = "";
        public string Motherboard { get; init; } = "";

        public string GetCpuModel() => Cpu;
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => GpuModels;
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<GpuBoard> GetGpuBoards() => Boards;
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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FleetEventService MakeService(
        IConfigStore store,
        FakeFleetEventTransport transport,
        ITelemetry? telemetry = null,
        IEnumerable<ITelemetrySink>? sinks = null,
        StubSensors? sensors = null,
        TimeProvider? clock = null,
        IUsbEnumerator? usb = null,
        bool ibuypowerSystem = false,
        TimeSpan? usbSettle = null,
        HardwareInventory? inventory = null) =>
        new(store, transport, telemetry ?? new TelemetryClient(store),
            sinks ?? Array.Empty<ITelemetrySink>(), new SystemSpecsCollector(sensors ?? new StubSensors()),
            usb ?? new StubUsbEnumerator(), () => ibuypowerSystem,
            clock ?? TimeProvider.System, usbSettle ?? TimeSpan.Zero,
            inventory is null ? null : () => inventory);

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

        var clock = new ManualClock();
        var svc1 = MakeService(store, transport, sensors: cpuA, clock: clock);
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        var hash1 = store.Load().Telemetry.FleetSpecsHash;
        Assert.NotEqual("", hash1);

        // Same service, same snapshot again - unchanged hash, no resend.
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);

        // Next boot with different hardware - hash differs, resend.
        var cpuB = new StubSensors { Cpu = "CPU B", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc2 = MakeService(store, transport, sensors: cpuB, clock: clock);
        await svc2.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.NotEqual(hash1, store.Load().Telemetry.FleetSpecsHash);

        var specsPayload = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs;
        Assert.NotNull(specsPayload);
        Assert.Equal("CPU B", specsPayload!.Cpu);
        Assert.Equal(32L * 1024 * 1024 * 1024, specsPayload.RamBytes);
        Assert.Equal("Board A", specsPayload.Motherboard);
    }

    private static StubSensors Sensors(string gpu = "GPU A", string mem = "32 GB") =>
        new() { Cpu = "CPU A", GpuModels = gpu.Length == 0 ? Array.Empty<string>() : gpu.Split('+'), MemoryFormatted = mem, Motherboard = "Board A" };

    [Fact]
    public async Task Specs_wait_for_the_usb_bus_to_settle_then_snapshot_once()
    {
        var store = OptedInStore(installDelivered: false);
        var transport = new FakeFleetEventTransport();
        var clock = new ManualClock();
        var usb = new StubUsbEnumerator();
        var svc = MakeService(store, transport, sensors: Sensors(), clock: clock, usb: usb, usbSettle: TimeSpan.FromMinutes(2));

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(0, SpecsSent(transport)); // too early: devices may still be enumerating
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install);

        usb.Entries.Add(new UsbDeviceEntry { VendorId = 0x045e, ProductId = 0x028e, Name = "Controller" });
        clock.Advance(TimeSpan.FromMinutes(3));
        await svc.RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Single(specs.Devices);
    }

    [Fact]
    public async Task Opt_out_reaches_the_usage_sink_through_the_sink_list()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var usage = new UsageTelemetrySink(true);
        var svc = MakeService(store, new FakeFleetEventTransport(), new TelemetryClient(store), new ITelemetrySink[] { usage });

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Equal(new[] { "opt_out" }, usage.Take(10).Select(e => e.Name));
    }

    [Fact]
    public async Task Hourly_passes_reuse_the_boot_usb_snapshot_so_hot_plugging_does_not_resend()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var usb = new StubUsbEnumerator();
        var svc = MakeService(store, transport, sensors: Sensors(), usb: usb);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);

        usb.Entries.Add(new UsbDeviceEntry { VendorId = 0x045e, ProductId = 0x028e, Name = "Controller" });
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        usb.Entries.Clear();
        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
    }

    [Fact]
    public async Task A_new_process_reads_a_fresh_usb_snapshot_at_its_boot_pass()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var usb = new StubUsbEnumerator();
        await MakeService(store, transport, sensors: Sensors(), usb: usb).RunPendingRetriesAsync(CancellationToken.None);

        usb.Entries.Add(new UsbDeviceEntry { VendorId = 0x045e, ProductId = 0x028e, Name = "Controller" });
        await MakeService(store, transport, sensors: Sensors(), usb: usb).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
    }

    [Fact]
    public async Task Y70_flip_resends_with_the_boot_snapshot_devices_not_a_live_scan()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var usb = new StubUsbEnumerator();
        var svc = MakeService(store, transport, sensors: Sensors(), usb: usb);
        await svc.RunPendingRetriesAsync(CancellationToken.None);

        usb.Entries.Add(new UsbDeviceEntry { VendorId = 0x045e, ProductId = 0x028e, Name = "Controller" });
        store.Update(s => s.Telemetry.FleetY70Seen = true);
        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        var last = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.True(last.Y70Seen);
        Assert.Empty(last.Devices);
    }

    [Fact]
    public async Task Gpu_order_does_not_change_the_hash()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors("GPU A+GPU B")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors("GPU B+GPU A")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
    }

    private static int SpecsSent(FakeFleetEventTransport t) => t.Sent.Count(p => p.Type == TelemetryEvents.Specs);

    [Fact]
    public async Task Transient_empty_gpu_read_reuses_the_persisted_list_without_a_resend()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors("GPU A+GPU B")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors(gpu: "")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(1, SpecsSent(transport));
        Assert.Equal("GPU A + GPU B", store.Load().Telemetry.FleetGpu);
    }

    [Fact]
    public async Task Install_that_never_reports_a_gpu_still_sends_an_empty_list()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors(gpu: "")).RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Empty(specs.Gpu);
    }

    [Fact]
    public async Task Real_gpu_change_sends_once_and_updates_the_persisted_list()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors("GPU A")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors("GPU B")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors("GPU B")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, SpecsSent(transport));
        Assert.Equal("GPU B", store.Load().Telemetry.FleetGpu);
    }

    [Fact]
    public async Task Zero_ram_with_nothing_persisted_sends_nothing_and_stores_nothing()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors(mem: "")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(0, SpecsSent(transport));
        Assert.Equal(0, store.Load().Telemetry.FleetRamBytes);
    }

    [Fact]
    public async Task Zero_ram_read_never_overwrites_the_persisted_total_and_does_not_resend()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors()).RunPendingRetriesAsync(CancellationToken.None);
        var persisted = store.Load().Telemetry.FleetRamBytes;
        Assert.True(persisted > 0);

        await MakeService(store, transport, sensors: Sensors(mem: "")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(1, SpecsSent(transport));
        Assert.Equal(persisted, store.Load().Telemetry.FleetRamBytes);
    }

    [Fact]
    public async Task Real_ram_change_sends_once()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: Sensors(mem: "32 GB")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors(mem: "64 GB")).RunPendingRetriesAsync(CancellationToken.None);
        await MakeService(store, transport, sensors: Sensors(mem: "64 GB")).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, SpecsSent(transport));
        Assert.Equal(64L * 1024 * 1024 * 1024, store.Load().Telemetry.FleetRamBytes);
    }

    [Theory]
    [InlineData("32 GB DDR5-6000 (2 × 16 GB)", true, 0, 32)]
    [InlineData("31.4 GB", true, 0, 31)]
    [InlineData("31.4 GB", true, 32, 32)]
    [InlineData("32 GB DDR5-6000 (2 × 16 GB)", true, 16, 32)]
    [InlineData("", true, 32, 32)]
    [InlineData("0 GB", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Ram_resolves_to_dimm_then_persisted_then_usable(string memory, bool requireDimm, int persistedGb, int expectedGb) =>
        Assert.Equal(
            (long)expectedGb * 1024 * 1024 * 1024,
            FleetEventService.ResolveRam(memory, requireDimm, (long)persistedGb * 1024 * 1024 * 1024));

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
    public async Task Y70_latched_mid_session_resends_specs_on_a_later_pass()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var clock = new ManualClock();
        var svc = MakeService(store, transport, sensors: sensors, clock: clock);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.False(transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.Y70Seen);

        store.Update(s => s.Telemetry.FleetY70Seen = true);
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.True(transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs!.Y70Seen);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
    }

    private static HardwareInventory SampleInventory(string part = "CMK32GX5M2B6000C36") => new(
        new[]
        {
            new FleetEventRamModule { Manufacturer = "Corsair", PartNumber = part, CapacityBytes = 17179869184, ConfiguredSpeedMts = 6000, RatedSpeedMts = 6000, Generation = "DDR5" },
            new FleetEventRamModule { Manufacturer = "Corsair", PartNumber = part, CapacityBytes = 17179869184, ConfiguredSpeedMts = 6000, RatedSpeedMts = 6000, Generation = "DDR5" },
        },
        new[] { new FleetEventDrive { Model = "Samsung SSD 990 PRO 2TB", CapacityBytes = 2000398934016, Bus = "nvme", Media = "ssd" } });

    [Fact]
    public async Task Specs_event_carries_ram_modules_and_physical_drives()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc = MakeService(store, transport, sensors: sensors, inventory: SampleInventory());

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Equal(2, specs.RamModuleCount);
        Assert.Equal("CMK32GX5M2B6000C36", specs.RamModules![0].PartNumber);
        Assert.Equal("Samsung SSD 990 PRO 2TB", specs.StorageDrives!.Single().Model);
        Assert.Equal(34359738368, specs.RamBytes);
    }

    private static StubSensors DefaultSensors() =>
        new() { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };

    [Fact]
    public async Task An_empty_inventory_read_reuses_the_last_good_one_instead_of_erasing_it()
    {
        var store = OptedInStore();
        var first = new FakeFleetEventTransport();
        await MakeService(store, first, sensors: DefaultSensors(), inventory: SampleInventory()).RunPendingRetriesAsync(CancellationToken.None);

        // New process, platform read fails, and an unrelated field changed so specs go out again.
        var second = new FakeFleetEventTransport();
        var changed = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU B" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        await MakeService(store, second, sensors: changed, inventory: HardwareInventory.Empty).RunPendingRetriesAsync(CancellationToken.None);

        var specs = second.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Equal(2, specs.RamModuleCount);
        Assert.Equal(2, specs.RamModules!.Count);
        Assert.Equal("Samsung SSD 990 PRO 2TB", specs.StorageDrives!.Single().Model);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(31, false)]
    public async Task The_remembered_inventory_stops_being_reused_after_the_expiry(int daysSinceLastGoodRead, bool expectReused)
    {
        var store = OptedInStore();
        var clock = new ManualClock();
        var stamp = clock.GetUtcNow() - TimeSpan.FromDays(daysSinceLastGoodRead);
        store.Update(s =>
        {
            s.Telemetry.FleetRamModules = SampleInventory().RamModules.ToList();
            s.Telemetry.FleetRamModuleTotal = 2;
            s.Telemetry.FleetDrives = SampleInventory().Drives.ToList();
            s.Telemetry.FleetRamReadAt = stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            s.Telemetry.FleetDrivesReadAt = stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        });
        var transport = new FakeFleetEventTransport();

        await MakeService(store, transport, sensors: DefaultSensors(), clock: clock, inventory: HardwareInventory.Empty)
            .RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Equal(expectReused, specs.RamModules is not null);
        Assert.Equal(expectReused, specs.StorageDrives is not null);
    }

    [Fact]
    public async Task Ram_and_drives_expire_independently()
    {
        var store = OptedInStore();
        var clock = new ManualClock();
        var fresh = (clock.GetUtcNow() - TimeSpan.FromDays(1)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var stale = (clock.GetUtcNow() - TimeSpan.FromDays(40)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        store.Update(s =>
        {
            s.Telemetry.FleetRamModules = SampleInventory().RamModules.ToList();
            s.Telemetry.FleetRamModuleTotal = 2;
            s.Telemetry.FleetDrives = SampleInventory().Drives.ToList();
            s.Telemetry.FleetRamReadAt = fresh;
            s.Telemetry.FleetDrivesReadAt = stale;
        });
        var transport = new FakeFleetEventTransport();

        await MakeService(store, transport, sensors: DefaultSensors(), clock: clock, inventory: HardwareInventory.Empty)
            .RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.NotNull(specs.RamModules);
        Assert.Null(specs.StorageDrives);
    }

    [Fact]
    public async Task A_good_read_refreshes_the_expiry_stamp()
    {
        var store = OptedInStore();
        var clock = new ManualClock();

        await MakeService(store, new FakeFleetEventTransport(), sensors: DefaultSensors(), clock: clock, inventory: SampleInventory())
            .RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(clock.GetUtcNow(), DateTimeOffset.Parse(store.Load().Telemetry.FleetRamReadAt, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(clock.GetUtcNow(), DateTimeOffset.Parse(store.Load().Telemetry.FleetDrivesReadAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_empty_read_with_nothing_remembered_is_not_hashed_as_a_change()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        await MakeService(store, transport, sensors: DefaultSensors(), inventory: HardwareInventory.Empty).RunPendingRetriesAsync(CancellationToken.None);
        var before = store.Load().Telemetry.FleetSpecsHash;

        await MakeService(store, transport, sensors: DefaultSensors()).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(before, store.Load().Telemetry.FleetSpecsHash);
        Assert.Equal(1, SpecsSent(transport));
    }

    [Fact]
    public async Task Persisted_inventory_survives_a_settings_round_trip()
    {
        var store = OptedInStore();
        await MakeService(store, new FakeFleetEventTransport(), sensors: DefaultSensors(), inventory: SampleInventory()).RunPendingRetriesAsync(CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(store.Load(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

        Assert.Equal(2, back.Telemetry.FleetRamModules.Count);
        Assert.Equal(2, back.Telemetry.FleetRamModuleTotal);
        Assert.Equal("nvme", back.Telemetry.FleetDrives.Single().Bus);
    }

    [Fact]
    public async Task Ram_module_count_is_the_populated_count_not_the_capped_list_length()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var capped = new HardwareInventory(SampleInventory().RamModules, SampleInventory().Drives, RamModuleTotal: 20);

        await MakeService(store, transport, sensors: DefaultSensors(), inventory: capped).RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Equal(20, specs.RamModuleCount);
        Assert.Equal(2, specs.RamModules!.Count);
    }

    [Fact]
    public async Task A_400_on_the_inventory_keys_resends_without_them_so_cpu_and_gpu_still_update()
    {
        var store = OptedInStore();
        var transport = new OldApiTransport();
        var direct = new FleetEventService(store, transport, new TelemetryClient(store), Array.Empty<ITelemetrySink>(),
            new SystemSpecsCollector(DefaultSensors()), new StubUsbEnumerator(), () => false, TimeProvider.System, TimeSpan.Zero,
            () => SampleInventory());

        await direct.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(new[] { true, false }, transport.SpecsHadInventory);
        var last = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Null(last.RamModules);
        Assert.Null(last.StorageDrives);
        Assert.Null(last.RamModuleCount);
        Assert.Equal("CPU A", last.Cpu);
        Assert.NotEqual("", store.Load().Telemetry.FleetSpecsHash);

        // Later passes in the same process do not retry the rejected keys or resend an unchanged summary.
        await direct.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
    }

    [Fact]
    public async Task Specs_event_omits_ram_and_storage_when_the_platform_reports_none()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc = MakeService(store, transport, sensors: sensors);

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Null(specs.RamModuleCount);
        Assert.Null(specs.RamModules);
        Assert.Null(specs.StorageDrives);
    }

    private static StubSensors BoardSensors(params GpuBoard[] boards) => new()
    {
        Cpu = "CPU A",
        GpuModels = new[] { "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 3090" },
        Boards = boards,
        MemoryFormatted = "32 GB",
        Motherboard = "Board A",
    };

    [Fact]
    public async Task Specs_event_keeps_the_bare_chip_in_gpu_and_adds_the_board_in_gpu_cards()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var sensors = BoardSensors(new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881));

        await MakeService(store, transport, sensors: sensors).RunPendingRetriesAsync(CancellationToken.None);

        var specs = transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Equal(new[] { "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 3090" }, specs.Gpu);
        var card = specs.GpuCards!.Single();
        Assert.Equal(("NVIDIA GeForce RTX 3090", "MSI", "1462", "3881"), (card.Chip, card.Maker, card.SubsystemVendorId, card.SubsystemDeviceId));
        Assert.Equal("AMD Radeon(TM) Graphics + MSI NVIDIA GeForce RTX 3090", await new SystemSpecsCollector(sensors).GetAsync(CancellationToken.None) is { } r ? r.GraphicsCard : "");
    }

    [Fact]
    public async Task Specs_event_omits_gpu_cards_when_no_board_could_be_read()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();

        await MakeService(store, transport, sensors: BoardSensors()).RunPendingRetriesAsync(CancellationToken.None);

        Assert.Null(transport.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.GpuCards);
    }

    [Fact]
    public async Task A_400_on_the_gpu_cards_resends_without_them_so_the_rest_still_updates()
    {
        var store = OptedInStore();
        var transport = new OldApiTransport();
        var direct = new FleetEventService(store, transport, new TelemetryClient(store), Array.Empty<ITelemetrySink>(),
            new SystemSpecsCollector(BoardSensors(new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881))), new StubUsbEnumerator(),
            () => false, TimeProvider.System, TimeSpan.Zero, () => HardwareInventory.Empty);

        await direct.RunPendingRetriesAsync(CancellationToken.None);

        var last = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs!;
        Assert.Null(last.GpuCards);
        Assert.Equal(2, last.Gpu.Length);
        Assert.NotEqual("", store.Load().Telemetry.FleetSpecsHash);
    }

    [Fact]
    public async Task A_read_with_no_gpus_reuses_the_remembered_boards_but_a_read_with_gpus_and_no_boards_does_not()
    {
        var store = OptedInStore();
        var board = new GpuBoard("NVIDIA GeForce RTX 3090", 0x3842, 0x3973);
        await MakeService(store, new FakeFleetEventTransport(), sensors: BoardSensors(board)).RunPendingRetriesAsync(CancellationToken.None);

        var noGpus = new FakeFleetEventTransport();
        var cold = new StubSensors { Cpu = "CPU B", MemoryFormatted = "32 GB", Motherboard = "Board A" };
        await MakeService(store, noGpus, sensors: cold).RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal("EVGA", noGpus.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.GpuCards!.Single().Maker);

        var swapped = new FakeFleetEventTransport();
        await MakeService(store, swapped, sensors: BoardSensors()).RunPendingRetriesAsync(CancellationToken.None);
        Assert.Null(swapped.Sent.Single(p => p.Type == TelemetryEvents.Specs).Specs!.GpuCards);
    }

    [Fact]
    public async Task Persisted_gpu_cards_survive_a_settings_round_trip()
    {
        var store = OptedInStore();
        await MakeService(store, new FakeFleetEventTransport(), sensors: BoardSensors(new GpuBoard("NVIDIA GeForce RTX 3090", 0x1462, 0x3881)))
            .RunPendingRetriesAsync(CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(store.Load(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

        var card = back.Telemetry.FleetGpuCards.Single();
        Assert.Equal(("MSI", "1462", "3881"), (card.Maker, card.SubsystemVendorId, card.SubsystemDeviceId));
    }

    [Fact]
    public void Specs_hash_is_unchanged_without_boards_and_changes_with_them_or_their_maker()
    {
        var gpu = new[] { "GPU A" };
        var baseline = FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board");
        var msi = new[] { new FleetEventGpuCard { Chip = "GPU A", Maker = "MSI", SubsystemVendorId = "1462", SubsystemDeviceId = "3881" } };
        var evga = new[] { new FleetEventGpuCard { Chip = "GPU A", Maker = "EVGA", SubsystemVendorId = "3842", SubsystemDeviceId = "3973" } };

        Assert.Equal(baseline, FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", gpuCards: Array.Empty<FleetEventGpuCard>()));
        Assert.NotEqual(baseline, FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", gpuCards: msi));
        Assert.NotEqual(
            FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", gpuCards: msi),
            FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", gpuCards: evga));
    }

    [Fact]
    public void Gpu_cards_are_capped_sorted_and_limited_to_boards_with_a_readable_vendor()
    {
        var read = Enumerable.Range(0, 12).Select(i => new GpuCard($"GPU {i:00}", "MSI", 0x1462, 0x3881 + i)).ToList();
        read.Add(new GpuCard("Integrated"));
        read.Reverse();

        var cards = FleetEventService.ResolveGpuCards(read, false, Array.Empty<FleetEventGpuCard>());

        Assert.Equal(8, cards.Count);
        Assert.Equal("GPU 00", cards[0].Chip);
        Assert.DoesNotContain(cards, c => c.Chip == "Integrated");
    }

    [Fact]
    public void Specs_hash_changes_when_a_module_or_drive_changes_and_ignores_order()
    {
        var gpu = new[] { "GPU A" };
        var baseline = FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board");
        var a = SampleInventory();
        var withInventory = FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", inventory: a);
        var otherPart = FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", inventory: SampleInventory("OTHER"));
        var reversed = new HardwareInventory(a.RamModules.Reverse().ToArray(), a.Drives);

        Assert.NotEqual(baseline, withInventory);
        Assert.NotEqual(withInventory, otherPart);
        Assert.Equal(withInventory, FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", inventory: reversed));
        Assert.Equal(baseline, FleetEventService.ComputeSpecsHash("CPU", gpu, 1, "Board", inventory: HardwareInventory.Empty));
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
