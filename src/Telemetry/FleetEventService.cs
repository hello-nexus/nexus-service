using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Store;

namespace Nexus.Service.Telemetry;

/// <summary>Builds/delivers fleet events to nexus-api (system of record, persisted retry) and PostHog (best-effort); <see cref="_gate"/> serializes the route and <see cref="FleetTelemetryWorker"/> call sites.</summary>
internal sealed class FleetEventService
{
    // Opted-out box: an undelivered opt_out this old is abandoned rather than retried forever.
    private static readonly TimeSpan OptOutRetryExpiry = TimeSpan.FromDays(7);

    // A box whose inventory read keeps failing stops reporting the remembered hardware after this long, rather than for ever.
    internal static readonly TimeSpan PersistedInventoryMaxAge = TimeSpan.FromDays(30);

    private readonly IConfigStore _store;
    private readonly IFleetEventTransport _transport;
    private readonly ITelemetry _telemetry;
    private readonly IReadOnlyList<ITelemetrySink> _sinks;
    private readonly SystemSpecsCollector _specs;
    private readonly IUsbEnumerator _usb;
    private readonly Func<bool> _ibuypowerSystem;
    private readonly Func<HardwareInventory> _inventorySource;
    private readonly TimeProvider _clock;
    // USB devices can enumerate late on a cold boot; specs snapshot the bus only after this delay.
    internal static readonly TimeSpan UsbSettleDelay = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Not persisted: USB set captured once per process after the bus settles; later passes reuse it so hot-plugging never changes the hash.
    private List<FleetEventDevice>? _bootDevices;

    // Not persisted: RAM module and drive identity read once per process, so a flaky re-read can never flip the hash.
    private HardwareInventory? _inventory;

    // Not persisted: set when nexus-api answered Bad Request to the inventory keys; later passes in this process skip them, a restart tries again.
    private bool _inventoryRejected;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _usbSettle;

    // Not persisted: caps the install event's PostHog leg to one attempt per process lifetime, independent of nexus-api retries.
    private bool _postHogInstallAttempted;

    // Not persisted: last consent-transition type PostHog was attempted for; a retry of the same type skips it, a new type gets its own attempt.
    private string? _postHogConsentAttemptedFor;

    public FleetEventService(
        IConfigStore store,
        IFleetEventTransport transport,
        ITelemetry telemetry,
        IEnumerable<ITelemetrySink> sinks,
        SystemSpecsCollector specs,
        IUsbEnumerator usb,
        HardwareAppCatalog hardware)
        : this(store, transport, telemetry, sinks, specs, usb,
            () => hardware.IsMatched(HardwareAppCatalog.IbuypowerAppId), TimeProvider.System,
            inventorySource: HardwareInventoryCollector.GetCached)
    {
    }

    internal FleetEventService(
        IConfigStore store,
        IFleetEventTransport transport,
        ITelemetry telemetry,
        IEnumerable<ITelemetrySink> sinks,
        SystemSpecsCollector specs,
        IUsbEnumerator usb,
        Func<bool> ibuypowerSystem,
        TimeProvider clock,
        TimeSpan? usbSettle = null,
        Func<HardwareInventory>? inventorySource = null)
    {
        _inventorySource = inventorySource ?? (() => HardwareInventory.Empty);
        _startedAt = clock.GetUtcNow();
        _usbSettle = usbSettle ?? UsbSettleDelay;
        _store = store;
        _transport = transport;
        _telemetry = telemetry;
        _sinks = sinks.Where(s => s.Enabled).ToArray();
        _specs = specs;
        _usb = usb;
        _ibuypowerSystem = ibuypowerSystem;
        _clock = clock;
    }

    /// <summary>One retry pass: consent event first regardless of consent state (the opt-out exception), then install and specs only while opted in.</summary>
    public async Task RunPendingRetriesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = _store.Load().Telemetry.FleetPendingConsentEvent;
            if (!string.IsNullOrEmpty(pending))
                await DeliverConsentTransitionCoreAsync(pending, ct).ConfigureAwait(false);

            if (!_store.Load().Telemetry.CollectAnonymousData)
                return;

            if (!_store.Load().Telemetry.FleetInstallDelivered)
                await DeliverInstallAsync(ct).ConfigureAwait(false);

