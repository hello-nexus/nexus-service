using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

public sealed class StrimerLightingFrameWriter : IHostedService, IDisposable
{
    // ~90ms tick matches the OpenRGB FPS gate for this device family. Source: OpenRGB.
    private const int TickPeriodMs = 90;

    private readonly LightingEngine _engine;
    private readonly StrimerUnits _units;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly StrimerLightingDeviceProvider _provider;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Per-controller send state, indexed by unit slot.
    private sealed class UnitState
    {
        // Separate segment buffers for ATX (6 segments) and GPU (4 or 6 segments).
        public RgbColor[][] AtxSegBuf = Array.Empty<RgbColor[]>();
        public RgbColor[][] GpuSegBuf = Array.Empty<RgbColor[]>();
        // Last firmware-mode signature committed; null forces re-commit on next tick.
        public int? LastFirmwareSig;
        // Sync state the controller last accepted; false after attach, so a cable never put on sync sees no extra write.
        public bool ArgbSyncSent;
        public long ArgbRetryAtMs;
        public int Generation = -1;
    }

    private readonly Dictionary<int, UnitState> _states = new();

    private UnitState StateFor(StrimerHub hub)
    {
        if (!_states.TryGetValue(hub.Unit, out var st))
        {
            st = new UnitState();
            _states[hub.Unit] = st;
        }
        return st;
    }

    // Scratch byte buffer for wire encoding one zone's LED data.
    private readonly byte[] _ledBuf = new byte[StrimerProtocol.MaxLedsPerZone * 3];

    private const long ArgbRetryMs = 1000;

    private readonly FeatureGates _gates;

    public StrimerLightingFrameWriter(
        LightingEngine engine,
        StrimerUnits units,
        IConfigStore store,
        Np50IdentifyTracker identify,
        StrimerLightingDeviceProvider provider,
        FeatureGates? gates = null)
    {
        _engine   = engine;
        _units    = units;
        _store    = store;
        _identify = identify;
        _provider = provider;
        _gates    = gates ?? FeatureGates.AllEnabled;
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

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        foreach (var hub in _units.All)
        {
            var st = StateFor(hub);
            if (!hub.IsConnected)
            {
                st.LastFirmwareSig = null;
                st.ArgbSyncSent = false;
                st.ArgbRetryAtMs = 0;
                continue;
            }
            var generation = hub.Generation;
            if (st.Generation != generation)
            {
                st.Generation = generation;
                st.LastFirmwareSig = null;
                st.ArgbSyncSent = false;
                st.ArgbRetryAtMs = 0;
            }
            TickUnit(hub, st);
        }
    }

    private void TickUnit(StrimerHub hub, UnitState st)
    {
        var settings         = _store.Load();
        var ls               = settings.Devices.StrimerLighting;
        // Under sync the cable has no engine cards, so the switch runs before the device check.
        if (ls.ArgbSync != st.ArgbSyncSent)
        {
            var now = Environment.TickCount64;
            if (now < st.ArgbRetryAtMs) return;
            if (!hub.SendArgbSync(ls.ArgbSync))
            {
                if (st.ArgbRetryAtMs == 0) ServiceLog.Warn($"[strimer-lighting-writer] ARGB sync switch rejected by controller {hub.Unit + 1}; retrying");
                st.ArgbRetryAtMs = now + ArgbRetryMs;
                return;
            }
            st.ArgbRetryAtMs = 0;
            st.ArgbSyncSent = ls.ArgbSync;
            st.LastFirmwareSig = null;
        }
        if (ls.ArgbSync) return;

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var globalBrightness = MasterBrightness.Resolve(settings);

        if (ls.Mode == "custom")
        {
            st.LastFirmwareSig = null;
            TickCustom(hub, st, settings, devices, globalBrightness);
            return;
        }

        if (StrimerLightingModes.Find(ls.Mode) == null) return;

        // The plan walks dictionaries a settings write edits in place, so it is built under the store's lock.
        var gpuZones = StrimerProtocol.NormalizeGpuZoneCount(settings.Devices.StrimerGpuZones);
        List<StrimerZonePlan> plan = new();
        _store.Read(s => plan = StrimerEffectLooks.BuildPlan(s.Devices.StrimerLighting, globalBrightness, gpuZones));
        var sig = ComputeFirmwareSig(plan);
        if (st.LastFirmwareSig.HasValue && sig == st.LastFirmwareSig.Value) return;

        CommitFirmwareMode(hub, plan);
        st.LastFirmwareSig = sig;
    }

    internal void TickCustom(NexusSettings settings, DeviceFrame[] devices, MasterLevel globalBrightness)
    {
        foreach (var hub in _units.Connected()) TickCustom(hub, StateFor(hub), settings, devices, globalBrightness);
    }

