using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Persistence;
using MiniHubColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the MiniHub. Same pattern as
/// <see cref="Np50LightingFrameWriter"/>: own 30 Hz timer so disabled
/// zones still receive blank frames when no effect is running; reads
/// brightness / disabled / identify from the shared settings store.
/// One <c>WriteLighting</c> call per LED channel per tick.
/// </summary>
public sealed class MiniHubLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly MiniHubColor[]?[] _portBuffers = new MiniHubColor[MiniHubProtocol.LedPortCount][];

    private readonly FeatureGates _gates;

    // Last resolved zones per chainable port, keyed by port id. Resolve reads
    // ZonePartitions/PortChains/ZoneLedCounts lock-free while routes mutate
    // them in place, so a mid-enumeration InvalidOperationException falls
    // back to last tick's zones rather than dropping the frame.
    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<ResolvedZone>> _zoneCache = new();

    public MiniHubLightingFrameWriter(LightingEngine engine, MiniHubHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
            { Console.Error.WriteLine($"[minihub-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
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
        var port3Zones = ResolveOrReuse($"{hubId}:port3",
            () => MiniHubLightingDeviceProvider.ResolveLedPortZones(settings, hubId, 3, _hub.State.Port3.LedCount));
        var port4Zones = ResolveOrReuse($"{hubId}:port4",
            () => MiniHubLightingDeviceProvider.ResolveLedPortZones(settings, hubId, 4, _hub.State.Port4.LedCount));

        // Every port uncontrolled: leave the whole hub alone so it drops back to
        // its firmware animation, same as never pushing at all. A chained port
        // (3/4) counts as uncontrolled only when every one of its products does.
        if (uncontrolled.Count > 0
            && uncontrolled.Contains($"{hubId}:port1") && uncontrolled.Contains($"{hubId}:port2")
            && ZoneResolution.IsFullyUncontrolled(port3Zones, uncontrolled)
            && ZoneResolution.IsFullyUncontrolled(port4Zones, uncontrolled))
        {
            return;
        }

        // Push every channel every tick - even channels with zero declared
        // LEDs get a padded blank frame, which the hub firmware honours by
        // blacking out the strip. The per-channel padded buffer comes from
        // MiniHubProtocol.BuildLightingStream, so each WriteLighting call
        // emits exactly 307 bytes (channel 4) or 157 bytes (channels 1-3)
        // regardless of how many LEDs the user has wired.
        TryPushZone(devices, $"{hubId}:port1", channel: 1, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        TryPushZone(devices, $"{hubId}:port2", channel: 2, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        PushPort(devices, port3Zones, channel: 3, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        PushPort(devices, port4Zones, channel: 4, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
    }

    private System.Collections.Generic.IReadOnlyList<ResolvedZone> ResolveOrReuse(
        string cacheKey, Func<System.Collections.Generic.IReadOnlyList<ResolvedZone>> resolve)
    {
        try
        {
            var zones = resolve();
            _zoneCache[cacheKey] = zones;
            return zones;
        }
        catch (InvalidOperationException)
        {
            return _zoneCache.TryGetValue(cacheKey, out var last) ? last : Array.Empty<ResolvedZone>();
        }
    }

    private void TryPushZone(DeviceFrame[] devices, string id, int channel,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }
        // Always push something - even a zero-LED frame goes out as a fully
        // zero-padded buffer, which BuildLightingStream then sends to the
        // hub. Skipping a channel means the hub eventually drops back to
        // its firmware animation on that strip. Cheap: BuildLightingStream
        // produces a fixed 157/307-byte frame regardless of declared count.
        var ledCount = frame is null ? 0 : frame.LedCount;
        var brightnessMul = ComputeBrightnessMul(id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < Math.Max(ledCount, 1))
            _portBuffers[idx] = new MiniHubColor[Math.Max(ledCount, 64)];
        var dst = _portBuffers[idx]!;
        if (ledCount > 0 && frame is not null)
        {
            FillBufferSlice(dst, 0, frame.LedBytes, ledCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
        }
        _hub.WriteLighting(channel, new ReadOnlySpan<MiniHubColor>(dst, 0, ledCount));
    }

    /// <summary>
    /// Fill a chainable port's buffer from its resolved zones, laid down back
    /// to back in chain order, and push it. Power, brightness, colour trim and
    /// identify are per-zone, so one product in a chain can be flashed or
    /// switched off without touching the ones beside it on the same wire.
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
            _portBuffers[idx] = new MiniHubColor[Math.Max(total, 64)];
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
                FillBufferSlice(dst, offset, frame.LedBytes, Math.Min(ledCount, frame.LedCount),
                    brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
            }
            offset += ledCount;
        }
        _hub.WriteLighting(channel, new ReadOnlySpan<MiniHubColor>(dst, 0, total));
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

    private static void FillBufferSlice(MiniHubColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new MiniHubColor(255, 255, 255) : new MiniHubColor(0, 0, 0);
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
                dst[dstStart + i] = new MiniHubColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new MiniHubColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new MiniHubColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
