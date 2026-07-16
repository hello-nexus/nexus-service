using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
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

    public SmartHubLightingFrameWriter(LightingEngine engine, SmartHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;
        var settings = _store.Load();
        // Firmware animation drives the ports; streaming would overwrite it.
        if (settings.Devices.SmartHubFirmwareControl) return;
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var hubId = _hub.DeviceId;
        var mirror = SmartHubLightingDeviceProvider.ReadMirror(settings, hubId);

        // Every port uncontrolled: leave the hub alone entirely so it drops back
        // to its firmware animation. When mirrored, the single mirror id
        // stands in for every physical port.
        if (uncontrolled.Count > 0)
        {
            var fullyUncontrolled = mirror
                ? uncontrolled.Contains(SmartHubLightingDeviceProvider.MirrorId(hubId))
                : AllPortsUncontrolled(hubId, uncontrolled);
            if (fullyUncontrolled) return;
        }

        // Push every port every tick - even ports with zero declared LEDs get
        // a zero-length frame, which the hub honours by keeping the strip dark
        // and stops it falling back to the firmware animation. When mirrored,
        // every port streams the one mirror device's frame.
        for (var channel = 1; channel <= SmartHubProtocol.ArgbPortCount; channel++)
        {
            var id = mirror ? SmartHubLightingDeviceProvider.MirrorId(hubId) : $"{hubId}:port{channel}";
            TryPushZone(devices, id, channel, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        }
    }

    private static bool AllPortsUncontrolled(string hubId, System.Collections.Generic.IReadOnlyList<string> uncontrolled)
    {
        for (var channel = 1; channel <= SmartHubProtocol.ArgbPortCount; channel++)
        {
            if (!uncontrolled.Contains($"{hubId}:port{channel}"))
            {
                return false;
            }
        }
        return true;
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
        var ledCount = frame is null ? 0 : frame.LedCount;
        var brightnessMul = ComputeBrightnessMul(id, disabled, uncontrolled, prefs, globalBrightness);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < Math.Max(ledCount, 1))
            _portBuffers[idx] = new SmartHubColor[Math.Max(ledCount, 64)];
        var dst = _portBuffers[idx]!;
        if (ledCount > 0 && frame is not null)
        {
            FillBufferSlice(dst, 0, frame.LedBytes, ledCount, brightnessMul, hasIdentify, startTicks, nowTicks);
        }
        _hub.WriteLighting(channel, new ReadOnlySpan<SmartHubColor>(dst, 0, ledCount));
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness)
    {
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        if (uncontrolled.Count > 0)
        {
            foreach (var u in uncontrolled) if (u == id) return 0.0;
        }
        int devBrightness;
        try { devBrightness = prefs.TryGetValue(id, out var pref) ? pref.Brightness : 100; }
        catch (InvalidOperationException) { devBrightness = 100; }
        return Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);
    }

    private static void FillBufferSlice(SmartHubColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, bool hasIdentify, long identifyStartTicks, long nowTicks)
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
