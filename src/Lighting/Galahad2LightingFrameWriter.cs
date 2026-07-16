using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

public sealed class Galahad2LightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 200;

    private readonly LightingEngine _engine;
    private readonly Galahad2Hub _hub;
    private readonly IConfigStore _store;
    private readonly Galahad2LightingDeviceProvider _provider;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Tracks last canvas LED colors to commit only on change.
    private byte _lastInnerR, _lastInnerG, _lastInnerB;
    private byte _lastOuterR, _lastOuterG, _lastOuterB;
    private bool _lastWasCanvas;

    // Null forces re-commit on next firmware-mode tick even when settings are unchanged.
    private int? _lastFirmwareSig;

    public Galahad2LightingFrameWriter(LightingEngine engine, Galahad2Hub hub, IConfigStore store, Galahad2LightingDeviceProvider provider)
    {
        _engine   = engine;
        _hub      = hub;
        _store    = store;
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
                Console.Error.WriteLine($"[galahad2-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_hub.IsConnected)
        {
            _lastFirmwareSig = null;
            _lastWasCanvas   = false;
            return;
        }

        var settings         = _store.Load();
        var ls               = settings.Devices.Galahad2Lighting;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var brightnessRaw    = (byte)Math.Clamp((int)Math.Round(Math.Min((double)ls.Brightness, globalBrightness * 4.0)), 0, 4);

        // Both rings share one wire packet: only leave the AIO alone entirely
        // once every ring is uncontrolled. A single uncontrolled ring stays on the
        // wire (TickCanvas blacks its color slot; the shared packet can't
        // omit it without also silencing the still-controlled ring). Clearing
        // both caches forces a resend on the next tick after re-enabling,
        // rather than waiting on a color or setting change that may never
        // come while an external app owns the AIO. Resolved against the
        // live zones (not the default ring ids) so a persisted custom
        // partition still gates correctly.
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var structure = _provider.BuildStructure();
        var zones = ZoneResolution.Resolve(structure, settings);
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            _lastFirmwareSig = null;
            _lastWasCanvas   = false;
            return;
        }

        if (ls.Mode == "canvas")
        {
            _lastFirmwareSig = null;
            TickCanvas(brightnessRaw, zones, uncontrolled);
            return;
        }

        var mode = Galahad2LightingModes.Find(ls.Mode);
        if (mode == null)
        {
            return;
        }

        var sig = ComputeFirmwareSig(ls, brightnessRaw);
        if (_lastFirmwareSig.HasValue && sig == _lastFirmwareSig.Value)
        {
            return;
        }

        _lastWasCanvas = false;
        CommitFirmwareMode(ls, mode, brightnessRaw);
        _lastFirmwareSig = sig;
    }

    private void TickCanvas(byte brightnessRaw, System.Collections.Generic.IReadOnlyList<ResolvedZone> zones, System.Collections.Generic.IReadOnlyList<string> uncontrolled)
    {
        var devices = _engine.Devices;
        byte innerR = 0, innerG = 0, innerB = 0;
        byte outerR = 0, outerG = 0, outerB = 0;
        foreach (var frame in devices)
        {
            if (frame.Id == "lianli-aio:ring:inner" && frame.LedCount > 0)
            {
                var b = frame.LedBytes;
                innerR = b[0]; innerG = b[1]; innerB = b[2];
            }
            else if (frame.Id == "lianli-aio:ring:outer" && frame.LedCount > 0)
            {
                var b = frame.LedBytes;
                outerR = b[0]; outerG = b[1]; outerB = b[2];
            }
        }
        if (ZoneResolution.IsSegmentFullyUncontrolled(zones, Galahad2LightingDeviceProvider.InnerSegment, uncontrolled))
        {
            innerR = 0; innerG = 0; innerB = 0;
        }
        if (ZoneResolution.IsSegmentFullyUncontrolled(zones, Galahad2LightingDeviceProvider.OuterSegment, uncontrolled))
        {
            outerR = 0; outerG = 0; outerB = 0;
        }

        if (_lastWasCanvas
            && innerR == _lastInnerR && innerG == _lastInnerG && innerB == _lastInnerB
            && outerR == _lastOuterR && outerG == _lastOuterG && outerB == _lastOuterB)
        {
            return;
        }

        // One R_BOTH (ring=2) packet: slot0=inner, slot1=outer. Matches OpenRGB SetMode_StaticColor.
        var bothColors = new byte[] { innerR, innerG, innerB, outerR, outerG, outerB };
        _hub.SendLighting(2, 0x03, brightnessRaw, 0, 0, bothColors);

        _lastInnerR = innerR; _lastInnerG = innerG; _lastInnerB = innerB;
        _lastOuterR = outerR; _lastOuterG = outerG; _lastOuterB = outerB;
        _lastWasCanvas = true;
    }

    private void CommitFirmwareMode(Galahad2LightingSettings ls, Galahad2ModeInfo mode, byte brightnessRaw)
    {
        var speed     = (byte)Math.Clamp(ls.Speed, 0, 4);
        var direction = (byte)Math.Clamp(ls.Direction, 0, 1);

        _hub.SendLighting(2, mode.WireByte, brightnessRaw, speed, direction, BuildColorBytes(ls, mode));
    }

    // staticColor: slot0 = InnerColor, slot1 = OuterColor. All other modes use ls.Colors.
    internal static byte[] BuildColorBytes(Galahad2LightingSettings ls, Galahad2ModeInfo mode)
    {
        if (mode.Key == "staticColor")
        {
            var (ir, ig, ib) = ParseHex(ls.InnerColor);
            var (or2, og, ob) = ParseHex(ls.OuterColor);
            return new byte[] { ir, ig, ib, or2, og, ob };
        }
        var maxColors  = mode.ColorsMax;
        var colorCount = Math.Min(ls.Colors.Count, maxColors);
        var colorBytes = new byte[colorCount * 3];
        for (var i = 0; i < colorCount; i++)
        {
            var (r, g, b) = ParseHex(ls.Colors[i]);
            colorBytes[i * 3]     = r;
            colorBytes[i * 3 + 1] = g;
            colorBytes[i * 3 + 2] = b;
        }
        return colorBytes;
    }

    private static (byte r, byte g, byte b) ParseHex(string hex)
    {
        if (hex == null || hex.Length < 7 || hex[0] != '#')
        {
            return (0, 0, 0);
        }
        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, null, out var b))
        {
            return (0, 0, 0);
        }
        return (r, g, b);
    }

    private static int ComputeFirmwareSig(Galahad2LightingSettings ls, byte brightnessRaw)
    {
        var hc = new HashCode();
        hc.Add(ls.Mode);
        hc.Add(ls.Speed);
        hc.Add(ls.Direction);
        hc.Add(brightnessRaw);
        hc.Add(ls.InnerColor);
        hc.Add(ls.OuterColor);
        foreach (var c in ls.Colors)
        {
            hc.Add(c);
        }
        return hc.ToHashCode();
    }
}
