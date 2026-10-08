using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Store;

namespace Nexus.Service.Telemetry;

/// <summary>Builds/delivers fleet events to nexus-api (system of record, persisted retry) and PostHog (best-effort); <see cref="_gate"/> serializes the route and <see cref="FleetTelemetryWorker"/> call sites.</summary>
internal sealed class FleetEventService
{
    // Opted-out box: an undelivered opt_out this old is abandoned rather than retried forever.
    private static readonly TimeSpan OptOutRetryExpiry = TimeSpan.FromDays(7);

    private readonly IConfigStore _store;
    private readonly IFleetEventTransport _transport;
    private readonly ITelemetry _telemetry;
    private readonly IReadOnlyList<ITelemetrySink> _sinks;
    private readonly SystemSpecsCollector _specs;
    private readonly IUsbEnumerator _usb;
    private readonly Func<IReadOnlyList<UsbDeviceEntry>, bool> _y70Connected;
    private readonly Func<bool> _ibuypowerSystem;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        IEnumerable<IDeviceHandler> handlers,
        HardwareAppCatalog hardware)
        : this(store, transport, telemetry, sinks, specs, usb,
            Y70Probe(handlers), () => hardware.IsMatched(HardwareAppCatalog.IbuypowerAppId), TimeProvider.System)
    {
    }

    internal FleetEventService(
        IConfigStore store,
        IFleetEventTransport transport,
        ITelemetry telemetry,
        IEnumerable<ITelemetrySink> sinks,
        SystemSpecsCollector specs,
        IUsbEnumerator usb,
        Func<IReadOnlyList<UsbDeviceEntry>, bool> y70Connected,
        Func<bool> ibuypowerSystem,
        TimeProvider clock)
    {
        _store = store;
        _transport = transport;
        _telemetry = telemetry;
        _sinks = sinks.Where(s => s.Enabled).ToArray();
        _specs = specs;
        _usb = usb;
        _y70Connected = y70Connected;
        _ibuypowerSystem = ibuypowerSystem;
        _clock = clock;
    }

    private static Func<IReadOnlyList<UsbDeviceEntry>, bool> Y70Probe(IEnumerable<IDeviceHandler> handlers)
    {
        var y70 = handlers.OfType<Y70Handler>().FirstOrDefault();
        return usb => y70?.IsConnected(usb) ?? false;
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

            // Every pass, not just boot: the Y70's serial and EDID detection can land after the boot pass.
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

        var gpu = specs.GraphicsCard.Split(
            " + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ramBytes = SystemProfileService.ParseRamGb(specs.Memory) is int gb ? (long)gb * 1024 * 1024 * 1024 : 0;

        var usb = _usb.Enumerate();
        var devices = FleetDeviceCollector.Collect(usb);
        if (!_store.Load().Telemetry.FleetY70Seen && _y70Connected(usb))
            _store.Update(s => s.Telemetry.FleetY70Seen = true);
        var y70Seen = _store.Load().Telemetry.FleetY70Seen;
        var ibuypowerSystem = _ibuypowerSystem();
        var hash = ComputeSpecsHash(specs.Processor, gpu, ramBytes, specs.Motherboard, devices, y70Seen, ibuypowerSystem);
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

        if (!await _transport.SendAsync(payload, ct).ConfigureAwait(false))
            return;

        _store.Update(s => s.Telemetry.FleetSpecsHash = hash);
        _telemetry.Capture(TelemetryEvents.Specs,
            ("cpu", specs.Processor), ("gpu", gpu), ("ram_bytes", ramBytes), ("motherboard", specs.Motherboard));
    }

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
        IReadOnlyList<FleetEventDevice>? devices = null, bool y70Seen = false, bool ibuypowerSystem = false)
    {
        var deviceIds = devices is null
            ? ""
            : string.Join(',', devices.Select(d => $"{d.Vid:x4}:{d.Pid:x4}"));
        var input = string.Join((char)0x1F, cpu, string.Join((char)0x1F, gpu), ramBytes.ToString(), motherboard, deviceIds,
            y70Seen ? "y70" : "", ibuypowerSystem ? "ibp" : "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
