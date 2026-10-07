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

/// <summary>Copies the engine's ring frame to <see cref="HydroShift2Aio"/>, which rate-limits the uploads.</summary>
public sealed class HydroShift2LightingFrameWriter : BackgroundService
{
    private const int TickMs = 100;

    private readonly LightingEngine _engine;
    private readonly HydroShift2Aio _aio;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly byte[] _ring = new byte[HydroShift2Protocol.RingLedCount * 3];
    private string? _effectSignature;

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
            return;
        }
        var settings = _store.Load();
        var id = HydroShift2LightingDeviceProvider.RingZoneId;
        // Uncontrolled: stop writing so the ring keeps whatever it last showed.
        if (settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            _effectSignature = null;
            return;
        }
        // Ring uploads are rate-limited, so identify holds solid white rather than flashing.
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out _))
        {
            _effectSignature = null;
            Array.Fill(_ring, (byte)255);
            _aio.SetRing(_ring);
            return;
        }
        var ls = settings.Devices.HydroShift2Lighting;
        if (ls.Mode != HydroShift2LightingSettings.CanvasMode)
        {
            TickEffect(settings, ls, id);
            return;
        }
        _effectSignature = null;

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
            settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
            var brightness = Math.Min(
                Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0,
                MasterBrightness.Effective(settings.Lighting));
            var adjust = DeviceColorAdjust.For(pref);
            var src = frame.LedBytes;
            for (int i = 0; i + 2 < _ring.Length && i + 2 < src.Length; i += 3)
            {
                adjust.Apply(src[i], src[i + 1], src[i + 2], brightness, out _ring[i], out _ring[i + 1], out _ring[i + 2]);
            }
        }
        _aio.SetRing(_ring);
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
