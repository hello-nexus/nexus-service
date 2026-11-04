using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hid.LampArray;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>What the connection worker needs from a Galleon lighting interface owner.</summary>
public interface IGalleonLightingHub
{
    bool IsConnected { get; }
    int Version { get; }
    bool Reconcile(bool allowed);
    void Disconnect();
}

/// <summary>The open Galleon 100 SD dial LampArray, as the lighting stack sees it.</summary>
public sealed class GalleonSdDialsAttached
{
    public GalleonSdDialsAttached(string serial, string path, IReadOnlyList<LampAttributes> lamps)
    {
        Serial = serial;
        Path = path;
        Lamps = lamps;
        DeviceId = GalleonSdProtocol.DialsDeviceIdPrefix + serial;
    }

    public string Serial { get; }
    public string Path { get; }
    public string DeviceId { get; }
    public IReadOnlyList<LampAttributes> Lamps { get; }
    public int LampCount => Lamps.Count;
}

/// <summary>
/// Drives the dial lamps of the Galleon 100 SD (1B1C:2B18) through its standard HID LampArray
/// collection (usage page 0x59), a separate interface from the Stream Deck protocol one.
/// Report IDs and lamp attributes are read from the device. Probe build: the parsed attributes
/// and the result of the first sends are logged under a fixed line budget. Fails closed: any
/// unreadable attribute leaves the dials untouched. Handle IO is serialised on <c>_io</c>.
/// </summary>
public sealed class GalleonSdDialsHub : IGalleonLightingHub, IDisposable
{
    private sealed class Slot
    {
        public Slot(GalleonSdDialsAttached info, IHidDevice device, LampArrayReportIds ids, LampArrayAttributes attrs)
        {
            Info = info;
            Device = device;
            Ids = ids;
            Attrs = attrs;
            Last = new RgbColor[info.LampCount];
            LampIds = new int[info.LampCount];
            for (var i = 0; i < LampIds.Length; i++) LampIds[i] = info.Lamps[i].LampId;
        }

        public GalleonSdDialsAttached Info { get; }
        public IHidDevice Device { get; }
        public LampArrayReportIds Ids { get; }
        public LampArrayAttributes Attrs { get; }
        public RgbColor[] Last { get; }
        public int[] LampIds { get; }
        public bool HasLast { get; set; }
        public bool Taken { get; set; }
        public long LastSentStamp { get; set; }
        public long LastControlStamp { get; set; }
        public int Failures { get; set; }
        public int Sends { get; set; }
    }

    private const int ConsecutiveFailureThreshold = 5;
    private const int ProbeSends = 3;
    private const int LogBudget = 200;
    private const double MinFrameGapMs = 50;
    private const double KeepAliveMs = 10_000;
    private const double ControlReassertMs = 30_000;

    private readonly IHidEnumerator _hid;
    private readonly Func<long> _clock;
    private readonly object _io = new();
    private Slot? _slot;
    private GalleonSdDialsAttached? _snapshot;
    private int _version;
    private bool _disposed;
    private string? _failedKey;
    private int _logsLeft = LogBudget;
    private bool _capsDumped;

