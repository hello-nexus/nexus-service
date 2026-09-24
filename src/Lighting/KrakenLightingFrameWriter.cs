using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes engine output to the Kraken's RGB channels, one per-LED write per channel per
/// tick. Same shape as <see cref="MiniHubLightingFrameWriter"/>.
///
/// One HID write per channel per tick on the Elite V2, three on the older
/// generations (two colour tables plus the latch). Unchanged frames are skipped so a static
/// look costs nothing, which matters because the cooler shares this pipe with telemetry
/// polling and any LCD upload.
/// </summary>
public sealed class KrakenLightingFrameWriter : IHostedService, IDisposable
{
    // Matches LightingEngine's own frame interval, as the other writers do.
    private const int TickPeriodMs = 33;

    // A push is one HID write, and its result only says the bytes left the host - the
    // firmware NAKs either way, so a frame the cooler drops is indistinguishable from one
    // it applied. Re-send an unchanged frame this often so a drop self-heals instead of
    // being deduped away until the look next changes.
    private const int ReassertPeriodMs = 1000;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly KrakenHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Last bytes pushed per zone, so an unchanged frame is not re-sent.
    private readonly Dictionary<string, byte[]> _lastPushed = new();
    private readonly Dictionary<string, long> _lastPushedAtMs = new();
    private readonly HashSet<string> _missingFrame = new();

    // Last resolved zones per channel, keyed by channel id. Resolve reads
    // ZonePartitions/PortChains/ZoneLedCounts lock-free while routes mutate
    // them in place, so a mid-enumeration InvalidOperationException falls
    // back to last tick's zones rather than dropping the frame.
    private readonly Dictionary<string, IReadOnlyList<ResolvedZone>> _zoneCache = new();

    public KrakenLightingFrameWriter(
        LightingEngine engine, KrakenHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
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
                Console.Error.WriteLine($"[nzxt-kraken-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            // Drop the de-dupe cache: after a re-attach the cooler is showing whatever its
            // firmware kept, so an unchanged payload still has to be written once.
            _lastPushed.Clear();
            _lastPushedAtMs.Clear();
            return;
        }
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        var nowTicks = DateTime.UtcNow.Ticks;

        var channels = _hub.Snapshot.Channels;
        for (int i = 0; i < channels.Count; i++)
        {
            var channelId = KrakenHub.ZoneIdForChannelIndex(i);
            var zones = ResolveOrReuse(channelId,
                () => KrakenLightingDeviceProvider.ResolveChannelZones(settings, _hub.ModelName, channels[i], i, _hub.MaxDirectColors));

            // Left uncontrolled means "hands off": stop pushing so the cooler keeps running
            // whatever firmware animation it was set to. A chained channel counts as
            // uncontrolled only when every one of its products does.
            if (uncontrolled.Count > 0 && ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                _lastPushed.Remove(channelId);
                continue;
            }

            PushChannel(devices, channelId, zones, channels[i], disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        }
    }

    private IReadOnlyList<ResolvedZone> ResolveOrReuse(string cacheKey, Func<IReadOnlyList<ResolvedZone>> resolve)
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

    /// <summary>
    /// Fill one channel's payload from its resolved zones, laid down back to
    /// back in chain order, and push it in one HID write. Power, brightness,
    /// colour trim and identify stay per-zone so one product in a fan chain
    /// can be flashed or switched off without touching the ones beside it on
    /// the wire.
    /// </summary>
    private void PushChannel(
        DeviceFrame[] devices, string channelId, IReadOnlyList<ResolvedZone> zones, KrakenLightingChannel channel,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var total = 0;
        for (var i = 0; i < zones.Count; i++) total += Math.Max(0, zones[i].LedCount);
        var ledCount = Math.Min(total, _hub.MaxDirectColors);
        if (ledCount <= 0)
        {
            return;
        }

        var payload = new byte[ledCount * 3];
        var offset = 0;
        foreach (var zone in zones)
        {
            var zoneLeds = Math.Max(0, zone.LedCount);
            var take = Math.Min(zoneLeds, Math.Max(0, ledCount - offset));
            if (take > 0)
            {
                DeviceFrame? frame = null;
                for (var f = 0; f < devices.Length; f++)
                {
                    if (devices[f].Id == zone.Id) { frame = devices[f]; break; }
                }
                if (frame is null)
                {
                    // No engine frame yet (a refresh in flight): dark, never stale
                    // bytes from whatever occupied this slice last tick. Logged
                    // on the transition only.
                    if (_missingFrame.Add(zone.Id))
                    {
                        ServiceLog.Warn($"[nzxt-kraken-lighting-writer] no engine frame for {zone.Id}");
                    }
                }
                else
                {
                    _missingFrame.Remove(zone.Id);
                    var brightnessMul = ComputeBrightnessMul(zone.Id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
                    var hasIdentify = _identify.TryGetActive(zone.Id, nowTicks, out var startTicks);
                    FillSlice(payload, offset, take, frame, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
                }
            }
            offset += zoneLeds;
        }

        var nowMs = nowTicks / TimeSpan.TicksPerMillisecond;
        if (_lastPushed.TryGetValue(channelId, out var previous)
            && previous.Length == payload.Length
            && previous.AsSpan().SequenceEqual(payload)
            && _lastPushedAtMs.TryGetValue(channelId, out var sentAt)
            && nowMs - sentAt < ReassertPeriodMs)
        {
            return;
        }

        if (_hub.SetDirectColors(channel.ChannelId, payload))
        {
            _lastPushed[channelId] = payload;
            _lastPushedAtMs[channelId] = nowMs;
        }
        else
        {
            // Force a re-push next tick rather than leaving a stale "already sent" entry.
            _lastPushed.Remove(channelId);
        }
    }

    private static void FillSlice(byte[] dst, int dstStart, int ledCount, DeviceFrame frame,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        var baseOff = dstStart * 3;
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            byte v = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0 ? (byte)255 : (byte)0;
            for (var i = 0; i < ledCount * 3 && baseOff + i < dst.Length; i++) dst[baseOff + i] = v;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            return;
        }
        var src = frame.LedBytes;
        // Branch once, not once per LED: an untuned zone runs the original
        // loop with no colour-tuning work in it at all.
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[baseOff + off] = ar;
                dst[baseOff + off + 1] = ag;
                dst[baseOff + off + 2] = ab;
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
                dst[baseOff + off] = src[off];
                dst[baseOff + off + 1] = src[off + 1];
                dst[baseOff + off + 2] = src[off + 2];
            }
            return;
        }
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
            dst[baseOff + off] = (byte)(src[off] * brightnessMul);
            dst[baseOff + off + 1] = (byte)(src[off + 1] * brightnessMul);
            dst[baseOff + off + 2] = (byte)(src[off + 2] * brightnessMul);
        }
    }

    private static double ComputeBrightnessMul(
        string id,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == id) return 0.0;
        }
        for (var i = 0; i < uncontrolled.Count; i++)
        {
            if (uncontrolled[i] == id) return 0.0;
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
}