            // Every pass, not just boot: SystemProfileService can latch FleetY70Seen mid-session.
            await MaybeDeliverSpecsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Delivers the opt_out/opt_in event; the caller already persisted the pending marker before flipping CollectAnonymousData.</summary>
    public async Task DeliverConsentTransitionAsync(string type, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DeliverConsentTransitionCoreAsync(type, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeliverConsentTransitionCoreAsync(string type, CancellationToken ct)
    {
        var installId = InstallIdentity.ResolveStored(_store);
        if (installId is null)
        {
            // No install id was ever minted (opted out before anything else ran) - nothing to attribute this to.
            ClearPendingConsentEvent();
            return;
        }

        // Bounded only for opt_out: an opted-in box still heartbeats, so opt_in retries unbounded.
        if (type == TelemetryEvents.OptOut && HasOptOutExpired())
        {
            Console.Error.WriteLine("[fleet-event] opt_out undelivered after 7 days, giving up");
            ClearPendingConsentEvent();
            return;
        }

        var payload = BuildEnvelope(type, installId);
        var delivered = await _transport.SendAsync(payload, ct).ConfigureAwait(false);

        if (_postHogConsentAttemptedFor != type)
        {
            _postHogConsentAttemptedFor = type;
            if (type == TelemetryEvents.OptOut)
            {
                // Both consent gates are closed here (flag already false) - send directly to each sink, bypassing Capture.
                var ev = new TelemetryEvent
                {
                    Name = TelemetryEvents.OptOut,
                    Timestamp = DateTimeOffset.UtcNow,
                    Properties = new[] { new KeyValuePair<string, object?>("version", payload.Version) },
                };
                foreach (var sink in _sinks)
                {
                    try { await sink.SendAsync(installId, new[] { ev }, ct).ConfigureAwait(false); }
                    catch (Exception ex) { Console.Error.WriteLine($"[fleet-event] opt_out sink failed: {ex.Message}"); }
                }
            }
            else
            {
                // opt_in: CollectAnonymousData is already true, so the normal Capture pipeline is open.
                _telemetry.Capture(type, ("version", payload.Version));
            }
        }

        if (delivered)
        {
            // Compare-and-clear: only drop the marker if it still names this delivered type - a newer toggle may have overwritten it mid-flight.
            _store.Update(s =>
            {
                if (s.Telemetry.FleetPendingConsentEvent == type)
                {
                    s.Telemetry.FleetPendingConsentEvent = "";
                    s.Telemetry.FleetPendingConsentSince = "";
                }
            });
        }
    }

    private bool HasOptOutExpired()
    {
        var since = _store.Load().Telemetry.FleetPendingConsentSince;
        return DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var armedAt)
            && _clock.GetUtcNow() - armedAt >= OptOutRetryExpiry;
    }

    private void ClearPendingConsentEvent() => _store.Update(s =>
    {
        s.Telemetry.FleetPendingConsentEvent = "";
        s.Telemetry.FleetPendingConsentSince = "";
    });

    private async Task DeliverInstallAsync(CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var payload = BuildEnvelope(TelemetryEvents.Install, installId);
        var delivered = await _transport.SendAsync(payload, ct).ConfigureAwait(false);

        if (!_postHogInstallAttempted)
        {
            _postHogInstallAttempted = true;
            _telemetry.Capture(TelemetryEvents.Install, ("version", payload.Version), ("os", payload.Os));
        }

        if (delivered)
            _store.Update(s => s.Telemetry.FleetInstallDelivered = true);
    }

    private async Task MaybeDeliverSpecsAsync(CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(specs.Processor) && string.IsNullOrWhiteSpace(specs.GraphicsCard))
            return; // not ready yet (cold boot) - retry next pass.

        // Devices enumerate late on a cold boot; wait for the bus before taking the one snapshot.
        if (_bootDevices is null && _clock.GetUtcNow() - _startedAt < _usbSettle)
            return;

        var gpu = specs.GraphicsCard.Split(
                " + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToArray();
        var persisted = _store.Load().Telemetry;
        var gpuList = ResolveGpu(gpu, persisted.FleetGpu);
        gpu = gpuList;
        var ramBytes = ResolveRam(specs.Memory, OperatingSystem.IsWindows(), persisted.FleetRamBytes);
        if (ramBytes == 0)
            return; // no RAM figure of any kind yet; never send or hash 0.
        var gpuJoined = string.Join(" + ", gpu);
        if (ramBytes != persisted.FleetRamBytes || gpuJoined != persisted.FleetGpu)
        {
            // Persist only DIMM-detail RAM and a non-empty GPU list.
            var keepGpu = gpu.Length > 0 ? gpuJoined : persisted.FleetGpu;
            var dimm = HasDimmDetail(specs.Memory, OperatingSystem.IsWindows());
            _store.Update(s =>
            {
                if (gpu.Length > 0) s.Telemetry.FleetGpu = keepGpu;
                if (dimm) s.Telemetry.FleetRamBytes = ramBytes;
            });
        }

        _bootDevices ??= FleetDeviceCollector.Collect(_usb.Enumerate());
        var devices = _bootDevices;
        var y70Seen = _store.Load().Telemetry.FleetY70Seen;
        var ibuypowerSystem = _ibuypowerSystem();
        // The platform read happens once per process; an empty one falls back to the last good inventory instead of erasing it server-side.
        if (_inventory is null)
        {
            var read = _inventorySource();
            var now = _clock.GetUtcNow();
            _inventory = ResolveInventory(read, persisted, now);
            PersistInventory(_inventory, persisted, read.RamModules.Count > 0 || read.Drives.Count > 0 ? now : null);
        }
        var inventory = _inventory;
        // After a Bad Request the specs go without the inventory keys, and the hash must not claim they were delivered.
        var hashedInventory = _inventoryRejected ? HardwareInventory.Empty : inventory;
        var hash = ComputeSpecsHash(specs.Processor, gpu, ramBytes, specs.Motherboard, devices, y70Seen, ibuypowerSystem, hashedInventory);
        if (hash == _store.Load().Telemetry.FleetSpecsHash)
            return; // unchanged since the last successful send.

        var payload = BuildEnvelope(TelemetryEvents.Specs, installId);
        payload.Specs = new FleetEventSpecs
        {
            Cpu = specs.Processor,
            Gpu = gpu,
            RamBytes = ramBytes,
            Motherboard = specs.Motherboard,
            Devices = devices,
            Y70Seen = y70Seen,
            IbuypowerSystem = ibuypowerSystem,
        };
        if (!_inventoryRejected)
            AttachInventory(payload.Specs, inventory);

        var result = await _transport.PostAsync(payload, ct).ConfigureAwait(false);
        if (result == FleetSendResult.Rejected && !_inventoryRejected && inventory.PopulatedModules + inventory.Drives.Count > 0)
        {
            // An older nexus-api refuses the unknown keys with Bad Request; resend once without them so cpu, gpu and devices still update.
            _inventoryRejected = true;
            AttachInventory(payload.Specs, HardwareInventory.Empty);
            hash = ComputeSpecsHash(specs.Processor, gpu, ramBytes, specs.Motherboard, devices, y70Seen, ibuypowerSystem, HardwareInventory.Empty);
            result = await _transport.PostAsync(payload, ct).ConfigureAwait(false);
        }
        if (result != FleetSendResult.Delivered)
            return;

        _store.Update(s => s.Telemetry.FleetSpecsHash = hash);
        _telemetry.Capture(TelemetryEvents.Specs,
            ("cpu", specs.Processor), ("gpu", gpu), ("ram_bytes", ramBytes), ("motherboard", specs.Motherboard));
    }

    private static void AttachInventory(FleetEventSpecs specs, HardwareInventory inventory)
    {
        specs.RamModuleCount = inventory.PopulatedModules > 0 ? inventory.PopulatedModules : null;
        specs.RamModules = inventory.RamModules.Count > 0 ? inventory.RamModules.ToList() : null;
        specs.StorageDrives = inventory.Drives.Count > 0 ? inventory.Drives.ToList() : null;
    }

    /// <summary>
    /// A non-empty read wins; an empty list reuses the last persisted one, list by list, until the persisted inventory is older than
    /// <see cref="PersistedInventoryMaxAge"/> since the last read that returned data.
    /// </summary>
    internal static HardwareInventory ResolveInventory(HardwareInventory current, TelemetrySettings persisted, DateTimeOffset now)
    {
        var usable = DateTimeOffset.TryParse(persisted.FleetInventoryReadAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var readAt)
            && now - readAt < PersistedInventoryMaxAge;
        var ram = current.RamModules.Count > 0 ? current.RamModules : usable ? persisted.FleetRamModules : current.RamModules;
        var total = current.RamModules.Count > 0 ? current.PopulatedModules : usable ? persisted.FleetRamModuleTotal : 0;
        var drives = current.Drives.Count > 0 ? current.Drives : usable ? persisted.FleetDrives : current.Drives;
        return new HardwareInventory(ram, drives, total);
    }

    private void PersistInventory(HardwareInventory inventory, TelemetrySettings persisted, DateTimeOffset? readAt)
    {
        var none = Array.Empty<FleetEventDrive>();
        var noModules = Array.Empty<FleetEventRamModule>();
        var ramChanged = inventory.RamModules.Count > 0
            && InventorySignature(new HardwareInventory(inventory.RamModules, none, inventory.PopulatedModules))
                != InventorySignature(new HardwareInventory(persisted.FleetRamModules, none, persisted.FleetRamModuleTotal));
        var drivesChanged = inventory.Drives.Count > 0
            && InventorySignature(new HardwareInventory(noModules, inventory.Drives)) != InventorySignature(new HardwareInventory(noModules, persisted.FleetDrives));
        if (!ramChanged && !drivesChanged && readAt is null)
            return;
        _store.Update(s =>
        {
            if (readAt is not null)
                s.Telemetry.FleetInventoryReadAt = readAt.Value.ToString("O", CultureInfo.InvariantCulture);
            if (ramChanged)
            {
                s.Telemetry.FleetRamModules = inventory.RamModules.ToList();
                s.Telemetry.FleetRamModuleTotal = inventory.PopulatedModules;
            }
            if (drivesChanged)
                s.Telemetry.FleetDrives = inventory.Drives.ToList();
        });
    }

    private static string InventorySignature(HardwareInventory inventory) =>
        string.Join('~', inventory.PopulatedModules,
            string.Join(';', inventory.RamModules.Select(m => string.Join('|', m.Manufacturer, m.ManufacturerId, m.PartNumber, m.CapacityBytes, m.ConfiguredSpeedMts, m.RatedSpeedMts, m.Generation)).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(';', inventory.Drives.Select(d => string.Join('|', d.Model, d.Vendor, d.CapacityBytes, d.Bus, d.Media)).OrderBy(x => x, StringComparer.Ordinal)));

    // On Windows the DIMM query appends a parenthesized stick layout; without it the string is the OS-usable total from the sensor fallback. Other platforms accept any figure.
    internal static bool HasDimmDetail(string memory, bool requireDimmDetail) =>
        !requireDimmDetail || memory.Contains('(');

    internal static long ParseRamBytes(string memory) =>
        SystemProfileService.ParseRamGb(memory) is int gb and > 0 ? (long)gb * 1024 * 1024 * 1024 : 0;

    /// <summary>
    /// The RAM figure to send. A DIMM total wins and is remembered; a read without DIMM detail reuses the remembered
    /// total so it cannot alternate with the OS-usable figure; with nothing remembered the usable figure is used as-is.
    /// 0 means no figure at all.
    /// </summary>
    internal static long ResolveRam(string memory, bool requireDimmDetail, long persistedDimmBytes)
    {
        var current = ParseRamBytes(memory);
        if (current > 0 && HasDimmDetail(memory, requireDimmDetail))
            return current;
        return persistedDimmBytes > 0 ? persistedDimmBytes : current;
    }

    /// <summary>A non-empty read wins; an empty one reuses the remembered list, else stays empty.</summary>
    internal static string[] ResolveGpu(string[] current, string persisted) =>
        current.Length > 0
            ? current
            : persisted.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FleetEventPayload BuildEnvelope(string type, string installId) => new()
    {
        InstallId = installId,
        Type = type,
        DeviceType = "desktop",
        Version = BuildInfo.Version,
        Os = TelemetryPlatform.OsTag(),
        OsVersion = RuntimeInformation.OSDescription,
        Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
    };

    // Unit-separator delimiter stops a field containing it from colliding two summaries into one hash.
    internal static string ComputeSpecsHash(string cpu, IReadOnlyList<string> gpu, long ramBytes, string motherboard,
        IReadOnlyList<FleetEventDevice>? devices = null, bool y70Seen = false, bool ibuypowerSystem = false,
        HardwareInventory? inventory = null)
    {
        var deviceIds = devices is null
            ? ""
            : string.Join(',', devices.Select(d => $"{d.Vid:x4}:{d.Pid:x4}"));
        var input = string.Join((char)0x1F, cpu, string.Join((char)0x1F, gpu), ramBytes.ToString(), motherboard, deviceIds,
            y70Seen ? "y70" : "", ibuypowerSystem ? "ibp" : "");
        // Appended only when present so an install without inventory keeps the hash it already delivered and is not re-sent.
        if (inventory is not null && (inventory.PopulatedModules > 0 || inventory.Drives.Count > 0))
        {
            var ram = inventory.RamModules.Select(m => string.Join('|', m.Manufacturer, m.ManufacturerId, m.PartNumber,
                m.CapacityBytes, m.ConfiguredSpeedMts, m.RatedSpeedMts, m.Generation)).OrderBy(x => x, StringComparer.Ordinal);
            var drives = inventory.Drives.Select(d => string.Join('|', d.Model, d.Vendor, d.CapacityBytes, d.Bus, d.Media))
                .OrderBy(x => x, StringComparer.Ordinal);
            input = string.Join((char)0x1F, input, "ram:" + inventory.PopulatedModules + string.Join(';', ram), "drv:" + string.Join(';', drives));
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
