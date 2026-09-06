using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Cnvs;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using CnvsColor = Nexus.Service.Peripherals.Hyte.Cnvs.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the CNVS mat. Same pattern as
/// <see cref="MiniHubLightingFrameWriter"/>: own 30 Hz timer so disabled
/// or effect-less zones still receive blank frames; honour brightness /
/// disabled / identify from the shared settings store; one
/// <see cref="CnvsHub.WriteLighting"/> per tick.
///
/// Firmware animation: on each new hub connection we send
/// <see cref="CnvsHub.SetFirmwareAnimationOff"/> once, mirroring
/// CNVSBaseController.SendToHardware's "if firmware animation is on,
/// turn it off before streaming" guard. Without that handshake the
/// firmware overlays its boot animation on top of our streamed
/// colors and the mat looks like a rainbow even when we're sending
/// solid black.
/// </summary>
public sealed class CnvsLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly CnvsHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private CnvsColor[]? _buffer;
    // Tracks the hub connection generation so we re-send the firmware-anim-off
    // handshake after a reconnect / device swap. Compare against the hub's
    // current Serial each tick; on change, the next tick re-fires the
    // handshake before streaming.
    private string _lastConnectedSerial = "";
    private bool _fwAnimSilenced;

    private readonly FeatureGates _gates;

    public CnvsLightingFrameWriter(LightingEngine engine, CnvsHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
            { ServiceLog.Error($"[cnvs-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            _fwAnimSilenced = false;
            _lastConnectedSerial = "";
            return;
        }

        // Gate: the firmware drops FF DC 07 (SetSettings) once any FF DC 05
        // has been sent since USB connect, so CnvsConnectionWorker has to be
        // the very first thing on the wire after a connect. Skip our tick
        // entirely until the worker has applied settings - IsReadyForStreaming
        // is the explicit signal it flips when WriteSettings completes.
        if (!_hub.IsReadyForStreaming) return;

        var settings = _store.Load();

        // Uncontrolled: skip the firmware-anim-off handshake too, so the mat's
        // own animation stays live instead of getting silenced for a stream
        // that never comes.
        if (settings.Devices.UncontrolledLightingDevices.Contains(_hub.DeviceId)) return;

        // Detect (re)connect: a new serial means the hub was reopened; we
        // need to re-send the firmware-anim-off handshake before our first
        // streamed frame, or the firmware overlay sits on top of it.
        var currentSerial = _hub.Serial;
        if (currentSerial != _lastConnectedSerial)
        {
            _lastConnectedSerial = currentSerial;
            _fwAnimSilenced = false;
        }

        if (!_fwAnimSilenced)
        {
            if (_hub.SetFirmwareAnimationOff())
            {
                _fwAnimSilenced = true;
                ServiceLog.Info($"[cnvs-lighting-writer] firmware animation silenced on {currentSerial}");
            }
        }

        var devices = _engine.Devices;
        if (devices.Length == 0)
        {
            // Engine hasn't built device list yet (RgbBridge.RefreshDevicesAsync
            // hasn't ticked); push a blank frame so the mat shows a steady
            // black rather than the firmware default. Cheap - 157-byte frame
            // over USB-CDC at 115200 baud is well under a millisecond.
            PushBlank();
            return;
        }

        // CNVS is exposed by CnvsLightingDeviceProvider as a single
        // standalone card with id = hub DeviceId (no ":mat" zone suffix).
        var id = _hub.DeviceId;
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }

        if (frame is null)
        {
            // No CNVS frame in the engine yet (lighting page never opened?).
            // Still keep the mat alive with a blank push so the firmware
            // doesn't re-enable its boot animation between user actions.
            PushBlank();
            return;
        }

        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var brightnessMul = ComputeBrightnessMul(id, disabled, prefs, globalBrightness, out var adjust);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var ledCount = Math.Min(frame.LedCount, CnvsHub.LedCount);
        EnsureCapacity(CnvsHub.LedCount);
        var dst = _buffer!;
        FillBufferSlice(dst, frame.LedBytes, ledCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
        // Any LEDs beyond the engine-frame count: zero so the mat is fully
        // owned by us, no firmware residue.
        for (var i = ledCount; i < CnvsHub.LedCount; i++) dst[i] = default;

        _hub.WriteLighting(new ReadOnlySpan<CnvsColor>(dst, 0, CnvsHub.LedCount));
    }

    private void PushBlank()
    {
        EnsureCapacity(CnvsHub.LedCount);
        var dst = _buffer!;
        for (var i = 0; i < CnvsHub.LedCount; i++) dst[i] = default;
        _hub.WriteLighting(new ReadOnlySpan<CnvsColor>(dst, 0, CnvsHub.LedCount));
    }

    private void EnsureCapacity(int n)
    {
        if (_buffer is null || _buffer.Length < n) _buffer = new CnvsColor[Math.Max(n, CnvsHub.LedCount)];
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
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

    private static void FillBufferSlice(CnvsColor[] dst, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new CnvsColor(255, 255, 255) : new CnvsColor(0, 0, 0);
            for (var i = 0; i < ledCount && i < dst.Length; i++) dst[i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && i < dst.Length; i++) dst[i] = default;
            return;
        }
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount && i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[i] = new CnvsColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[i] = new CnvsColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[i] = new CnvsColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
