using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes engine output to every attached Nollie channel on its own 30 Hz
/// timer, so a disabled channel still receives blank frames rather than holding
/// whatever the firmware last drove. A channel with no declared count sends
/// nothing and stays dark. A port's buffer is tiled per zone, then cut into
/// lanes: one channel for a plain header, six for a Strimer connector.
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

    // Last resolved zones per port, keyed by port id. Resolve reads
    // ZonePartitions/PortChains/ZoneLedCounts lock-free while routes mutate
    // them in place, so a mid-enumeration InvalidOperationException falls
    // back to last tick's zones rather than dropping the frame.
    private readonly Dictionary<string, IReadOnlyList<ResolvedZone>> _zoneCache = new(StringComparer.Ordinal);

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

    internal void Tick()
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
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
        var nowTicks = DateTime.UtcNow.Ticks;

        foreach (var controller in controllers)
        {
            // Resolved once per port this tick, reused for the uncontrolled
            // check and the push loop below.
            var ports = controller.Spec.Ports;
            var portZones = new IReadOnlyList<ResolvedZone>[ports.Count];
            for (var i = 0; i < portZones.Length; i++)
            {
                var port = ports[i];
                portZones[i] = ResolveOrReuse(NollieLightingDeviceProvider.PortId(controller.DeviceId, port),
                    () => NollieLightingDeviceProvider.ResolvePortZones(controller, port, settings));
            }

            // Every port of this controller left uncontrolled: don't touch it
            // at all, so it keeps whatever its firmware is doing.
            if (uncontrolled.Count > 0 && AllPortsUncontrolled(portZones, uncontrolled))
            {
                continue;
            }

            var sends = new List<ChannelSend>(controller.Spec.Channels);
            for (var i = 0; i < ports.Count; i++)
            {
                FillPort(devices, controller, ports[i], portZones[i], disabled, uncontrolled, prefs, globalBrightness, nowTicks, sends);
            }

            var wrote = false;
            foreach (var send in PushOrder(controller, sends))
            {
                if (controller.SendChannel(send.Card, new ReadOnlySpan<byte>(send.Buffer, send.Offset, send.Length)))
                {
                    wrote = true;
                }
            }

            // Chunked controllers only apply once latched; the wide transport
            // takes effect on receipt and ignores this.
            if (wrote) controller.SendLatch();
        }
    }

    /// <summary>One channel's bytes for this tick: a lane of a port buffer.</summary>
    private readonly record struct ChannelSend(int Card, byte[] Buffer, int Offset, int Length);

    private IReadOnlyList<ResolvedZone> ResolveOrReuse(string cacheKey, Func<IReadOnlyList<ResolvedZone>> resolve)
    {
        try
        {
            var zones = resolve();
            _zoneCache[cacheKey] = zones;
            return zones;
        }
        catch (InvalidOperationException)
        {
            return _zoneCache.TryGetValue(cacheKey, out var last) ? last : Array.Empty<ResolvedZone>();
        }
    }

    /// <summary>
    /// Fill one port's buffer from its resolved zones, laid down back to back
    /// in chain order, then queue one send per lane that carries LEDs (plus
    /// the wide transport's flag channels, whose marker delimits the update
    /// even at zero LEDs). Power, brightness, colour trim and identify stay
    /// per-zone so one product in a chain can be flashed or switched off
    /// without touching the ones beside it on the same wire.
    /// </summary>
    private void FillPort(DeviceFrame[] devices, NollieController controller, NolliePort port,
        IReadOnlyList<ResolvedZone> zones,
        IReadOnlyList<string> disabled, IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs, float globalBrightness, long nowTicks,
        List<ChannelSend> sends)
    {
        var total = 0;
        for (var i = 0; i < zones.Count; i++) total += Math.Max(0, zones[i].LedCount);
        total = Math.Min(total, port.MaxLedCount);

        var bufferId = NollieLightingDeviceProvider.PortId(controller.DeviceId, port);
        var buf = Rent(bufferId, Math.Max(total, 1) * 3);
        Array.Clear(buf, 0, Math.Min(buf.Length, total * 3));

        var offset = 0;
        for (var i = 0; i < zones.Count && offset < total; i++)
        {
            var zone = zones[i];
            var ledCount = Math.Min(Math.Max(0, zone.LedCount), total - offset);
            if (ledCount > 0)
            {
                var frame = FindFrame(devices, zone.Id);
                var brightnessMul = ComputeBrightnessMul(zone.Id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
                var hasIdentify = _identify.TryGetActive(zone.Id, nowTicks, out var startTicks);
                FillSlice(buf, offset, ledCount, frame, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
            }
            offset += ledCount;
        }

        var spec = controller.Spec;
        for (var lane = 0; lane < port.Lanes; lane++)
        {
            var card = port.FirstChannel + lane;
            var laneLeds = port.LaneLeds(total, lane);
            if (laneLeds > 0 || NollieProtocol.IsFlagChannel(spec, spec.HardwareChannel(card)))
            {
                // An empty flag-channel send carries no bytes, so its offset
                // must not point past a buffer sized for a shorter port.
                var offsetBytes = laneLeds > 0 ? lane * port.LaneLedCount * 3 : 0;
                sends.Add(new ChannelSend(card, buf, offsetBytes, laneLeds * 3));
            }
        }
    }

    /// <summary>
    /// The reference driver sends the wide transport's channels in ascending
    /// HARDWARE channel order; the chunked path sends in card order and
    /// latches. Sends arrive in card order already.
    /// </summary>
    private static List<ChannelSend> PushOrder(NollieController controller, List<ChannelSend> sends)
    {
        var spec = controller.Spec;
        if (spec.Transport == NollieTransport.Wide)
        {
            sends.Sort((a, b) => spec.HardwareChannel(a.Card).CompareTo(spec.HardwareChannel(b.Card)));
        }
        return sends;
    }

    /// <summary>A port counts as uncontrolled only when every one of its resolved zones does.</summary>
    private static bool AllPortsUncontrolled(IReadOnlyList<ResolvedZone>[] portZones, IReadOnlyList<string> uncontrolled)
    {
        foreach (var zones in portZones)
        {
            if (!ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
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

    /// <summary>Fills a slice of the port buffer starting at LED index <paramref name="dstStart"/>, so several zones can tile one port back to back.</summary>
    private static void FillSlice(byte[] dst, int dstStart, int ledCount, DeviceFrame? frame,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        var baseOff = dstStart * 3;
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var v = on ? (byte)255 : (byte)0;
            for (var i = 0; i < ledCount * 3 && baseOff + i < dst.Length; i++) dst[baseOff + i] = v;
            return;
        }
        if (frame is null || brightnessMul <= 0.0) return;

        var src = frame.LedBytes;
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[baseOff + off] = ar;
                dst[baseOff + off + 1] = ag;
                dst[baseOff + off + 2] = ab;
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
                dst[baseOff + off] = src[off];
                dst[baseOff + off + 1] = src[off + 1];
                dst[baseOff + off + 2] = src[off + 2];
            }
            return;
        }
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length || baseOff + off + 2 >= dst.Length) break;
            dst[baseOff + off] = (byte)(src[off] * brightnessMul);
            dst[baseOff + off + 1] = (byte)(src[off + 1] * brightnessMul);
            dst[baseOff + off + 2] = (byte)(src[off + 2] * brightnessMul);
        }
    }
}
