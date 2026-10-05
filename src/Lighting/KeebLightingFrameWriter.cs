using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the keeb over raw HID. Mirrors
/// <see cref="Np50LightingFrameWriter"/>: a 30 Hz timer walks
/// <see cref="LightingEngine.Devices"/>, composes the device's zone frames
/// into the keys + underglow segment buffers (applying brightness / disabled
/// / identify per zone card against the shared settings store), and streams
/// each hardware segment via <see cref="KeebHub"/>. With the default
/// partition this is byte-identical to the legacy per-card writer.
/// </summary>
public sealed class KeebLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33; // 30 Hz, matches the engine + NP50 writer.
    // Knob follow cadence. The read now happens on KeebSettingsApplier's own
    // HID handle, so it no longer serializes against the LED stream the way the
    // hub's read did - the stall that showed as flicker on a held Static frame.
    // NEXUS_KEEB_KNOB_POLL_TICKS overrides it; 0 disables the follow entirely.
    private static readonly int KnobReadEveryTicks = ResolveKnobPollTicks();

    private static int ResolveKnobPollTicks()
    {
        var raw = Environment.GetEnvironmentVariable("NEXUS_KEEB_KNOB_POLL_TICKS");
        return int.TryParse(raw, out var ticks) && ticks >= 0 ? ticks : 6;
    }

    private readonly LightingEngine _engine;
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly KeebSettingsApplier _applier;
    private readonly Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay _keyReactive;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;
    private int _knobPollTicks;
    // No periodic re-pin: a settings write takes the same IO lock as the LED
    // writes, so re-asserting on a timer stalled a frame every couple of
    // seconds and WAS the residual flicker once the animation itself was
    // stopped (camera-measured). The pin holds because KeebSettingsApplier
    // refuses to put an animated mode back while a stream owns the device.

    // Writer-owned copies of the keeb's keyboard cards, painted by the key
    // reaction overlay while no engine effect runs (the engine owns the real ones).
    private readonly System.Collections.Generic.Dictionary<string, DeviceFrame> _standaloneFrames = new(StringComparer.Ordinal);

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    private readonly FeatureGates _gates;

    public KeebLightingFrameWriter(LightingEngine engine, KeebHub hub, IConfigStore store, Np50IdentifyTracker identify, KeebSettingsApplier applier, Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay keyReactive, FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _applier = applier;
        _keyReactive = keyReactive;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Nexus.Service.Platform.ServiceLog.Error($"[keeb-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected) return;

        var settings = _store.Load();
        var hubId = _hub.DeviceId;
        if (!string.IsNullOrEmpty(hubId) && Nexus.Service.Lighting.KeyReactive.KeebLegacyReactiveMigration.Pending(settings))
        {
            _store.Update(s => Nexus.Service.Lighting.KeyReactive.KeebLegacyReactiveMigration.Apply(s, hubId));
            settings = _store.Load();
        }
        var keyReactive = !string.IsNullOrEmpty(hubId) && _keyReactive.IsEnabled(hubId);
        var softwareEffect = _engine.CurrentEffectName != "none";

        // Firmware/software arbitration: stream only while a software effect is active
        // or key reactions are on. When neither applies, re-assert the persisted
        // firmware settings once so the onboard animation comes back.
        if (!softwareEffect && !keyReactive)
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                // Release first: Apply must write the user's own animation back.
                _applier.ReleaseFirmwareAnimation();
                _applier.Apply();
            }
            return;
        }

        if (!_wasStreaming)
        {
            _wasStreaming = true;
            _applier.ResetKnobBaseline();
            // Hold the onboard animation on its non-animated mode for the whole
            // stream, so a frame gap cannot repaint a rainbow over our output.
            _applier.SuppressFirmwareAnimation();
        }

        // Read the knob byte every Nth tick and set global = byte/100 on a change.
        var readByte = KnobReadEveryTicks > 0 && ++_knobPollTicks >= KnobReadEveryTicks;
        if (readByte) _knobPollTicks = 0;
        _applier.PollKnobToGlobal(readByte);

        // One key-map read per tick: KeyMap derives from State.Layout, which the
        // device thread rewrites on reconnect. Re-reading it per use would let a
        // mid-tick layout change leave the structure and the streamed span
        // disagreeing about the key count for that frame.
        var keys = _hub.KeyMap;

        // With an engine effect running, key reactions are already in the
        // engine's frames; without one, the overlay paints them over black.
        TickFrames(settings, keys, softwareEffect ? _engine.Devices : StandaloneFrames(hubId!));
    }

    private DeviceFrame[] StandaloneFrames(string hubId)
    {
        var list = new System.Collections.Generic.List<DeviceFrame>();
        foreach (var dev in _engine.Devices)
        {
            if (Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay.ConfigKey(dev) != hubId
                || !Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay.IsKeyboard(dev))
            {
                continue;
            }
            if (!_standaloneFrames.TryGetValue(dev.Id, out var copy) || copy.LedCount != dev.LedCount)
            {
                copy = new DeviceFrame(dev.Index, dev.Id, dev.LedCount);
                _standaloneFrames[dev.Id] = copy;
            }
            copy.Archetype = dev.Archetype;
            copy.DeviceId = dev.DeviceId;
            copy.LedU = dev.LedU;
            copy.LedV = dev.LedV;
            copy.LedKeys = dev.LedKeys;
            copy.LedDisabled = dev.LedDisabled;
            list.Add(copy);
        }
        var frames = list.ToArray();
        _keyReactive.PaintStandalone(frames, Environment.TickCount64);
        // Locked LEDs hold their colour here too, as they do over an engine effect.
        foreach (var frame in frames)
        {
            _engine.LedColorLocks?.PaintLocks(frame);
            frame.Publish();
        }
        return frames;
    }

    private void TickFrames(NexusSettings settings, KeebKeyMap keys, DeviceFrame[] devices)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        // The keeb software stream brightness is min(global, per-zone). The
        // firmware-brightness level (keeb Settings slider) dims the firmware animation,
        // not the software stream; the knob drives global brightness while streaming
        // (see SyncFromDevice), so it never multiplies into this stream (masterMul 1).
        var nowTicks = DateTime.UtcNow.Ticks;

        if (devices.Length == 0) return;

        var hubId = _hub.DeviceId;
        if (string.IsNullOrEmpty(hubId)) return;

        var structure = KeebZoneSupport.BuildStructure(hubId, keys);
        var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settings);
        Nexus.Service.Lighting.Zones.SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
        var touched = Nexus.Service.Lighting.Zones.SegmentFrameComposer.Compose(
            structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

        // Keys and underglow stream over separate HID reports, so each segment
        // can be handed back to firmware independently: a fully uncontrolled
        // segment simply isn't written this tick. Checked against the live
        // resolved zones (not the default card ids) so a custom partition's
        // zone ids still gate the write correctly.
        if (touched[KeebZoneSupport.KeysSegment]
            && !Nexus.Service.Lighting.Zones.ZoneResolution.IsSegmentFullyUncontrolled(zones, KeebZoneSupport.KeysSegment, uncontrolled))
        {
            _hub.WriteKeyboard(_segmentBuffers[KeebZoneSupport.KeysSegment]);
        }
        if (touched[KeebZoneSupport.UnderglowSegment]
            && !Nexus.Service.Lighting.Zones.ZoneResolution.IsSegmentFullyUncontrolled(zones, KeebZoneSupport.UnderglowSegment, uncontrolled))
        {
            _hub.WriteSurround(_segmentBuffers[KeebZoneSupport.UnderglowSegment]);
        }
    }
}
