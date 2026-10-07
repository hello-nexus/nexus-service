using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Drives the HydroShift II ring: a ring effect, or the Lighting page. The ring plays an
/// uploaded loop on its own clock, which the USB link cannot read, so a Lighting page effect
/// that can be rendered ahead is captured as one seamless loop (its repeat, or a crossfaded
/// seam) and uploaded once; anything else streams as rate-limited single frames.
/// </summary>
public sealed class HydroShift2LightingFrameWriter : BackgroundService
{
    private const int TickMs = 100;

    /// <summary>Loop frame interval in ring clock ticks (0.625 ms), so one frame every 50 ms.</summary>
    internal const int LoopIntervalTicks = 80;
    private const double LoopFrameMs = LoopIntervalTicks * 0.625;
    /// <summary>Longest loop: past this an effect that never repeats gets a crossfaded seam.</summary>
    internal const int LoopMaxFrames = 160;
    /// <summary>Frames compared to find where an effect repeats, also the crossfade length.</summary>
    internal const int LoopMatchFrames = 12;
    private const int LoopMinFrames = 8;
    /// <summary>Mean per-channel difference under which two stretches of frames count as the same.</summary>
    private const double LoopMatchTolerance = 3.0;
    /// <summary>The ring applies an upload a few hundred ms after the request, so rendering starts past that.</summary>
    private const int LoopLeadMs = 300;

    private readonly LightingEngine _engine;
    private readonly HydroShift2Aio _aio;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly byte[] _ring = new byte[HydroShift2Protocol.RingLedCount * 3];
    private string? _effectSignature;
    private AheadRequest? _loopRequest;
    private string? _loopRequestContext;
    private string? _loopContext;