    public GalleonSdDialsHub(IHidEnumerator hid, Func<long>? clock = null)
    {
        _hid = hid;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    public GalleonSdDialsAttached? Attached => Volatile.Read(ref _snapshot);

    public bool IsConnected => Attached is not null;

    public int Version => Volatile.Read(ref _version);

    public bool Reconcile(bool allowed)
    {
        lock (_io)
        {
            if (_disposed) return false;
            if (!allowed) return CloseLocked();
            if (_slot is not null) return false;
        }

        HidDeviceInfo? chosen = null;
        try
        {
            foreach (var info in _hid.Find(GalleonSdProtocol.VendorId, GalleonSdProtocol.StreamDeckProductId))
            {
                if (info.UsagePage != LampArrayProtocol.UsagePage || info.Usage != LampArrayProtocol.LampArrayUsage) continue;
                chosen = info;
                break;
            }
        }
        catch (Exception ex)
        {
            Log($"HID enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (chosen is null) return false;

        IHidDevice? dev = null;
        try { dev = _hid.Open(chosen.Path); }
        catch (Exception ex) { Log($"open threw: {ex.GetType().Name}: {ex.Message}"); }
        if (dev is null)
        {
            LogOnce(chosen.Path + "|open", $"open failed at {chosen.Path}");
            return false;
        }

        Slot? slot = null;
        try { slot = Probe(dev, chosen); }
        catch (Exception ex) { LogOnce(chosen.Path + "|probe", $"probe threw: {ex.GetType().Name}: {ex.Message}"); }
        if (slot is null)
        {
            try { dev.Dispose(); } catch { /* best effort */ }
            return false;
        }

        lock (_io)
        {
            if (_disposed || _slot is not null)
            {
                try { dev.Dispose(); } catch { /* best effort */ }
                return false;
            }
            _slot = slot;
            _failedKey = null;
            Volatile.Write(ref _snapshot, slot.Info);
            Interlocked.Increment(ref _version);
            Log($"connected serial={slot.Info.Serial} path={slot.Info.Path} lamps={slot.Info.LampCount} {LampArrayProtocol.Describe(slot.Ids)}");
        }
        return true;
    }

    // Reads the report map, the array attributes and every lamp once. Null (device closed by the caller) on any unreadable piece.
    private Slot? Probe(IHidDevice dev, HidDeviceInfo info)
    {
        var key = info.Path + "|probe";
        var first = _failedKey != key;
        if (first) Log($"interface {info.Path} usage={info.UsagePage:X4}/{info.Usage:X2} rpt={info.InputReportByteLength}/{info.OutputReportByteLength}/{info.FeatureReportByteLength}");
        if (dev is not IHidFeatureCaps capsSource)
        {
            LogOnce(key, "no report caps source on this platform; dials left alone");
            return null;
        }
        var caps = capsSource.ReadFeatureValueCaps();
        if (caps is null || caps.Count == 0)
        {
            LogOnce(key, "feature value caps unreadable; dials left alone");
            return null;
        }
        if (!_capsDumped)
        {
            _capsDumped = true;
            Log($"feature value caps: {caps.Count} entries");
            foreach (var c in caps) Log(LampArrayProtocol.DescribeCap(c));
        }
        var ids = LampArrayProtocol.ResolveReportIds(caps, out var reason);
        if (ids is null)
        {
            LogOnce(key, $"{reason}; dials left alone");
            return null;
        }

        var attrBuf = new byte[LampArrayProtocol.AttributesBytes];
        attrBuf[0] = ids.Value.Attributes;
        if (!dev.GetFeature(attrBuf) || !LampArrayProtocol.TryParseAttributes(attrBuf, out var attrs))
        {
            LogOnce(key, $"attributes report unreadable raw={LampArrayProtocol.Hex(attrBuf)}");
            return null;
        }
        if (first) Log($"attributes lamps={attrs.LampCount} box={attrs.WidthUm}x{attrs.HeightUm}x{attrs.DepthUm}um kind={attrs.Kind} minInterval={attrs.MinUpdateIntervalUs}us raw={LampArrayProtocol.Hex(attrBuf)}");
        if (attrs.LampCount is < 1 or > LampArrayProtocol.MaxLamps)
        {
            LogOnce(key, $"lamp count {attrs.LampCount} out of range; dials left alone");
            return null;
        }

        var lamps = new List<LampAttributes>(attrs.LampCount);
        var respBuf = new byte[LampArrayProtocol.ResponseBytes];
        for (var i = 0; i < attrs.LampCount; i++)
        {
            Array.Clear(respBuf);
            respBuf[0] = ids.Value.Response;
            if (!dev.SetFeature(LampArrayProtocol.BuildLampRequest(ids.Value, i))
                || !dev.GetFeature(respBuf)
                || !LampArrayProtocol.TryParseLampAttributes(respBuf, out var lamp))
            {
                LogOnce(key, $"lamp {i} attributes unreadable raw={LampArrayProtocol.Hex(respBuf)}; dials left alone");
                return null;
            }
            if (first && lamp.LampId != i) Log($"lamp {i} answered id {lamp.LampId}");
            if (first && lamps.Count < 12) Log($"lamp {i} id={lamp.LampId} pos={lamp.XUm},{lamp.YUm},{lamp.ZUm}um purposes={lamp.Purposes:X} latency={lamp.LatencyUs}us levels={lamp.RedLevels}/{lamp.GreenLevels}/{lamp.BlueLevels}/{lamp.IntensityLevels} programmable={lamp.Programmable}");
            lamps.Add(lamp);
        }

        var serial = !string.IsNullOrWhiteSpace(info.Serial) ? info.Serial! : StableIdFromPath(info.Path);
        return new Slot(new GalleonSdDialsAttached(serial, info.Path, lamps), dev, ids.Value, attrs);
    }

    public void Disconnect()
    {
        lock (_io) CloseLocked();
    }

    /// <summary>Sends one frame (<paramref name="leds"/> in lamp order). False when not open or a write failed.</summary>
    public bool WriteFrame(ReadOnlySpan<RgbColor> leds)
    {
        lock (_io)
        {
            var slot = _slot;
            if (slot is null) return false;
            var count = slot.Info.LampCount;
            if (leds.Length < count) return false;
            var frame = leds[..count];
            var now = _clock();
            var minGap = Math.Max(MinFrameGapMs, slot.Attrs.MinUpdateIntervalUs / 1000.0);
            var sinceSent = StampsToMs(now - slot.LastSentStamp);
            if (slot.LastSentStamp != 0)
            {
                var unchanged = slot.HasLast && frame.SequenceEqual(slot.Last);
                if (sinceSent < minGap || (unchanged && sinceSent < KeepAliveMs)) return true;
            }
            try
            {
                if (!slot.Taken || StampsToMs(now - slot.LastControlStamp) >= ControlReassertMs)
                {
                    var ok = slot.Device.SetFeature(LampArrayProtocol.BuildControl(slot.Ids, autonomous: false));
                    if (slot.Sends < ProbeSends || !ok) Log($"autonomous off ok={ok}");
                    if (!ok) return RecordFailureLocked(slot, "control");
                    slot.Taken = true;
                    slot.LastControlStamp = now;
                }
                if (!SendColorsLocked(slot, frame)) return RecordFailureLocked(slot, "update");
                frame.CopyTo(slot.Last);
                slot.HasLast = true;
                slot.Failures = 0;
                slot.LastSentStamp = now;
                slot.Sends++;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                LogOnce("write|" + ex.GetType().Name, $"write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordFailureLocked(slot, ex.GetType().Name);
            }
        }
    }

    private bool SendColorsLocked(Slot slot, ReadOnlySpan<RgbColor> frame)
    {
        var probe = slot.Sends < ProbeSends;
        var uniform = true;
        for (var i = 1; i < frame.Length; i++)
        {
            if (frame[i] != frame[0]) { uniform = false; break; }
        }
        if (uniform && AllLampsShareIntensity(slot, frame[0]))
        {
            var report = LampArrayProtocol.BuildRangeUpdate(slot.Ids, slot.LampIds[0], slot.LampIds[^1], frame[0], LampArrayProtocol.IntensityFor(slot.Info.Lamps[0], frame[0]));
            var ok = slot.Device.SetFeature(report);
            if (probe || !ok) Log($"range update ok={ok} {LampArrayProtocol.Hex(report)}");
            return ok;
        }

        var intensity = new byte[LampArrayProtocol.MultiUpdateLamps];
        for (var first = 0; first < frame.Length; first += LampArrayProtocol.MultiUpdateLamps)
        {
            var n = Math.Min(LampArrayProtocol.MultiUpdateLamps, frame.Length - first);
            for (var i = 0; i < n; i++) intensity[i] = LampArrayProtocol.IntensityFor(slot.Info.Lamps[first + i], frame[first + i]);
            var complete = first + n >= frame.Length;
            var report = LampArrayProtocol.BuildMultiUpdate(slot.Ids, slot.LampIds.AsSpan(first, n), frame.Slice(first, n), intensity, complete);
            var ok = slot.Device.SetFeature(report);
            if ((probe && first == 0) || !ok) Log($"multi update from {first} ok={ok} {LampArrayProtocol.Hex(report)}");
            if (!ok) return false;
        }
        return true;
    }

    // A range update carries one intensity, so lamps of mixed kinds (intensity-only next to RGB) take the per-lamp path.
    private static bool AllLampsShareIntensity(Slot slot, RgbColor color)
    {
        var first = LampArrayProtocol.IntensityFor(slot.Info.Lamps[0], color);
        for (var i = 1; i < slot.Info.LampCount; i++)
        {
            if (LampArrayProtocol.IntensityFor(slot.Info.Lamps[i], color) != first) return false;
        }
        return true;
    }

    /// <summary>Returns the dials to their onboard lighting. No-op unless Nexus took them.</summary>
    public void ReleaseToHardware()
    {
        lock (_io)
        {
            if (_slot is not null) ReleaseLocked(_slot);
        }
    }

    public void Dispose()
    {
        lock (_io)
        {
            if (_disposed) return;
            _disposed = true;
            CloseLocked();
        }
    }

    private bool CloseLocked()
    {
        var slot = _slot;
        if (slot is null) return false;
        ReleaseLocked(slot);
        try { slot.Device.Dispose(); } catch { /* best effort */ }
        _slot = null;
        Volatile.Write(ref _snapshot, null);
        Interlocked.Increment(ref _version);
        Log($"released serial={slot.Info.Serial} sends={slot.Sends}");
        return true;
    }

    private void ReleaseLocked(Slot slot)
    {
        if (!slot.Taken) return;
        slot.Taken = false;
        slot.HasLast = false;
        try
        {
            var ok = slot.Device.SetFeature(LampArrayProtocol.BuildControl(slot.Ids, autonomous: true));
            Log($"autonomous on ok={ok}");
        }
        catch (Exception ex)
        {
            Log($"autonomous restore threw: {ex.GetType().Name}");
        }
    }

    private bool RecordFailureLocked(Slot slot, string where)
    {
        var n = ++slot.Failures;
        if (n >= ConsecutiveFailureThreshold)
        {
            Log($"{n} consecutive IO failures ({where}), dropping interface");
            ReleaseLocked(slot);
            try { slot.Device.Dispose(); } catch { /* best effort */ }
            _slot = null;
            Volatile.Write(ref _snapshot, null);
            Interlocked.Increment(ref _version);
        }
        return false;
    }

    private static double StampsToMs(long stamps) => stamps * 1000.0 / Stopwatch.Frequency;

    // Every line shares one budget so a flapping interface cannot flood the log.
    private void Log(string message, bool warn = false)
    {
        var left = Interlocked.Decrement(ref _logsLeft);
        if (left < 0) return;
        var line = $"[galleon-sd-dials] {message}{(left == 0 ? " (further lines suppressed)" : "")}";
        if (warn) ServiceLog.Warn(line);
        else ServiceLog.Info(line);
    }

    private void LogOnce(string key, string message)
    {
        if (_failedKey == key) return;
        _failedKey = key;
        Log(message, warn: true);
    }

    private static string StableIdFromPath(string path)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"p-{h:x8}";
        }
    }
}
