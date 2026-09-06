using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using QColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the HYTE Q-series cooler. Same pattern as
/// <see cref="CnvsLightingFrameWriter"/>: own 30 Hz timer so disabled / effect-less
/// zones still receive blank frames; honour brightness / disabled / identify from
/// the shared settings store. One <see cref="QSeriesCoolerHub.WriteLighting"/> call
/// per tick for the Panel + Logo card (ports 3/4), plus one
/// <see cref="QSeriesCoolerHub.WriteLinkLighting"/> call per Nexus Link channel
/// (ports 1/2), concatenating that channel's device frames in slot order.
///
/// Unlike CNVS there's no firmware-animation-off handshake: putting the cooler in
/// software RGB control mode (asserted inside the hub's WriteLighting on the first
/// frame after each connect) already suppresses the firmware animation.
/// </summary>
public sealed class QSeriesLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly QSeriesCoolerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private QColor[]? _buffer;
    // True once the LEDs have been handed back to the firmware; latches the
    // release so it costs one write on the transition, not one per 30 Hz tick.
    private bool _releasedToFirmware;
    // Device id the release was latched against; a reconnect clears the latch so a
    // cooler that came back in software control is released again.
    private string _releasedForDeviceId = "";
    // Cached id-prefixes for classifying engine frames by Nexus Link channel, rebuilt
    // only when the hub id changes rather than allocated fresh on every 30 Hz tick.
    private string _prefixedForDeviceId = "";
    private string _link1Prefix = "";
    private string _link2Prefix = "";

    private readonly FeatureGates _gates;

    public QSeriesLightingFrameWriter(LightingEngine engine, QSeriesCoolerHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            { Console.Error.WriteLine($"[qseries-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsReadyForStreaming) return;

        var settings = _store.Load();
        var id = _hub.DeviceId;
        if (settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            // Uncontrolled means the firmware owns every port's LEDs (RGB control is
            // hub-wide). Merely not pushing leaves the cooler in software RGB control
            // on its last frame, which suppresses the standalone animation the
            // setting exists to restore.
            if (!string.Equals(_releasedForDeviceId, id, StringComparison.Ordinal)) _releasedToFirmware = false;
            if (!_releasedToFirmware && _hub.ReleaseRgbControlToFirmware())
            {
                _releasedToFirmware = true;
                _releasedForDeviceId = id;
            }
            return;
        }
        _releasedToFirmware = false;

        if (!string.Equals(_prefixedForDeviceId, id, StringComparison.Ordinal))
        {
            _prefixedForDeviceId = id;
            _link1Prefix = $"{id}:p{QSeriesCoolerProtocol.LinkChannel1}:";
            _link2Prefix = $"{id}:p{QSeriesCoolerProtocol.FanChannel}:";
        }

        var devices = _engine.Devices;
        DeviceFrame? panelFrame = null;
        _link1.Clear();
        _link2.Clear();
        for (var i = 0; i < devices.Length; i++)
        {
            var dev = devices[i];
            if (dev.Id == id) { panelFrame = dev; continue; }
            if (dev.Id.StartsWith(_link1Prefix, StringComparison.Ordinal)) _link1.Add(dev);
            else if (dev.Id.StartsWith(_link2Prefix, StringComparison.Ordinal)) _link2.Add(dev);
        }

        // Snapshot once: State.Channel{1,2}Devices publishes a fresh list per poll, so one
        // read per channel gives a stable firmware-LED-count lookup for this whole tick.
        var channel1State = _hub.State.Channel1Devices;
        var channel2State = _hub.State.Channel2Devices;

        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        if (panelFrame is null)
        {
            // No Q-series panel frame in the engine yet (lighting page never opened,
            // or RgbBridge hasn't rebuilt since connect). Keep the LEDs owned by us
            // with a blank push so the firmware can't re-assert its boot animation.
            PushBlankPanel();
        }
        else
        {
            var brightnessMul = ComputeBrightnessMul(id, disabled, prefs, globalBrightness, out var adjust);
            var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);
            var ledCount = Math.Min(panelFrame.LedCount, QSeriesCoolerHub.LedCount);
            EnsureCapacity(ref _buffer, QSeriesCoolerHub.LedCount);
            var dst = _buffer!;
            FillBufferSlice(dst, 0, panelFrame.LedBytes, ledCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
            for (var i = ledCount; i < QSeriesCoolerHub.LedCount; i++) dst[i] = default;
            _hub.WriteLighting(new ReadOnlySpan<QColor>(dst, 0, QSeriesCoolerHub.LedCount));
        }

        PushLinkChannel(QSeriesCoolerProtocol.LinkChannel1, _link1, ref _link1Buffer, channel1State, disabled, prefs, globalBrightness, nowTicks);
        PushLinkChannel(QSeriesCoolerProtocol.FanChannel, _link2, ref _link2Buffer, channel2State, disabled, prefs, globalBrightness, nowTicks);
    }

    private void PushBlankPanel()
    {
        EnsureCapacity(ref _buffer, QSeriesCoolerHub.LedCount);
        var dst = _buffer!;
        for (var i = 0; i < QSeriesCoolerHub.LedCount; i++) dst[i] = default;
        _hub.WriteLighting(new ReadOnlySpan<QColor>(dst, 0, QSeriesCoolerHub.LedCount));
    }

    // Per-tick device lists for the two Nexus Link channels, cleared and refilled each Tick.
    private readonly List<DeviceFrame> _link1 = new();
    private readonly List<DeviceFrame> _link2 = new();
    private QColor[]? _link1Buffer;
    private QColor[]? _link2Buffer;

    // Floor for a reused link buffer so a 1-2 LED chain doesn't force a reallocation
    // the moment it grows by one device.
    private const int MinLinkBufferCapacity = 64;

    /// <summary>
    /// Streams one Nexus Link channel's devices into its port frame, slot order, device 0
    /// first. Each device occupies a fixed-width block sized to its FIRMWARE LED count
    /// (from <paramref name="channelState"/>), not the frame's possibly-trimmed LedCount -
    /// a ZoneLedCounts trim must shorten what lights up, not shift every device after it.
    /// </summary>
    private void PushLinkChannel(int channel, List<DeviceFrame> chainDevices, ref QColor[]? buffer,
        IReadOnlyList<QSeriesLinkDevice> channelState,
        IReadOnlyList<string> disabled, IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var total = 0;
        foreach (var d in chainDevices) total += FirmwareLedCountOf(channelState, d.Id, d.LedCount);
        if (total == 0)
        {
            _hub.WriteLinkLighting(channel, ReadOnlySpan<QColor>.Empty);
            return;
        }
        EnsureCapacity(ref buffer, total);
        var dst = buffer!;
        var offset = 0;
        foreach (var dev in chainDevices)
        {
            var blockWidth = FirmwareLedCountOf(channelState, dev.Id, dev.LedCount);
            var brightnessMul = ComputeBrightnessMul(dev.Id, disabled, prefs, globalBrightness, out var adjust);
            var hasIdentify = _identify.TryGetActive(dev.Id, nowTicks, out var startTicks);
            var fillCount = Math.Min(dev.LedCount, blockWidth);
            FillBufferSlice(dst, offset, dev.LedBytes, fillCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
            for (var i = fillCount; i < blockWidth && offset + i < dst.Length; i++) dst[offset + i] = default;
            offset += blockWidth;
        }
        _hub.WriteLinkLighting(channel, new ReadOnlySpan<QColor>(dst, 0, total));
    }

    // frameId shape: "<hubId>:p<channel>:<slot>". Falls back to fallbackLedCount when the
    // slot isn't found in channelState (topology changed since BuildFrames last ran).
    private static int FirmwareLedCountOf(IReadOnlyList<QSeriesLinkDevice> channelState, string frameId, int fallbackLedCount)
    {
        var lastColon = frameId.LastIndexOf(':');
        if (lastColon < 0 || !int.TryParse(frameId.AsSpan(lastColon + 1), out var slot)) return fallbackLedCount;
        foreach (var d in channelState)
        {
            if (d.Slot == slot) return d.LedCount;
        }
        return fallbackLedCount;
    }

    private static void EnsureCapacity(ref QColor[]? buffer, int n)
    {
        if (buffer is null || buffer.Length < n) buffer = new QColor[Math.Max(n, MinLinkBufferCapacity)];
    }

    private static double ComputeBrightnessMul(string id,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
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

    private static void FillBufferSlice(QColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new QColor(255, 255, 255) : new QColor(0, 0, 0);
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = default;
            return;
        }
        // src is RGB triples (engine order); QColor(R,G,B) - the hub emits GRB on the wire.
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[dstStart + i] = new QColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new QColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new QColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