    public HydroShift2LightingFrameWriter(
        LightingEngine engine, HydroShift2Aio aio, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _aio = aio;
        _store = store;
        _identify = identify;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{HydroShift2LcdDriver.Id}] lighting tick failed: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting || !_aio.IsAvailable)
        {
            _effectSignature = null;
            DropLoop();
            return;
        }
        var settings = _store.Load();
        var id = HydroShift2LightingDeviceProvider.RingZoneId;
        // Uncontrolled: stop writing so the ring keeps whatever it last showed.
        if (settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            _effectSignature = null;
            DropLoop();
            return;
        }
        // Ring uploads are rate-limited, so identify holds solid white rather than flashing.
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out _))
        {
            _effectSignature = null;
            DropLoop();
            Array.Fill(_ring, (byte)255);
            _aio.SetRing(_ring);
            return;
        }
        var ls = settings.Devices.HydroShift2Lighting;
        if (ls.Mode != HydroShift2LightingSettings.CanvasMode)
        {
            DropLoop();
            TickEffect(settings, ls, id);
            return;
        }
        _effectSignature = null;

        var ids = new[] { id };
        if (settings.Devices.DisabledLightingDevices.Contains(id) || !_engine.CanRenderAhead(ids))
        {
            DropLoop();
            TickLive(settings, id);
            return;
        }
        TickLoop(settings, id, ids);
    }

    private void TickLive(NexusSettings settings, string id)
    {
        DeviceFrame? frame = null;
        foreach (var candidate in _engine.Devices)
        {
            if (candidate.Id == id) { frame = candidate; break; }
        }
        if (frame is null)
        {
            return;
        }
        Array.Clear(_ring);
        if (!settings.Devices.DisabledLightingDevices.Contains(id))
        {
            ToRing(settings, id, frame.LedBytes, _ring);
        }
        _aio.SetRing(_ring);
    }

    /// <summary>Renders the effect ahead once per change of effect, layout or colour settings, and uploads it as a loop.</summary>
    private void TickLoop(NexusSettings settings, string id, string[] ids)
    {
        var context = LoopContext(settings, id);
        if (_loopRequest is { } request)
        {
            if (!request.Done.IsSet)
            {
                return;
            }
            _loopRequest = null;
            if (request.Frames is null || _loopRequestContext != context)
            {
                return;
            }
            var frames = new List<byte[]>(request.Frames.Length);
            foreach (var rendered in request.Frames)
            {
                var ring = Array.Find(rendered, d => d.Id == id);
                if (ring is null)
                {
                    // The ring left the engine's device list mid-render; the next tick asks again.
                    return;
                }
                var bytes = new byte[_ring.Length];
                ToRing(settings, id, ring.LedBytes, bytes);
                frames.Add(bytes);
            }
            var (packed, count) = FitLoop(frames);
            ServiceLog.Info($"[{HydroShift2LcdDriver.Id}] ring loop: {count} frames from {frames.Count} rendered");
            _aio.SetRingAnimation(packed, count, LoopIntervalTicks);
            _loopContext = context;
            return;
        }
        if (_loopContext == context)
        {
            return;
        }
        // A render requested before the engine lists the ring would come back without it.
        if (Array.Find(_engine.Devices, d => d.Id == id) is null)
        {
            return;
        }
        // The engine renders against Unix-epoch milliseconds.
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + LoopLeadMs;
        var ticks = new long[LoopMatchFrames + LoopMaxFrames];
        for (int k = 0; k < ticks.Length; k++)
        {
            ticks[k] = start + (long)(k * LoopFrameMs);
        }
        _loopRequest = _engine.RequestAhead(ids, ticks);
        _loopRequestContext = context;
    }

    private void DropLoop()
    {
        _loopRequest?.Abandon();
        _loopRequest = null;
        _loopContext = null;
    }

    /// <summary>What a rendered loop depends on beyond the effect's own parameters, whose changes pause render-ahead and so restart it.</summary>
    private string LoopContext(NexusSettings settings, string id)
    {
        var hc = new HashCode();
        hc.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_engine.CurrentEffect));
        hc.Add(MasterBrightness.Effective(settings.Lighting));
        if (settings.Lighting.DeviceLayouts.TryGetValue(id, out var layout))
        {
            hc.Add(layout.X);
            hc.Add(layout.Y);
            hc.Add(layout.W);
            hc.Add(layout.H);
            hc.Add(layout.Rotation);
        }
        if (settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            hc.Add(pref.Brightness);
            hc.Add(pref.AdjustRed);
            hc.Add(pref.AdjustGreen);
            hc.Add(pref.AdjustBlue);
            hc.Add(pref.AdjustTemperature);
            hc.Add(pref.AdjustSaturation);
        }
        if (_engine.LedColorLocks?.TryGet(id, out var locks) == true)
        {
            foreach (var led in locks)
            {
                hc.Add(led);
            }
        }
        return hc.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }

    private static void ToRing(NexusSettings settings, string id, ReadOnlySpan<byte> src, byte[] ring)
    {
        settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
        var brightness = Math.Min(
            Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0,
            MasterBrightness.Effective(settings.Lighting));
        var adjust = DeviceColorAdjust.For(pref);
        for (int i = 0; i + 2 < ring.Length && i + 2 < src.Length; i += 3)
        {
            adjust.Apply(src[i], src[i + 1], src[i + 2], brightness, out ring[i], out ring[i + 1], out ring[i + 2]);
        }
    }

    /// <summary>
    /// Turns frames rendered at the loop interval into a seamless loop. The first
    /// <see cref="LoopMatchFrames"/> are a pre-roll; the loop is the shortest stretch after it
    /// that the effect repeats, or, for an effect that never does, everything after the
    /// pre-roll with its last frames crossfaded into the pre-roll so the wrap is continuous.
    /// </summary>
    internal static (byte[] Frames, int Count) FitLoop(IReadOnlyList<byte[]> frames)
    {
        const int pre = LoopMatchFrames;
        var body = frames.Count - pre;
        for (int length = LoopMinFrames; length + LoopMatchFrames <= body; length++)
        {
            if (MeanDifference(frames, pre, pre + length, LoopMatchFrames) <= LoopMatchTolerance)
            {
                return (Pack(frames, pre, length), length);
            }
        }
        var faded = new List<byte[]>(body);
        for (int i = 0; i < body; i++)
        {
            faded.Add(frames[pre + i]);
        }
        for (int i = 0; i < LoopMatchFrames; i++)
        {
            // The tail eases into the pre-roll, which runs straight on into the loop's first frame.
            var alpha = (i + 1) / (double)(LoopMatchFrames + 1);
            var tail = faded[body - LoopMatchFrames + i];
            var blended = new byte[tail.Length];
            for (int c = 0; c < tail.Length; c++)
            {
                blended[c] = (byte)Math.Round((tail[c] * (1 - alpha)) + (frames[i][c] * alpha));
            }
            faded[body - LoopMatchFrames + i] = blended;
        }
        return (Pack(faded, 0, body), body);
    }

    private static double MeanDifference(IReadOnlyList<byte[]> frames, int a, int b, int count)
    {
        long sum = 0;
        long n = 0;
        for (int k = 0; k < count; k++)
        {
            var x = frames[a + k];
            var y = frames[b + k];
            for (int c = 0; c < x.Length; c++)
            {
                sum += Math.Abs(x[c] - y[c]);
                n++;
            }
        }
        return n == 0 ? double.MaxValue : sum / (double)n;
    }

    private static byte[] Pack(IReadOnlyList<byte[]> frames, int first, int count)
    {
        var size = frames[first].Length;
        var packed = new byte[count * size];
        for (int k = 0; k < count; k++)
        {
            frames[first + k].CopyTo(packed, k * size);
        }
        return packed;
    }

    /// <summary>Renders the chosen ring effect once per settings change; the pump head then loops it without further uploads.</summary>
    private void TickEffect(NexusSettings settings, HydroShift2LightingSettings ls, string id)
    {
        var mode = settings.Devices.DisabledLightingDevices.Contains(id) ? HydroShift2RingEffects.Off : ls.Mode;
        settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
        // The card and master brightness (and the blackouts that zero it) cap the effect's own
        // level, rounded up so a dim setting dims the ring rather than switching it off.
        var cap = Math.Min(Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0, MasterBrightness.Effective(settings.Lighting));
        var level = Math.Min(ls.Brightness, (int)Math.Ceiling(cap * HydroShift2RingEffects.MaxLevel));
        var signature = $"{mode}|{level}|{ls.Speed}|{ls.Direction}|{string.Join(',', ls.Colors)}";
        if (signature == _effectSignature)
        {
            return;
        }
        _effectSignature = signature;

        var colors = new List<(byte R, byte G, byte B)>(ls.Colors.Count);
        foreach (var hex in ls.Colors)
        {
            if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var rgb))
            {
                colors.Add(((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
        }
        var (frames, interval) = HydroShift2RingEffects.Render(mode, colors, level, ls.Speed, ls.Direction == 1);
        var packed = new byte[frames.Count * _ring.Length];
        for (int f = 0; f < frames.Count; f++)
        {
            frames[f].CopyTo(packed, f * _ring.Length);
        }
        _aio.SetRingAnimation(packed, frames.Count, interval);
    }
}
