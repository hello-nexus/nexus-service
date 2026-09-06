using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
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
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var channels = _hub.Snapshot.Channels;
        for (int i = 0; i < channels.Count; i++)
        {
            var zoneId = KrakenHub.ZoneIdForChannelIndex(i);

            // Left uncontrolled means "hands off": stop pushing so the cooler keeps running
            // whatever firmware animation it was set to.
            bool isUncontrolled = false;
            for (var u = 0; u < uncontrolled.Count; u++)
            {
                if (uncontrolled[u] == zoneId) { isUncontrolled = true; break; }
            }
            if (isUncontrolled)
            {
                _lastPushed.Remove(zoneId);
                continue;
            }

            PushZone(devices, zoneId, channels[i], disabled, prefs, globalBrightness, nowTicks);
        }
    }

    private void PushZone(
        DeviceFrame[] devices, string zoneId, KrakenLightingChannel channel,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        {
            if (devices[i].Id == zoneId) { frame = devices[i]; break; }
        }
        if (frame is null)
        {
            // Without an engine frame this zone is unreachable - no canvas colour and no
            // identify flash. Logged on the transition only; it means the bridge has not
            // picked up this contributor.
            if (_missingFrame.Add(zoneId))
            {
                ServiceLog.Warn($"[nzxt-kraken-lighting-writer] no engine frame for {zoneId}");
            }
            return;
        }
        _missingFrame.Remove(zoneId);

        var ledCount = Math.Min(frame.LedCount, _hub.MaxDirectColors);
        if (ledCount <= 0)
        {
            return;
        }

        var brightnessMul = ComputeBrightnessMul(zoneId, disabled, prefs, globalBrightness, out var adjust);
        var hasIdentify = _identify.TryGetActive(zoneId, nowTicks, out var startTicks);

        var payload = new byte[ledCount * 3];
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - startTicks) / TimeSpan.TicksPerMillisecond;
            byte v = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0 ? (byte)255 : (byte)0;
            payload.AsSpan().Fill(v);
        }
        else if (brightnessMul > 0.0)
        {
            var src = frame.LedBytes;
            // Branch once, not once per LED: an untuned zone runs the original
            // loop with no colour-tuning work in it at all.
            if (!adjust.IsIdentity)
            {
                for (var i = 0; i < ledCount; i++)
                {
                    var off = i * 3;
                    if (off + 2 >= src.Length) break;
                    adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                        out var ar, out var ag, out var ab);
                    payload[off] = ar;
                    payload[off + 1] = ag;
                    payload[off + 2] = ab;
                }
            }
            else if (brightnessMul >= 0.999)
            {
                for (var i = 0; i < ledCount; i++)
                {
                    var off = i * 3;
                    if (off + 2 >= src.Length) break;
                    payload[off] = src[off];
                    payload[off + 1] = src[off + 1];
                    payload[off + 2] = src[off + 2];
                }
            }
            else
            {
                for (var i = 0; i < ledCount; i++)
                {
                    var off = i * 3;
                    if (off + 2 >= src.Length) break;
                    payload[off] = (byte)(src[off] * brightnessMul);
                    payload[off + 1] = (byte)(src[off + 1] * brightnessMul);
                    payload[off + 2] = (byte)(src[off + 2] * brightnessMul);
                }
            }
        }

        var nowMs = nowTicks / TimeSpan.TicksPerMillisecond;
        if (_lastPushed.TryGetValue(zoneId, out var previous)
            && previous.Length == payload.Length
            && previous.AsSpan().SequenceEqual(payload)
            && _lastPushedAtMs.TryGetValue(zoneId, out var sentAt)
            && nowMs - sentAt < ReassertPeriodMs)
        {
            return;
        }

        if (_hub.SetDirectColors(channel.ChannelId, payload))
        {
            _lastPushed[zoneId] = payload;
            _lastPushedAtMs[zoneId] = nowMs;
        }
        else
        {
            // Force a re-push next tick rather than leaving a stale "already sent" entry.
            _lastPushed.Remove(zoneId);
        }
    }

    private static double ComputeBrightnessMul(
        string id,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == id) return 0.0;
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
