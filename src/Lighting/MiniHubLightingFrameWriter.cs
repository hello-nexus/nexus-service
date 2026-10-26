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

    /// <summary>
    /// Minimum interval between consecutive non-black frame writes on the same
    /// channel (~10 Hz). MiniHub runs over a 115200-baud UART where a full
    /// 4-channel cycle is 778 bytes (~67.5 ms wire time plus MCU WS2812B
    /// bit-banging); streaming all 4 channels at 30 Hz overruns the serial link
    /// by >200% and causes visible flicker.
    /// </summary>
    internal const int MinStreamIntervalMs = 100;

    private readonly LightingEngine _engine;
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Func<long> _nowMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly MiniHubColor[]?[] _portBuffers = new MiniHubColor[MiniHubProtocol.LedPortCount][];
    private readonly int[] _currentLedCounts = new int[MiniHubProtocol.LedPortCount];
    private readonly MiniHubColor[]?[] _lastSentBuffers = new MiniHubColor[MiniHubProtocol.LedPortCount][];
    private readonly int[] _lastSentLedCounts = new int[MiniHubProtocol.LedPortCount];
    private bool _hasSentCycle;
    private int _pendingConfirmCycles;
    private long _lastCycleWriteMs;
    private int _lastConnectEpoch = -1;

    private readonly FeatureGates _gates;

    // Last resolved zones per chainable port, keyed by port id. Resolve reads
    // ZonePartitions/PortChains/ZoneLedCounts lock-free while routes mutate
    // them in place, so a mid-enumeration InvalidOperationException falls
    // back to last tick's zones rather than dropping the frame.
    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<ResolvedZone>> _zoneCache = new();

    public MiniHubLightingFrameWriter(
        LightingEngine engine,
        MiniHubHub hub,
        IConfigStore store,
        Np50IdentifyTracker identify,
        FeatureGates? gates = null,
        Func<long>? nowMs = null)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
        _gates = gates ?? FeatureGates.AllEnabled;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
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

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            InvalidateSentState();
            return;
        }
        var epoch = _hub.ConnectEpoch;
        if (epoch != _lastConnectEpoch)
        {
            InvalidateSentState();
            _lastConnectEpoch = epoch;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        var nowTicks = DateTime.UtcNow.Ticks;
        var nowMs = _nowMs();

        var hubId = _hub.DeviceId;
        var port3Zones = ResolveOrReuse($"{hubId}:port3",
            () => MiniHubLightingDeviceProvider.ResolveLedPortZones(settings, hubId, 3, _hub.State.Port3.LedCount));
        var port4Zones = ResolveOrReuse($"{hubId}:port4",
            () => MiniHubLightingDeviceProvider.ResolveLedPortZones(settings, hubId, 4, _hub.State.Port4.LedCount));

        var port1Uncontrolled = uncontrolled.Count > 0 && uncontrolled.Contains($"{hubId}:port1");
        var port2Uncontrolled = uncontrolled.Count > 0 && uncontrolled.Contains($"{hubId}:port2");
        var port3Uncontrolled = ZoneResolution.IsFullyUncontrolled(port3Zones, uncontrolled);
        var port4Uncontrolled = ZoneResolution.IsFullyUncontrolled(port4Zones, uncontrolled);

        // Every port uncontrolled: leave the whole hub alone so it drops back to
        // its firmware animation, same as never pushing at all. A chained port
        // (3/4) counts as uncontrolled only when every one of its products does.
        if (port1Uncontrolled && port2Uncontrolled && port3Uncontrolled && port4Uncontrolled)
        {
            InvalidateSentState();
            return;
        }

        // Stage all 4 channels and push the full 4-port cycle whenever any
        // channel's LED output changes. Like Np50LightingFrameWriter (which
        // shares HYTE's CoolingHubBaseController.SendToHardware firmware
        // contract), the MiniHub firmware only commits its lighting latch when
        // all 4 channels (1..4) are written in a cycle; skipping unchanged empty
        // channels (such as port 3/4) leaves the latch uncommitted.
        PrepareZone(devices, $"{hubId}:port1", channel: 1, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        PrepareZone(devices, $"{hubId}:port2", channel: 2, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        PreparePort(devices, port3Zones, channel: 3, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        PreparePort(devices, port4Zones, channel: 4, disabled, uncontrolled, prefs, globalBrightness, nowTicks);

        PushCycleIfChanged(nowMs);
    }

    private void InvalidateSentState()
    {
        _hasSentCycle = false;
        _pendingConfirmCycles = 0;
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

    private void PrepareZone(DeviceFrame[] devices, string id, int channel,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }
        var ledCount = frame is null ? 0 : frame.LedCount;
        var brightnessMul = ComputeBrightnessMul(id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < Math.Max(ledCount, 1))
            _portBuffers[idx] = new MiniHubColor[Math.Max(ledCount, 64)];
        var dst = _portBuffers[idx]!;
        if (ledCount > 0) Array.Clear(dst, 0, Math.Min(ledCount, dst.Length));
        if (ledCount > 0 && frame is not null)
        {
            FillBufferSlice(dst, 0, frame.LedBytes, ledCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
        }
        _currentLedCounts[idx] = ledCount;
    }

    /// <summary>
    /// Fill a chainable port's buffer from its resolved zones, laid down back
    /// to back in chain order. Power, brightness, colour trim and
    /// identify are per-zone, so one product in a chain can be flashed or
    /// switched off without touching the ones beside it on the same wire.
    /// </summary>
    private void PreparePort(DeviceFrame[] devices,
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
        if (total > 0) Array.Clear(dst, 0, Math.Min(total, dst.Length));

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
        _currentLedCounts[idx] = total;
    }

    private void PushCycleIfChanged(long nowMs)
    {
        var changed = !CycleEquals();
        if (!changed)
        {
            if (_pendingConfirmCycles <= 0 || nowMs - _lastCycleWriteMs < MinStreamIntervalMs)
            {
                return;
            }
            if (WriteFullCycle())
            {
                _pendingConfirmCycles--;
                _lastCycleWriteMs = _nowMs();
            }
            return;
        }

        // Rate-limit continuous streaming so animated effects don't saturate
        // the 115200-baud UART (778 bytes/cycle = 67.5 ms wire time; 10 Hz =
        // 7,780 B/s < 11,520 B/s). First write after connect/uncontrol and
        // blackout/off frames bypass the interval so power-off is immediate.
        if (_hasSentCycle
            && !IsCycleAllBlack()
            && nowMs - _lastCycleWriteMs < MinStreamIntervalMs)
        {
            return;
        }

        if (!WriteFullCycle())
        {
            _hasSentCycle = false;
            return;
        }

        for (var idx = 0; idx < MiniHubProtocol.LedPortCount; idx++)
        {
            var ledCount = _currentLedCounts[idx];
            var dst = _portBuffers[idx]!;
            var last = _lastSentBuffers[idx];
            if (last is null || last.Length < Math.Max(ledCount, 1))
            {
                last = new MiniHubColor[Math.Max(ledCount, 64)];
                _lastSentBuffers[idx] = last;
            }
            if (ledCount > 0)
            {
                Array.Copy(dst, 0, last, 0, ledCount);
            }
            _lastSentLedCounts[idx] = ledCount;
        }
        _hasSentCycle = true;
        _pendingConfirmCycles = 1;
        _lastCycleWriteMs = _nowMs();
    }

    private bool WriteFullCycle()
    {
        lock (_hub.IoLock)
        {
            for (var channel = 1; channel <= MiniHubProtocol.LedPortCount; channel++)
            {
                var idx = channel - 1;
                var dst = _portBuffers[idx]!;
                var ledCount = _currentLedCounts[idx];
                if (!_hub.WriteLighting(channel, new ReadOnlySpan<MiniHubColor>(dst, 0, ledCount)))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private bool CycleEquals()
    {
        if (!_hasSentCycle) return false;
        for (var idx = 0; idx < MiniHubProtocol.LedPortCount; idx++)
        {
            var ledCount = _currentLedCounts[idx];
            if (_lastSentLedCounts[idx] != ledCount) return false;
            if (!FramesEqual(_lastSentBuffers[idx], _portBuffers[idx]!, ledCount)) return false;
        }
        return true;
    }

    private bool IsCycleAllBlack()
    {
        for (var idx = 0; idx < MiniHubProtocol.LedPortCount; idx++)
        {
            if (!IsAllBlack(_portBuffers[idx]!, _currentLedCounts[idx])) return false;
        }
        return true;
    }

    private static bool FramesEqual(MiniHubColor[]? last, MiniHubColor[] current, int ledCount)
    {
        if (ledCount == 0) return true;
        if (last is null || last.Length < ledCount) return false;
        for (var i = 0; i < ledCount; i++)
        {
            if (last[i] != current[i]) return false;
        }
        return true;
    }

    private static bool IsAllBlack(MiniHubColor[] buf, int ledCount)
    {
        for (var i = 0; i < ledCount && i < buf.Length; i++)
        {
            var c = buf[i];
            if (c.R != 0 || c.G != 0 || c.B != 0) return false;
        }
        return true;
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
