using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the iCUE LINK System Hub at 30 Hz.
/// Builds one contiguous RGB byte buffer from all LED-bearing devices in
/// ascending channel order and calls <see cref="CorsairLinkHub.SendColors"/>
/// once per tick. The hub handles chunked endpoint writes internally.
///
/// Wire color order is R,G,B (no swap) per the protocol spec.
/// </summary>
public sealed class CorsairLinkLightingFrameWriter : IHostedService, IDisposable
{
    // 30 Hz, matching the engine's default frame interval.
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly CorsairLinkHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Per-device segment composition buffer: one buffer per segment, always
    // sized to one entry (each LINK device exposes exactly one segment).
    private RgbColor[][] _segBuf = Array.Empty<RgbColor[]>();

    // Scratch buffer for the concatenated wire frame; resized on demand.
    private byte[] _wireBuf = Array.Empty<byte>();

    // Per-tick structure/zone resolution, cleared and repopulated each Tick so
    // the uncontrolled check and the wire-building pass share one resolve per
    // device instead of resolving zones twice.
    private readonly List<DeviceStructure> _tickStructures = new();
    private readonly List<IReadOnlyList<ResolvedZone>> _tickZones = new();

    public CorsairLinkLightingFrameWriter(
        LightingEngine engine, CorsairLinkHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
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
                Console.Error.WriteLine($"[corsair-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
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
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var hubDevices = _hub.State.Devices;
        var hubHasLcd = _hub.State.HasLcd;
        var portCap = ComputePortCapFactor(hubDevices);
        var effectiveBrightness = Math.Min(globalBrightness, portCap);

        var totalLeds = 0;
        var anyChannel = false;
        var hubFullyUncontrolled = true;
        _tickStructures.Clear();
        _tickZones.Clear();
        foreach (var dev in hubDevices)
        {
            if (dev.LedCount <= 0) continue;
            totalLeds += dev.LedCount;
            anyChannel = true;
            var id = $"corsair:ch{dev.Channel}";
            var structure = CorsairLinkLightingDeviceProvider.BuildStructure(id, dev);
            var zones = ZoneResolution.Resolve(structure, settings);
            _tickStructures.Add(structure);
            _tickZones.Add(zones);
            if (hubFullyUncontrolled && !ZoneResolution.IsFullyUncontrolled(zones, uncontrolled)) hubFullyUncontrolled = false;
        }
        if (totalLeds == 0) return;

        // Every channel uncontrolled: leave the whole hub alone so firmware /
        // vendor lighting can take over.
        if (anyChannel && hubFullyUncontrolled) return;

        var totalBytes = totalLeds * 3;
        if (_wireBuf.Length < totalBytes) _wireBuf = new byte[Math.Max(totalBytes, 512)];

        var wireOffset = 0;
        var devIdx = 0;
        foreach (var dev in hubDevices)
        {
            if (dev.LedCount <= 0) continue;
            var structure = _tickStructures[devIdx];
            var zones = _tickZones[devIdx];
            devIdx++;
            SegmentFrameComposer.EnsureBuffers(structure, ref _segBuf);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, effectiveBrightness, 1.0, nowTicks, _identify, _segBuf);

            // Segment 0 holds all LEDs for this device; copy as R,G,B (no swap).
            var buf = _segBuf[0];
            if (dev.Class == CorsairLinkClass.Aio && hubHasLcd)
            {
                ApplyAioInnerRingBlank(buf);
            }
            for (var i = 0; i < buf.Length; i++)
            {
                var c = buf[i];
                _wireBuf[wireOffset]     = c.R;
                _wireBuf[wireOffset + 1] = c.G;
                _wireBuf[wireOffset + 2] = c.B;
                wireOffset += 3;
            }
        }

        _hub.SendColors(new ReadOnlySpan<byte>(_wireBuf, 0, wireOffset));
    }

    /// <summary>
    /// Computes the most restrictive brightness cap across both hub ports.
    /// Per-port LED totals above 238/340/442 trigger 0.66/0.33/0.10 reductions
    /// to stay within port power limits (OpenLinkHub lsh.go:582-605).
    /// Channels 1..12 belong to port 0; channels 13..24 to port 1 (lsh.go:3978).
    /// </summary>
    internal static float ComputePortCapFactor(IReadOnlyList<CorsairLinkDevice> devices)
    {
        var port0 = 0;
        var port1 = 0;
        foreach (var d in devices)
        {
            if (d.LedCount <= 0) continue;
            if (d.PortId == 0)
            {
                port0 += d.LedCount;
            }
            else
            {
                port1 += d.LedCount;
            }
        }
        return Math.Min(StageFactor(port0), StageFactor(port1));
    }

    private static float StageFactor(int portLeds)
    {
        if (portLeds > 442) return 0.10f;
        if (portLeds > 340) return 0.33f;
        if (portLeds > 238) return 0.66f;
        return 1.0f;
    }

    /// <summary>
    /// Zeros the AIO pump inner-ring LEDs (indices 16..19) when a pump LCD is present on the
    /// chain, matching OpenLinkHub (lsh.go:5485).
    /// </summary>
    internal static void ApplyAioInnerRingBlank(RgbColor[] segment)
    {
        for (var i = 16; i < 20 && i < segment.Length; i++)
        {
            segment[i] = default;
        }
    }
}
