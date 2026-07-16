using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
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
    private const int KnobReadEveryTicks = 2; // ~66 ms knob byte read; ease runs every tick.

    private readonly LightingEngine _engine;
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly KeebSettingsApplier _applier;
    private readonly KeebReactiveRenderer _renderer;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;
    private int _knobPollTicks;

    // Reused per-tick scratch buffer for reactive-only frames (base = black).
    private readonly RgbColor[] _reactiveKeyBuf = new RgbColor[KeebLayout.KeyLedCount];

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    public KeebLightingFrameWriter(LightingEngine engine, KeebHub hub, IConfigStore store, Np50IdentifyTracker identify, KeebSettingsApplier applier, KeebReactiveRenderer renderer)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _applier = applier;
        _renderer = renderer;
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
        if (!_hub.IsConnected) return;

        var settings = _store.Load();
        var keeb = settings.Keeb.FirmwareLighting;
        var keyReactive = keeb.KeyReactive;
        var softwareEffect = _engine.CurrentEffectName != "none";

        // Firmware/software arbitration: stream only while a software effect is active
        // or key-reactive is enabled. When neither applies, re-assert the persisted
        // firmware settings once so the onboard animation comes back.
        if (!softwareEffect && !keyReactive)
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _applier.Apply();
            }
            return;
        }

        if (!_wasStreaming)
        {
            _wasStreaming = true;
            _applier.ResetKnobBaseline();
        }

        // Read the knob byte every Nth tick and set global = byte/100 on a change.
        var readByte = ++_knobPollTicks >= KnobReadEveryTicks;
        if (readByte) _knobPollTicks = 0;
        _applier.PollKnobToGlobal(readByte);

        // Configure renderer from settings snapshot before calling Render().
        var reactiveColor = new RgbColor(keeb.KeyReactiveColor.R, keeb.KeyReactiveColor.G, keeb.KeyReactiveColor.B);
        _renderer.Configure(keyReactive, keeb.KeyReactiveMode, reactiveColor);
        var reactive = keyReactive ? _renderer.Render() : null;

        if (softwareEffect)
        {
            TickSoftwareEffect(settings, reactive, keeb.KeyReactiveMask);
        }
        else
        {
            // Key-reactive only (no software effect): stream base=black + reactive overlay, keys segment only.
            TickReactiveOnly(reactive);
        }
    }

    private void TickSoftwareEffect(NexusSettings settings, RgbColor?[]? reactive, bool mask)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        // The keeb software stream brightness is min(global, per-zone). The
        // firmware-brightness level (keeb Settings slider) dims the firmware animation,
        // not the software stream; the knob drives global brightness while streaming
        // (see SyncFromDevice), so it never multiplies into this stream (masterMul 1).
        var nowTicks = DateTime.UtcNow.Ticks;

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var hubId = _hub.DeviceId;
        if (string.IsNullOrEmpty(hubId)) return;

        var structure = KeebZoneSupport.BuildStructure(hubId);
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
            if (reactive != null)
            {
                ApplyReactive(_segmentBuffers[KeebZoneSupport.KeysSegment], reactive, mask);
            }
            _hub.WriteKeyboard(_segmentBuffers[KeebZoneSupport.KeysSegment]);
        }
        if (touched[KeebZoneSupport.UnderglowSegment]
            && !Nexus.Service.Lighting.Zones.ZoneResolution.IsSegmentFullyUncontrolled(zones, KeebZoneSupport.UnderglowSegment, uncontrolled))
        {
            _hub.WriteSurround(_segmentBuffers[KeebZoneSupport.UnderglowSegment]);
        }
    }

    private void TickReactiveOnly(RgbColor?[]? reactive)
    {
        if (reactive == null) return;

        // No software effect = no base to reveal, so mask is a no-op here: always paint the
        // reactive color on black (matches HYTE's reactive-only path, which ignores mask).
        // Mask only means something with a software effect as the base (TickSoftwareEffect).
        Array.Clear(_reactiveKeyBuf, 0, _reactiveKeyBuf.Length);
        ApplyReactive(_reactiveKeyBuf, reactive, mask: false);
        _hub.WriteKeyboard(_reactiveKeyBuf);
    }

    private static void ApplyReactive(RgbColor[] keyBuf, RgbColor?[] reactive, bool mask)
    {
        for (var i = 0; i < keyBuf.Length && i < reactive.Length; i++)
        {
            if (reactive[i] is { } overlayColor)
            {
                if (!mask)
                {
                    // Non-mask: reacting key shows reactive color.
                    keyBuf[i] = overlayColor;
                }
                // Mask: reacting key keeps base color (keyBuf[i] unchanged).
            }
            else
            {
                if (mask)
                {
                    // Mask: non-reacting key goes black.
                    keyBuf[i] = default;
                }
                // Non-mask: non-reacting key keeps base color (keyBuf[i] unchanged).
            }
        }
    }
}
