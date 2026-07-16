using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

public sealed class StrimerLightingFrameWriter : IHostedService, IDisposable
{
    // ~90ms tick matches the OpenRGB FPS gate for this device family. Source: OpenRGB.
    private const int TickPeriodMs = 90;

    private readonly LightingEngine _engine;
    private readonly StrimerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly StrimerLightingDeviceProvider _provider;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Per-zone segment buffers; separate buffers for ATX (6 segments) and GPU (6 segments).
    private RgbColor[][] _atxSegBuf = Array.Empty<RgbColor[]>();
    private RgbColor[][] _gpuSegBuf = Array.Empty<RgbColor[]>();

    // Scratch byte buffer for wire encoding one zone's LED data.
    private readonly byte[] _ledBuf = new byte[StrimerProtocol.MaxLedsPerZone * 3];

    // Last firmware-mode signature committed; null forces re-commit on next tick.
    private int? _lastFirmwareSig;

    public StrimerLightingFrameWriter(
        LightingEngine engine,
        StrimerHub hub,
        IConfigStore store,
        Np50IdentifyTracker identify,
        StrimerLightingDeviceProvider provider)
    {
        _engine   = engine;
        _hub      = hub;
        _store    = store;
        _identify = identify;
        _provider = provider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts  = new CancellationTokenSource();
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
        _cts  = null;
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
                Console.Error.WriteLine($"[strimer-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_hub.IsConnected)
        {
            _lastFirmwareSig = null;
            return;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings         = _store.Load();
        var ls               = settings.Devices.StrimerLighting;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);

        if (ls.Mode == "custom")
        {
            _lastFirmwareSig = null;
            TickCustom(settings, devices, globalBrightness);
            return;
        }

        var mode = StrimerLightingModes.Find(ls.Mode);
        if (mode == null) return;

        var sig = ComputeFirmwareSig(ls, globalBrightness);
        if (_lastFirmwareSig.HasValue && sig == _lastFirmwareSig.Value) return;

        CommitFirmwareMode(ls, mode, globalBrightness);
        _lastFirmwareSig = sig;
    }

    internal void TickCustom(NexusSettings settings, DeviceFrame[] devices, float globalBrightness)
    {
        var ls           = settings.Devices.StrimerLighting;
        var speedByte    = StrimerLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte      = StrimerLightingModes.DirectionByte(ls.Direction);
        var brightnessByte = ComputeBrightnessByte(ls.Brightness, globalBrightness);

        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs    = settings.Devices.LightingDevicePrefs;
        var nowTicks = DateTime.UtcNow.Ticks;

        var structures = _provider.BuildStructures();

        // ATX: 6 segments, 20 LEDs each.
        var atxStructure = structures[0];
        var atxZones     = ZoneResolution.Resolve(atxStructure, settings);
        SegmentFrameComposer.EnsureBuffers(atxStructure, ref _atxSegBuf);
        SegmentFrameComposer.Compose(
            atxStructure, atxZones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _atxSegBuf);
        for (var s = 0; s < StrimerProtocol.AtxZoneCount; s++)
        {
            // A fully uncontrolled strip skips its color/commit pair entirely,
            // so it stops updating rather than going black.
            if (uncontrolled.Count > 0 && uncontrolled.Contains($"strimer:atx:z{s}")) continue;
            var zone = StrimerProtocol.AtxZone(s);
            FillLedBuf(_atxSegBuf[s], StrimerProtocol.AtxLedsPerZone);
            _hub.SendColorData(zone, _ledBuf.AsSpan(0, StrimerProtocol.AtxLedsPerZone * 3));
            _hub.SendEffectCommit(zone, StrimerProtocol.ModeDirect, speedByte, dirByte, brightnessByte);
        }

        // GPU: 6 segments, 27 LEDs each.
        var gpuStructure = structures[1];
        var gpuZones     = ZoneResolution.Resolve(gpuStructure, settings);
        SegmentFrameComposer.EnsureBuffers(gpuStructure, ref _gpuSegBuf);
        SegmentFrameComposer.Compose(
            gpuStructure, gpuZones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _gpuSegBuf);
        for (var s = 0; s < StrimerProtocol.GpuZoneCount; s++)
        {
            if (uncontrolled.Count > 0 && uncontrolled.Contains($"strimer:gpu:z{s}")) continue;
            var zone = StrimerProtocol.GpuZone(s);
            FillLedBuf(_gpuSegBuf[s], StrimerProtocol.GpuLedsPerZone);
            _hub.SendColorData(zone, _ledBuf.AsSpan(0, StrimerProtocol.GpuLedsPerZone * 3));
            _hub.SendEffectCommit(zone, StrimerProtocol.ModeDirect, speedByte, dirByte, brightnessByte);
        }

        _hub.SendApplyLatch();
    }

    // Writes RGB values from src into _ledBuf as R,G,B. StrimerProtocol.WriteColorData
    // applies the R,B,G wire swap when the hub sends.
    private void FillLedBuf(RgbColor[] src, int count)
    {
        var n = Math.Min(src.Length, count);
        for (var i = 0; i < n; i++)
        {
            var c        = src[i];
            _ledBuf[i * 3]     = c.R;
            _ledBuf[i * 3 + 1] = c.G;
            _ledBuf[i * 3 + 2] = c.B;
        }
        // Zero remainder if src is shorter than the zone size.
        for (var i = n; i < count; i++)
        {
            _ledBuf[i * 3]     = 0;
            _ledBuf[i * 3 + 1] = 0;
            _ledBuf[i * 3 + 2] = 0;
        }
    }

    private void CommitFirmwareMode(StrimerLightingSettings ls, StrimerModeInfo mode, float globalBrightness)
    {
        var speedByte      = StrimerLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte        = StrimerLightingModes.DirectionByte(ls.Direction);
        var brightnessByte = ComputeBrightnessByte(ls.Brightness, globalBrightness);

        for (var zone = 0; zone < StrimerProtocol.ZoneCount; zone++)
        {
            _hub.SendEffectCommit(zone, mode.EffectByte, speedByte, dirByte, brightnessByte);
        }
        _hub.SendApplyLatch();
    }

    private static byte ComputeBrightnessByte(int brightness, float globalBrightness)
    {
        if (globalBrightness <= 0f) return StrimerLightingModes.BrightnessCodes[0];
        var idx = (int)Math.Round(Math.Min((double)brightness, globalBrightness * 4.0));
        return StrimerLightingModes.BrightnessCodes[Math.Clamp(idx, 0, 4)];
    }

    private static int ComputeFirmwareSig(StrimerLightingSettings ls, float globalBrightness)
    {
        var hc = new HashCode();
        hc.Add(ls.Mode);
        hc.Add(ls.Speed);
        hc.Add(ls.Direction);
        hc.Add(ls.Brightness);
        hc.Add(globalBrightness);
        return hc.ToHashCode();
    }
}
