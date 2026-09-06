using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes engine output to every attached Nollie channel on its own 30 Hz
/// timer, so a disabled channel still receives blank frames rather than holding
/// whatever the firmware last drove. A channel with no declared count sends
/// nothing and stays dark.
/// </summary>
public sealed class NollieLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly NollieHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Scratch RGB buffer per card id; grown on demand, reused every tick.</summary>
    private readonly Dictionary<string, byte[]> _buffers = new(StringComparer.Ordinal);

    public NollieLightingFrameWriter(LightingEngine engine, NollieHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            { Console.Error.WriteLine($"[nollie-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        var controllers = _hub.Controllers;
        if (controllers.Count == 0) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var counts = settings.Devices.ZoneLedCounts;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        foreach (var controller in controllers)
        {
            // Every channel of this controller left uncontrolled: don't touch it
            // at all, so it keeps whatever its firmware is doing.
            if (uncontrolled.Count > 0 && AllChannelsUncontrolled(controller, uncontrolled))
            {
                continue;
            }

            var wrote = false;
            foreach (var ch in PushOrder(controller, counts))
            {
                var id = NollieLightingDeviceProvider.ChannelId(controller.DeviceId, ch);
                var ledCount = NollieLightingDeviceProvider.DeclaredLedCount(counts, id, controller.Spec);

                var frame = FindFrame(devices, id);
                var buf = Rent(id, Math.Max(ledCount, 1) * 3);
                var brightnessMul = ComputeBrightnessMul(id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
                var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);
                Fill(buf, ledCount, frame, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);

                if (controller.SendChannel(ch, new ReadOnlySpan<byte>(buf, 0, ledCount * 3)))
                {
                    wrote = true;
                }
            }

            // Chunked controllers only apply once latched; the wide transport
            // takes effect on receipt and ignores this.
            if (wrote) controller.SendLatch();
        }
    }

    /// <summary>
    /// Cards to push this tick. The reference driver sends configured channels
    /// plus the wide transport's flag channels (even at zero LEDs, so the
    /// marker still delimits the update) in ascending HARDWARE channel order;
    /// the chunked path sends configured channels in card order and latches.
    /// </summary>
    private static List<int> PushOrder(NollieController controller, IReadOnlyDictionary<string, int> counts)
    {
        var spec = controller.Spec;
        var order = new List<int>(spec.Channels);
        for (var ch = 0; ch < spec.Channels; ch++)
        {
            var id = NollieLightingDeviceProvider.ChannelId(controller.DeviceId, ch);
            var configured = NollieLightingDeviceProvider.DeclaredLedCount(counts, id, spec) > 0;
            if (configured || NollieProtocol.IsFlagChannel(spec, spec.HardwareChannel(ch)))
            {
                order.Add(ch);
            }
        }
        if (spec.Transport == NollieTransport.Wide)
        {
            order.Sort((a, b) => spec.HardwareChannel(a).CompareTo(spec.HardwareChannel(b)));
        }
        return order;
    }

    private static bool AllChannelsUncontrolled(NollieController controller, IReadOnlyList<string> uncontrolled)
    {
        for (var ch = 0; ch < controller.Spec.Channels; ch++)
        {
            if (!uncontrolled.Contains(NollieLightingDeviceProvider.ChannelId(controller.DeviceId, ch)))
            {
                return false;
            }
        }
        return true;
    }

    private static DeviceFrame? FindFrame(DeviceFrame[] devices, string id)
    {
        for (var i = 0; i < devices.Length; i++)
        {
            if (devices[i].Id == id) return devices[i];
        }
        return null;
    }

    private byte[] Rent(string id, int byteLength)
    {
        if (!_buffers.TryGetValue(id, out var buf) || buf.Length < byteLength)
        {
            buf = new byte[Math.Max(byteLength, 64 * 3)];
            _buffers[id] = buf;
        }
        return buf;
    }

    private static double ComputeBrightnessMul(string id,
        IReadOnlyList<string> disabled, IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs, float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        if (uncontrolled.Count > 0)
        {
            foreach (var u in uncontrolled) if (u == id) return 0.0;
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

    private static void Fill(byte[] dst, int ledCount, DeviceFrame? frame,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        Array.Clear(dst, 0, Math.Min(dst.Length, ledCount * 3));

        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var v = on ? (byte)255 : (byte)0;
            for (var i = 0; i < ledCount * 3 && i < dst.Length; i++) dst[i] = v;
            return;
        }
        if (frame is null || brightnessMul <= 0.0) return;

        var src = frame.LedBytes;
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || off + 2 >= dst.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[off] = ar;
                dst[off + 1] = ag;
                dst[off + 2] = ab;
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || off + 2 >= dst.Length) break;
                dst[off] = src[off];
                dst[off + 1] = src[off + 1];
                dst[off + 2] = src[off + 2];
            }
            return;
        }
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length || off + 2 >= dst.Length) break;
            dst[off] = (byte)(src[off] * brightnessMul);
            dst[off + 1] = (byte)(src[off + 1] * brightnessMul);
            dst[off + 2] = (byte)(src[off + 2] * brightnessMul);
        }
    }
}