    private void TickCustom(StrimerHub hub, UnitState st, NexusSettings settings, DeviceFrame[] devices, MasterLevel globalBrightness)
    {
        var ls           = settings.Devices.StrimerLighting;
        var speedByte    = StrimerLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte      = StrimerLightingModes.DirectionByte(ls.Direction);
        var brightnessByte = ComputeBrightnessByte(ls.Brightness, globalBrightness);

        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs    = settings.Devices.LightingDevicePrefs;
        var nowTicks = DateTime.UtcNow.Ticks;

        var structures = _provider.BuildStructures(hub);

        // ATX: 6 segments, 20 LEDs each.
        var atxStructure = structures[0];
        var atxZones     = ZoneResolution.Resolve(atxStructure, settings);
        SegmentFrameComposer.EnsureBuffers(atxStructure, ref st.AtxSegBuf);
        SegmentFrameComposer.Compose(
            atxStructure, atxZones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, st.AtxSegBuf, _engine.Live, segmentGated: true);
        for (var s = 0; s < StrimerProtocol.AtxZoneCount; s++)
        {
            // A fully uncontrolled strip skips its color/commit pair entirely,
            // so it stops updating rather than going black.
            if (uncontrolled.Count > 0 && uncontrolled.Contains($"{hub.IdPrefix}atx:z{s}")) continue;
            var zone = StrimerProtocol.AtxZone(s);
            FillLedBuf(st.AtxSegBuf[s], StrimerProtocol.AtxLedsPerZone);
            hub.SendColorData(zone, _ledBuf.AsSpan(0, StrimerProtocol.AtxLedsPerZone * 3));
            hub.SendEffectCommit(zone, StrimerProtocol.ModeDirect, speedByte, dirByte, brightnessByte);
        }

        // GPU: 4 or 6 segments depending on the harness, 27 LEDs each.
        var gpuStructure = structures[1];
        var gpuZones     = ZoneResolution.Resolve(gpuStructure, settings);
        SegmentFrameComposer.EnsureBuffers(gpuStructure, ref st.GpuSegBuf);
        SegmentFrameComposer.Compose(
            gpuStructure, gpuZones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, st.GpuSegBuf, _engine.Live, segmentGated: true);
        var gpuZoneCount = StrimerProtocol.NormalizeGpuZoneCount(settings.Devices.StrimerGpuZones);
        for (var s = 0; s < gpuZoneCount; s++)
        {
            if (uncontrolled.Count > 0 && uncontrolled.Contains($"{hub.IdPrefix}gpu:z{s}")) continue;
            var zone = StrimerProtocol.GpuZone(s);
            FillLedBuf(st.GpuSegBuf[s], StrimerProtocol.GpuLedsPerZone);
            hub.SendColorData(zone, _ledBuf.AsSpan(0, StrimerProtocol.GpuLedsPerZone * 3));
            hub.SendEffectCommit(zone, StrimerProtocol.ModeDirect, speedByte, dirByte, brightnessByte);
        }

        hub.SendApplyLatch(gpuZoneCount);
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

    // Per zone the effect command goes first and its colors after, then one latch for the zones written.
    internal static void CommitFirmwareMode(StrimerHub hub, IReadOnlyList<StrimerZonePlan> plan)
    {
        var mask = 0;
        foreach (var z in plan)
        {
            hub.SendEffectCommit(z.Zone, z.Mode.EffectByte, z.Speed, z.Direction, z.Brightness);
            if (z.Colors.Length > 0) hub.SendColorData(z.Zone, z.Colors);
            mask |= 1 << z.Zone;
        }
        hub.SendApplyLatchMask(mask);
    }

    internal static byte ComputeBrightnessByte(int brightness, MasterLevel globalBrightness)
    {
        if (globalBrightness.Level <= 0f) return StrimerLightingModes.BrightnessCodes[0];
        var idx = (int)Math.Round(globalBrightness.CombineSteps(brightness, 4.0));
        return StrimerLightingModes.BrightnessCodes[Math.Clamp(idx, 0, 4)];
    }

    private static int ComputeFirmwareSig(IReadOnlyList<StrimerZonePlan> plan)
    {
        var hc = new HashCode();
        foreach (var z in plan)
        {
            hc.Add(z.Zone);
            hc.Add(z.Mode.EffectByte);
            hc.Add(z.Speed);
            hc.Add(z.Direction);
            hc.Add(z.Brightness);
            foreach (var b in z.Colors) hc.Add(b);
        }
        return hc.ToHashCode();
    }
}
