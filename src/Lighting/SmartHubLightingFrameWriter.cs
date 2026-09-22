using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Persistence;
using SmartHubColor = Nexus.Service.Peripherals.Hyte.SmartHub.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the Smart Hub's four ARGB ports. Same
/// pattern as <see cref="MiniHubLightingFrameWriter"/>: own 30 Hz timer so
/// disabled zones still receive blank frames; reads brightness / disabled /
/// identify from the shared settings store. One <c>WriteLighting</c> call per
/// ARGB port per tick.
/// </summary>
public sealed class SmartHubLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly SmartHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly SmartHubColor[]?[] _portBuffers = new SmartHubColor[SmartHubProtocol.ArgbPortCount][];

    private readonly FeatureGates _gates;

    public SmartHubLightingFrameWriter(LightingEngine engine, SmartHubHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
            { Console.Error.WriteLine($"[smarthub-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        var nowTicks = DateTime.UtcNow.Ticks;

        var hubId = _hub.DeviceId;
        var mirror = SmartHubLightingDeviceProvider.ReadMirror(settings, hubId);

        // A port's zones: one when nothing is chained to it, otherwise one per
        // declared product, in chain order. Mirrored, every physical port
        // streams the single mirror device's zones instead. Resolved ONCE per
        // tick and reused by both the uncontrolled check and the push loop -
        // at 30 Hz this walks the partition dictionaries, and doing it per
        // port per use was eight walks and eight allocations a tick.
        if (mirror)
        {
            // One device, streamed to every physical port.
            var shared = ResolveOrReuse(0, () => SmartHubLightingDeviceProvider.ResolveMirrorZones(settings, hubId, _hub));
            for (var i = 0; i < _portZones.Length; i++) _portZones[i] = shared;
        }
        else
        {
            for (var i = 0; i < _portZones.Length; i++)
            {
                var channel = i + 1;
                _portZones[i] = ResolveOrReuse(i, () => SmartHubLightingDeviceProvider.ResolvePortZones(
                    settings, hubId, channel, FirmwareLedCount(channel)));
            }
        }

        // Every port uncontrolled: leave the hub alone entirely so it drops back
        // to its firmware animation. A chained port counts as uncontrolled only
        // when every one of its products does.
        if (uncontrolled.Count > 0 && AllPortsUncontrolled(uncontrolled))
        {
            return;
        }

        // Push every port every tick - even ports with zero declared LEDs get
        // a zero-length frame, which the hub honours by keeping the strip dark
        // and stops it falling back to the firmware animation.
        for (var channel = 1; channel <= SmartHubProtocol.ArgbPortCount; channel++)
        {
            PushPort(devices, _portZones[channel - 1]!, channel, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        }
    }

    /// <summary>
    /// Last tick's zones per port, kept so a resolve that loses a race can
    /// reuse them. Resolution reads the partition dictionaries without the
    /// store lock while routes mutate them in place, so an insert landing
    /// mid-enumeration throws; the previous shape is a far better answer than
    /// a dropped frame, and the next tick picks up the new one.
    /// </summary>
    private readonly System.Collections.Generic.IReadOnlyList<ResolvedZone>?[] _portZones =
        new System.Collections.Generic.IReadOnlyList<ResolvedZone>?[SmartHubProtocol.ArgbPortCount];

    private System.Collections.Generic.IReadOnlyList<ResolvedZone> ResolveOrReuse(
        int index, Func<System.Collections.Generic.IReadOnlyList<ResolvedZone>> resolve)
    {
        try { return resolve(); }
        catch (InvalidOperationException)
        {
            return _portZones[index] ?? System.Array.Empty<ResolvedZone>();
        }
    }

    private int FirmwareLedCount(int channel)
    {
        foreach (var port in _hub.State.Ports)
        {
            if (port.Channel == channel) return port.LedCount;
        }
        return 0;
    }

    private bool AllPortsUncontrolled(System.Collections.Generic.IReadOnlyList<string> uncontrolled)
    {
        foreach (var zones in _portZones)
        {
            if (zones is null || !ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Fill one port's buffer from its zones, laid down back to back in chain
    /// order, and push it. Power, brightness, colour trim and identify are
    /// per-zone, so one product in a chain can be flashed or switched off
    /// without touching the ones beside it on the same wire.
    /// </summary>
    private void PushPort(DeviceFrame[] devices,
        System.Collections.Generic.IReadOnlyList<ResolvedZone> zones, int channel,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var total = 0;
        for (var i = 0; i < zones.Count; i++) total += Math.Max(0, zones[i].LedCount);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < Math.Max(total, 1))
            _portBuffers[idx] = new SmartHubColor[Math.Max(total, 64)];
        var dst = _portBuffers[idx]!;

        var offset = 0;
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            var ledCount = Math.Max(0, zone.LedCount);
            if (ledCount == 0) continue;

            DeviceFrame? frame = null;
            for (var f = 0; f < devices.Length; f++)
            { if (devices[f].Id == zone.Id) { frame = devices[f]; break; } }

            var brightnessMul = ComputeBrightnessMul(zone.Id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
            var hasIdentify = _identify.TryGetActive(zone.Id, nowTicks, out var startTicks);
            if (frame is null)
            {
                // No frame yet (a refresh in flight): dark, never stale bytes
                // from whatever occupied this slice last tick.
                for (var k = 0; k < ledCount && offset + k < dst.Length; k++) dst[offset + k] = default;
            }
            else
            {
                // A frame shorter than its zone (a refresh resized it) leaves
                // a tail, which would otherwise keep whatever occupied that
                // offset last tick.
                var fromFrame = Math.Min(ledCount, frame.LedCount);
                FillBufferSlice(dst, offset, frame.LedBytes, fromFrame,
                    brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
                for (var k = fromFrame; k < ledCount && offset + k < dst.Length; k++) dst[offset + k] = default;
            }
            offset += ledCount;
        }
        _hub.WriteLighting(channel, new ReadOnlySpan<SmartHubColor>(dst, 0, total));
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        if (uncontrolled.Count > 0)
        {
            foreach (var u in uncontrolled) if (u == id) return 0.0;
        }
        // One lookup feeds both the brightness and the colour trim.
        int devBrightness;
        try
        {
            if (prefs.TryGetValue(id, out var pref) && pref is not null)
            {
                devBrightness = pref.Brightness;
                adjust = DeviceColorAdjust.For(pref);
            }
            else
            {
                devBrightness = 100;
            }
        }
        catch (InvalidOperationException) { devBrightness = 100; }
        return Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);
    }

    private static void FillBufferSlice(SmartHubColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new SmartHubColor(255, 255, 255) : new SmartHubColor(0, 0, 0);
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = default;
            return;
        }
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[dstStart + i] = new SmartHubColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new SmartHubColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new SmartHubColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
