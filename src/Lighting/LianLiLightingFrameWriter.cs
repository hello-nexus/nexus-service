using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
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

    // Settle between HID writes. At 5 ms the six writes of a custom-mode frame
    // cost 30 ms of pure sleep against a 33 ms tick, capping the stream well
    // under 30 Hz. Measured on fw 1.4 (2026-08-27): 60 back-to-back frames of
    // the full cycle over the control pipe took 312 ms total (5.2 ms/frame) with
    // zero failed writes and no pacing at all, so the old 5 ms was sized for the
    // interrupt-OUT path this no longer uses. 1 ms keeps a yield between writes
    // while leaving headroom for a real 30 Hz.
    private const int InterWriteSettleMs = 1;

    // Hub writes are synchronous HidD_SetFeature, each taking LianLiHub._lock that
    // fan control also takes, so a rejected commit backs off rather than
    // re-acquiring it once per channel write every tick against a silent hub.
    private const int RetryBaseMs = 1000;
    private const int RetryMaxMs = 30_000;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private readonly LightingEngine _engine;
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    // Last firmware-mode signature the hardware accepted.
    private int? _lastFirmwareSig;

    // Signature currently being attempted, so a retry is told apart from a settings change.
    private int? _pendingFirmwareSig;

    // Independent windows: a hub that rejects its attach commands must not gate
    // custom-mode streaming, which does not depend on them landing.
    private readonly RetryWindow _initRetry = new();
    private readonly RetryWindow _commitRetry = new();

    // False until the attach-time commands (merge off, per-port quantity) have
    // gone out for the current connection; families with a per-frame start
    // carry the quantity in every frame instead.
    private bool _hubInitialised;

    // Per-device resolved zones for the firmware-mode sig/commit pair, reused
    // each tick so they resolve once instead of once per call site.
    private readonly List<IReadOnlyList<ResolvedZone>> _firmwareZonesByDevice = new();

    // Raw RGB scratch for one channel, sized for the largest per-fan ring across families.
    private readonly byte[] _channelBuf =
        new byte[LianLiProtocol.MaxFansPerPort * LianLiProtocol.MaxLedsPerFanPerChannel * 3];

    // True while timeBeginPeriod(1) is active; matches the custom-streaming lifetime.
    private bool _highResTimer;

    private readonly FeatureGates _gates;

    public LianLiLightingFrameWriter(LightingEngine engine, LianLiHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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

    internal void Tick()
    {
        if (!_gates.Lighting) return;
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
            _pendingFirmwareSig = null;
            _hubInitialised = false;
            _initRetry.Reset();
            _commitRetry.Reset();
            return;
        }
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var ls = settings.Devices.LianLiLighting;
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);

        var profile = _hub.Profile;
        // A rejected init is retried on its own window but never aborts the tick:
        // pre-retry this was fire-and-forget, and custom mode streams without it.
        if (!_hubInitialised && !_initRetry.BackingOff(NowMs()))
        {
            if (InitialiseHub(profile, settings.Devices.LianLi))
            {
                NoteSuccess(_initRetry, "hub init");
                _hubInitialised = true;
            }
            else
            {
                NoteFailure(_initRetry, "hub init");
            }
        }
        var comp = LianLiZoneSupport.ReadComposition(settings, _hub.DeviceId);
        var composed = LianLiZoneSupport.Compose(_hub.DeviceId, profile, comp, settings.Devices.LianLi);

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
            // Reset firmware sig so the next firmware-mode switch re-commits,
            // and its backoff with it so a stale deadline cannot gate that switch.
            _lastFirmwareSig = null;
            _pendingFirmwareSig = null;
            _commitRetry.Reset();
            TickCustom(settings, devices, globalBrightness, composed, profile);
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
        if (!mode.SupportedBy(profile.Family))
        {
            // A mode persisted for another family (the route rejects new ones);
            // static is the one commit every Uni hub accepts.
            mode = LianLiLightingModes.Find("static")!;
        }

        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        _firmwareZonesByDevice.Clear();
        foreach (var device in composed)
        {
            _firmwareZonesByDevice.Add(ZoneResolution.Resolve(device.Structure, settings));
        }

        var sig = ComputeFirmwareSig(ls, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        if (_lastFirmwareSig.HasValue && sig == _lastFirmwareSig.Value)
        {
            return;
        }

        if (_pendingFirmwareSig != sig)
        {
            // New settings, not a retry: apply without serving out the old backoff.
            _pendingFirmwareSig = sig;
            _commitRetry.Reset();
        }
        else if (_commitRetry.BackingOff(NowMs()))
        {
            return;
        }

        // One merged animation cannot honour per-port control, so any excluded port keeps the per-port commit.
        var merged = ls.Merge && mode.MergesOn(profile)
            && !AnyDeviceExcluded(composed, settings);
        var committed = merged
            ? CommitMergedMode(ls, mode, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, settings.Devices.LianLi)
            : CommitFirmwareMode(ls, mode, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        if (committed)
        {
            NoteSuccess(_commitRetry, "firmware commit");
            _lastFirmwareSig = sig;
        }
        else
        {
            NoteFailure(_commitRetry, "firmware commit");
        }
    }

    /// <summary>Test seam: tests advance the backoff clock instead of sleeping out the cap.</summary>
    internal Func<long> NowMs { get; set; } = static () => Environment.TickCount64;

    /// <summary>Widening retry window in milliseconds: RetryBaseMs doubling per failure, capped at RetryMaxMs.</summary>
    private sealed class RetryWindow
    {
        private int _streak;
        private long _retryAtMs;

        public int Streak => _streak;

        public bool BackingOff(long nowMs) => _streak > 0 && nowMs < _retryAtMs;

        public void Fail(long nowMs)
        {
            _streak++;
            // Shift clamp is an overflow bound only; RetryMaxMs is the one cap.
            _retryAtMs = nowMs + Math.Min((long)RetryBaseMs << Math.Min(_streak - 1, 30), RetryMaxMs);
        }

        public void Reset()
        {
            _streak = 0;
            _retryAtMs = 0;
        }
    }

    /// <summary>Logs once per streak so a hub that never answers cannot fill the log at tick rate.</summary>
    private void NoteFailure(RetryWindow window, string what)
    {
        window.Fail(NowMs());
        if (window.Streak == 1)
        {
            Console.Error.WriteLine(
                $"[lianli-lighting-writer] {what} rejected by the hub; retrying with backoff (max {RetryMaxMs / 1000}s)");
        }
    }

    private void NoteSuccess(RetryWindow window, string what)
    {
        if (window.Streak > 0)
        {
            Console.WriteLine(
                $"[lianli-lighting-writer] {what} applied after {window.Streak} rejected attempt(s)");
            window.Reset();
        }
    }

    /// <summary>Attach-time commands; returns false on the first rejected write so the caller retries the whole sequence.</summary>
    private bool InitialiseHub(in LianLiFanProfile profile, LianLiSettings fans)
    {
        if (profile.ClearMergeOnAttach)
        {
            if (!_hub.SendStopMerge()) return false;
            Thread.Sleep(InterWriteSettleMs);
        }
        if (profile.StartActionPerFrame) return true;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            if (!_hub.SetQuantity(p, LianLiZoneSupport.ClampFans(fans.GetFans(p)))) return false;
            Thread.Sleep(InterWriteSettleMs);
        }
        return true;
    }

    private void TickCustom(
        NexusSettings settings,
        DeviceFrame[] devices,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile)
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
                    if (profile.StartActionPerFrame)
                    {
                        _hub.SendStartAction(ch / profile.ChannelsPerPort, LianLiProtocol.MaxFansPerPort);
                        Thread.Sleep(InterWriteSettleMs);
                    }
                    _hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount));
                    Thread.Sleep(InterWriteSettleMs);
                    _hub.SendEffectCommit(ch);
                    Thread.Sleep(InterWriteSettleMs);
                }
            }

            // Latch PER DEVICE, not once per tick. The sync applies the frame for
            // the port it follows; with a single port a trailing sync looked
            // equivalent, but as soon as a second port is populated only the
            // last-addressed one latched and the other fell back to the
            // firmware's ~0.6 Hz internal repaint - the whole rig then reads as
            // roughly 1 Hz once a fan is moved to a second port.
            _hub.SendFrameSync();
            Thread.Sleep(InterWriteSettleMs);
        }
    }

    /// <summary>Commits the firmware animation on every controlled channel; returns false on the first rejected write, leaving the signature unlatched so the next attempt re-sends every channel.</summary>
    private bool CommitFirmwareMode(
        LianLiLightingSettings ls,
        LianLiModeInfo mode,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile,
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

            var numFans = NumFansForDevice(device, profile);
            var brightnessByte = DeviceBrightnessByte(device, ls, globalBrightness, disabled);

            for (var seg = 0; seg < device.SegmentChannels.Count; seg++)
            {
                foreach (var ch in device.SegmentChannels[seg])
                {
                    // Inner and outer rings hold different per-fan counts, so the
                    // palette is refilled per channel rather than once per device.
                    var ledsPerFan = profile.LedsPerFanForChannel(ch);
                    var perFan = profile.PerFanStaticPalette && mode.EffectByte is LianLiProtocol.EffectStatic or LianLiProtocol.EffectBreathing;
                    FillPaletteBuffer(_channelBuf, mode, ls.Colors, numFans, ledsPerFan, perFan);
                    var byteCount = numFans * ledsPerFan * 3;
                    if (profile.StartActionPerFrame)
                    {
                        if (!_hub.SendStartAction(ch / profile.ChannelsPerPort, numFans)) return false;
                        Thread.Sleep(InterWriteSettleMs);
                    }
                    if (!_hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount))) return false;
                    Thread.Sleep(InterWriteSettleMs);
                    if (!_hub.SendModeCommit(ch, mode.EffectByte, speedByte, dirByte, brightnessByte)) return false;
                    Thread.Sleep(InterWriteSettleMs);
                }
            }
        }

        // The per-channel commits alone leave the firmware rendering the
        // previous speed/brightness; this latches them. Once for the whole
        // apply, after every port, exactly as L-Connect does.
        if (!_hub.SendFrameSync()) return false;
        Thread.Sleep(InterWriteSettleMs);
        return true;
    }

    /// <summary>
    /// One animation across every port. Firmware sequence: port order, each
    /// port's fan count, every channel but 0 parked dark, then the palette and
    /// merged effect on channel 0, with no frame sync after. Returns false on
    /// the first rejected write.
    /// </summary>
    private bool CommitMergedMode(
        LianLiLightingSettings ls,
        LianLiModeInfo mode,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile,
        IReadOnlyList<string> disabled,
        LianLiSettings fans)
    {
        if (composed.Count == 0) return true;
        var anchor = composed[0];
        foreach (var device in composed)
        {
            if (CarriesChannel(device, 0))
            {
                anchor = device;
                break;
            }
        }

        var speedByte = LianLiLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte = LianLiLightingModes.DirectionByte(ls.Direction);
        var brightnessByte = DeviceBrightnessByte(anchor, ls, globalBrightness, disabled);

        if (!_hub.SendMergeOrder()) return false;
        Thread.Sleep(InterWriteSettleMs);
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            if (!_hub.SetQuantity(p, LianLiZoneSupport.ClampFans(fans.GetFans(p)))) return false;
            Thread.Sleep(InterWriteSettleMs);
        }
        for (var ch = LianLiProtocol.PortCount * profile.ChannelsPerPort - 1; ch >= 1; ch--)
        {
            var off = LianLiLightingModes.BrightnessCodes[0];
            if (!_hub.SendModeCommit(ch, LianLiProtocol.EffectMergeIdle, LianLiProtocol.SpeedDefault, LianLiProtocol.DirectionDefault, off)) return false;
            Thread.Sleep(InterWriteSettleMs);
        }
        FillPaletteBuffer(_channelBuf, mode, ls.Colors, LianLiProtocol.MaxFansPerPort, profile.LedsPerFanForChannel(0), perFan: false);
        if (!_hub.SendColorData(0, _channelBuf.AsSpan(0, LianLiProtocol.MergedPaletteBytes))) return false;
        Thread.Sleep(InterWriteSettleMs);
        if (!_hub.SendModeCommit(0, mode.MergedEffectByte, speedByte, dirByte, brightnessByte)) return false;
        Thread.Sleep(InterWriteSettleMs);
        return true;
    }

    /// <summary>True when a fan group is disabled or fully uncontrolled; the lighting route hides Merge on the same test.</summary>
    internal static bool AnyDeviceExcluded(List<ComposedDevice> composed, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        foreach (var device in composed)
        {
            if (IsDeviceDisabled(device, disabled)
                || ZoneResolution.IsFullyUncontrolled(ZoneResolution.Resolve(device.Structure, settings), uncontrolled))
            {
                return true;
            }
        }
        return false;
    }

    private static bool CarriesChannel(ComposedDevice device, int channel)
    {
        foreach (var channels in device.SegmentChannels)
        {
            foreach (var ch in channels)
            {
                if (ch == channel) return true;
            }
        }
        return false;
    }

    private static int NumFansForDevice(ComposedDevice device, in LianLiFanProfile profile)
    {
        if (device.Structure.Segments.Count == 0 || profile.InnerLedsPerFan <= 0)
        {
            return LianLiProtocol.MaxFansPerPort;
        }
        // Segment 0 is the inner (or only) ring, so its count divides by that per-fan value.
        var fans = device.Structure.Segments[0].LedCount / profile.InnerLedsPerFan;
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

    // 6 colors: each fills one fan slot. Fewer: resize to 4, interleaved across fans.
    // perFan: colour i fills fan i's whole ring (cycling when fewer colours than fans).
    // Input colors are RGB; SendColorData applies the R,B,G wire swap.
    private static void FillPaletteBuffer(byte[] buf, LianLiModeInfo mode, IReadOnlyList<string> colors, int numFans, int ledsPerFan, bool perFan)
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

        if (perFan)
        {
            for (var fanIdx = 0; fanIdx < numFans; fanIdx++)
            {
                var (r, g, b) = parsed[fanIdx % count];
                for (var led = 0; led < ledsPerFan; led++)
                {
                    var off = (fanIdx * ledsPerFan + led) * 3;
                    if (off + 2 < buf.Length)
                    {
                        buf[off]     = r;
                        buf[off + 1] = g;
                        buf[off + 2] = b;
                    }
                }
            }
            return;
        }

        if (count == 6)
        {
            // Fill each fan slot with its corresponding color.
            for (var fanIdx = 0; fanIdx < numFans && fanIdx < count; fanIdx++)
            {
                var (r, g, b) = parsed[fanIdx];
                for (var led = 0; led < ledsPerFan; led++)
                {
                    var off = (fanIdx * ledsPerFan + led) * 3;
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
        in LianLiFanProfile profile,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        List<IReadOnlyList<ResolvedZone>> zonesByDevice)
    {
        var hc = new HashCode();
        hc.Add(ls.Mode);
        hc.Add(ls.Speed);
        hc.Add(ls.Direction);
        hc.Add(ls.Merge);
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
            hc.Add(NumFansForDevice(device, profile));
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
