using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the Lian Li Uni Hub at 30 Hz (custom mode)
/// or commits firmware animations once on settings change (all other modes).
/// </summary>
public sealed class LianLiLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    // The SL-Infinity firmware needs 5 ms settle after each HID write before the
    // next; back-to-back writes flood it and it falls back to its slow internal
    // refresh. Matches OpenRGB's std::this_thread::sleep_for(5ms).
    private const int InterWriteSettleMs = 5;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private readonly LightingEngine _engine;
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    // Last firmware-mode signature committed to hardware; null = nothing sent yet.
    private int? _lastFirmwareSig;

    // Per-device resolved zones for the firmware-mode sig/commit pair, reused
    // each tick so they resolve once instead of once per call site.
    private readonly List<IReadOnlyList<ResolvedZone>> _firmwareZonesByDevice = new();

    // Raw RGB scratch for one channel: MaxFansPerPort fans * LedsPerFanPerChannel * 3 bytes.
    private readonly byte[] _channelBuf =
        new byte[LianLiProtocol.MaxFansPerPort * LianLiProtocol.LedsPerFanPerChannel * 3];

    // True while timeBeginPeriod(1) is active; matches the custom-streaming lifetime.
    private bool _highResTimer;

    public LianLiLightingFrameWriter(LightingEngine engine, LianLiHub hub, IConfigStore store, Np50IdentifyTracker identify)
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
        if (_highResTimer && OperatingSystem.IsWindows())
        {
            timeEndPeriod(1);
            _highResTimer = false;
        }
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
                Console.Error.WriteLine($"[lianli-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
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
            if (_highResTimer && OperatingSystem.IsWindows())
            {
                timeEndPeriod(1);
                _highResTimer = false;
            }
            // The hub loses commanded LED state on USB re-enumeration, so clear
            // the committed sig: the first reconnected tick re-commits the
            // firmware mode even when settings are unchanged. The cooling path
            // re-asserts on reconnect for the same reason.
            _lastFirmwareSig = null;
            return;
        }
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var ls = settings.Devices.LianLiLighting;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);

        var comp = LianLiZoneSupport.ReadComposition(settings, _hub.DeviceId);
        var composed = LianLiZoneSupport.Compose(_hub.DeviceId, comp, settings.Devices.LianLi);

        if (ls.Mode == "custom")
        {
            // Raise OS timer resolution so the 5 ms inter-write settles are ~5 ms,
            // not the default ~15 ms. Held only while streaming; lowered on mode
            // switch or hub detach.
            if (!_highResTimer && OperatingSystem.IsWindows())
            {
                timeBeginPeriod(1);
                _highResTimer = true;
            }
            // Reset firmware sig so the next firmware-mode switch re-commits.
            _lastFirmwareSig = null;
            TickCustom(settings, devices, globalBrightness, composed);
            return;
        }

        if (_highResTimer && OperatingSystem.IsWindows())
        {
            timeEndPeriod(1);
            _highResTimer = false;
        }

        var mode = LianLiLightingModes.Find(ls.Mode);
        if (mode == null)
        {
            return;
        }

        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        _firmwareZonesByDevice.Clear();
        foreach (var device in composed)
        {
            _firmwareZonesByDevice.Add(ZoneResolution.Resolve(device.Structure, settings));
        }

        var sig = ComputeFirmwareSig(ls, globalBrightness, composed, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        if (_lastFirmwareSig.HasValue && sig == _lastFirmwareSig.Value)
        {
            return;
        }

        CommitFirmwareMode(ls, mode, globalBrightness, composed, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        _lastFirmwareSig = sig;
    }

    private void TickCustom(
        NexusSettings settings,
        DeviceFrame[] devices,
        float globalBrightness,
        List<ComposedDevice> composed)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var nowTicks = DateTime.UtcNow.Ticks;

        foreach (var device in composed)
        {
            var structure = device.Structure;
            var zones = ZoneResolution.Resolve(structure, settings);
            if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                // Every zone of this fan group is uncontrolled: leave its two
                // channels alone entirely rather than streaming a black frame.
                continue;
            }
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            for (var seg = 0; seg < structure.Segments.Count; seg++)
            {
                var buf = _segmentBuffers[seg];
                var byteCount = buf.Length * 3;
                for (var i = 0; i < buf.Length; i++)
                {
                    var c = buf[i];
                    var off = i * 3;
                    _channelBuf[off]     = c.R;
                    _channelBuf[off + 1] = c.G;
                    _channelBuf[off + 2] = c.B;
                }
                foreach (var ch in device.SegmentChannels[seg])
                {
                    var port = ch / 2;
                    _hub.SendStartAction(port, LianLiProtocol.MaxFansPerPort);
                    Thread.Sleep(InterWriteSettleMs);
                    _hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount));
                    Thread.Sleep(InterWriteSettleMs);
                    _hub.SendEffectCommit(ch);
                    Thread.Sleep(InterWriteSettleMs);
                }
            }
        }
    }

    private void CommitFirmwareMode(
        LianLiLightingSettings ls,
        LianLiModeInfo mode,
        float globalBrightness,
        List<ComposedDevice> composed,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        List<IReadOnlyList<ResolvedZone>> zonesByDevice)
    {
        var speedByte = LianLiLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte = LianLiLightingModes.DirectionByte(ls.Direction);

        for (var i = 0; i < composed.Count; i++)
        {
            var device = composed[i];
            if (ZoneResolution.IsFullyUncontrolled(zonesByDevice[i], uncontrolled))
            {
                // Every zone of this fan group is uncontrolled: skip its channel
                // commits entirely rather than committing at brightness zero.
                continue;
            }

            var numFans = NumFansForDevice(device);
            var brightnessByte = DeviceBrightnessByte(device, ls, globalBrightness, disabled);

            FillPaletteBuffer(_channelBuf, mode, ls.Colors, numFans);
            var byteCount = numFans * LianLiProtocol.LedsPerFanPerChannel * 3;

            for (var seg = 0; seg < device.SegmentChannels.Count; seg++)
            {
                foreach (var ch in device.SegmentChannels[seg])
                {
                    var port = ch / 2;
                    _hub.SendStartAction(port, numFans);
                    Thread.Sleep(InterWriteSettleMs);
                    _hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount));
                    Thread.Sleep(InterWriteSettleMs);
                    _hub.SendModeCommit(ch, mode.EffectByte, speedByte, dirByte, brightnessByte);
                    Thread.Sleep(InterWriteSettleMs);
                }
            }
        }
    }

    private static int NumFansForDevice(ComposedDevice device)
    {
        if (device.Structure.Segments.Count == 0)
        {
            return LianLiProtocol.MaxFansPerPort;
        }
        var fans = device.Structure.Segments[0].LedCount / LianLiProtocol.LedsPerFanPerChannel;
        return Math.Clamp(fans, 1, LianLiProtocol.MaxFansPerPort);
    }

    private static byte DeviceBrightnessByte(
        ComposedDevice device,
        LianLiLightingSettings ls,
        float globalBrightness,
        IReadOnlyList<string> disabled)
    {
        if (globalBrightness <= 0f)
        {
            return LianLiLightingModes.BrightnessCodes[0];
        }
        if (IsDeviceDisabled(device, disabled))
        {
            return LianLiLightingModes.BrightnessCodes[0];
        }
        var idx = (int)Math.Round(Math.Min((double)ls.Brightness, globalBrightness * 4.0));
        return LianLiLightingModes.BrightnessCodes[Math.Clamp(idx, 0, 4)];
    }

    private static bool IsDeviceDisabled(ComposedDevice device, IReadOnlyList<string> disabled)
    {
        var prefix = device.Structure.DeviceId + ":";
        var id = device.Structure.DeviceId;
        for (var i = 0; i < disabled.Count; i++)
        {
            var d = disabled[i];
            if (d == id || d.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // Replicates OpenRGB SetChannelMode's fan_led_data fill.
    // 6 colors: each fills one fan slot. Fewer: resize to 4, interleaved across fans.
    // Input colors are RGB; SendColorData applies the R,B,G wire swap.
    private static void FillPaletteBuffer(byte[] buf, LianLiModeInfo mode, IReadOnlyList<string> colors, int numFans)
    {
        Array.Clear(buf);
        if (mode.ColorsMax == 0 || colors.Count == 0)
        {
            return;
        }

        var count = Math.Min(colors.Count, mode.ColorsMax);
        var parsed = new (byte r, byte g, byte b)[count];
        for (var i = 0; i < count; i++)
        {
            parsed[i] = ParseHexColor(colors[i]);
        }

        if (count == 6)
        {
            // Fill each fan slot with its corresponding color.
            for (var fanIdx = 0; fanIdx < numFans && fanIdx < count; fanIdx++)
            {
                var (r, g, b) = parsed[fanIdx];
                for (var led = 0; led < LianLiProtocol.LedsPerFanPerChannel; led++)
                {
                    var off = (fanIdx * LianLiProtocol.LedsPerFanPerChannel + led) * 3;
                    if (off + 2 < buf.Length)
                    {
                        buf[off]     = r;
                        buf[off + 1] = g;
                        buf[off + 2] = b;
                    }
                }
            }
        }
        else
        {
            // Resize to 4 slots (pad with black). Interleaved: j=color, i=fan.
            // Position = i * 12 + j * 3.
            var slots = new (byte r, byte g, byte b)[4];
            for (var i = 0; i < count && i < 4; i++)
            {
                slots[i] = parsed[i];
            }
            for (var j = 0; j < 4; j++)
            {
                var (r, g, b) = slots[j];
                for (var i = 0; i < numFans; i++)
                {
                    var off = i * 12 + j * 3;
                    if (off + 2 < buf.Length)
                    {
                        buf[off]     = r;
                        buf[off + 1] = g;
                        buf[off + 2] = b;
                    }
                }
            }
        }
    }

    private static (byte r, byte g, byte b) ParseHexColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length < 6)
        {
            return (0, 0, 0);
        }
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)) { r = 0; }
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)) { g = 0; }
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b)) { b = 0; }
        return (r, g, b);
    }

    private static int ComputeFirmwareSig(
        LianLiLightingSettings ls,
        float globalBrightness,
        List<ComposedDevice> composed,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        List<IReadOnlyList<ResolvedZone>> zonesByDevice)
    {
        var hc = new HashCode();
        hc.Add(ls.Mode);
        hc.Add(ls.Speed);
        hc.Add(ls.Direction);
        foreach (var c in ls.Colors)
        {
            hc.Add(c);
        }
        for (var i = 0; i < composed.Count; i++)
        {
            var device = composed[i];
            var skipped = ZoneResolution.IsFullyUncontrolled(zonesByDevice[i], uncontrolled);
            hc.Add(skipped);
            if (skipped)
            {
                continue;
            }
            hc.Add(NumFansForDevice(device));
            var brightnessByte = DeviceBrightnessByte(device, ls, globalBrightness, disabled);
            for (var seg = 0; seg < device.SegmentChannels.Count; seg++)
            {
                foreach (var ch in device.SegmentChannels[seg])
                {
                    hc.Add(ch);
                    hc.Add(brightnessByte);
                }
            }
        }
        return hc.ToHashCode();
    }
}
