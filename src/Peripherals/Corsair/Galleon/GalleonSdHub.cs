using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>The open Galleon 100 SD keyboard interface, as the lighting stack sees it.</summary>
public sealed class GalleonSdAttached
{
    public GalleonSdAttached(string serial, string path)
    {
        Serial = serial;
        Path = path;
        DeviceId = GalleonSdProtocol.DeviceIdPrefix + serial;
    }

    public string Serial { get; }
    public string Path { get; }
    public string DeviceId { get; }
}

/// <summary>
/// Owns the Galleon 100 SD command interface and streams direct LED frames.
/// Probe build: every OUT frame and IN reply of the connect sequence and the
/// first frames is logged, then one summary line per window. Only the direct
/// lighting resource is ever opened; onboard layer and profile resources are
/// never touched. Handle IO is serialised on <c>_io</c>.
/// </summary>
public sealed class GalleonSdHub : IGalleonLightingHub, IDisposable
{
    private sealed class Slot
    {
        public Slot(GalleonSdAttached info, IHidDevice device)
        {
            Info = info;
            Device = device;
            Payload = new byte[GalleonSdProtocol.DirectPayloadLength];
            LastPayload = new byte[GalleonSdProtocol.DirectPayloadLength];
        }

        public GalleonSdAttached Info { get; }
        public IHidDevice Device { get; }
        public byte[] Payload { get; }
        public byte[] LastPayload { get; }
        public bool HasLast { get; set; }
        public bool ModeTouched { get; set; }
        public bool SoftwareMode { get; set; }
        public byte Handle { get; set; }
        public bool HandleSettled { get; set; }
        public int Failures { get; set; }
        public long NextNegotiateStamp { get; set; }
        public int NegotiationFailures { get; set; }
        public int NegotiationRounds { get; set; }
        public string LastReplyHex { get; set; } = "";
        public int FramesSent { get; set; }
        public long LastSentStamp { get; set; }
        public long LastModeStamp { get; set; }
        public long WindowStartStamp { get; set; }
        public int WindowFrames { get; set; }
        public int WindowSkipped { get; set; }
        public int WindowOk { get; set; }
        public int WindowErr { get; set; }
        public int WindowSilent { get; set; }
        public int RepliesEver { get; set; }
        public int ErrorLogs { get; set; }
    }

    private const int ConsecutiveWriteFailureThreshold = 5;
    private const int ProbeFrames = 3;
    private const int MaxErrorLogs = 20;
    private const int ProbeNegotiations = 3;
    private const int HandbackAfterFailures = 3;
    private const double MinFrameGapMs = 50;
    private const double KeepAliveMs = 10_000;
    private const double ModeReassertMs = 30_000;
    private const double StatsWindowSec = 30;
    private const int ProbeReplyMs = 100;
    private const int FrameReplyMs = 20;
    private const int FirstPacketReplyMs = 5;
    private const int InputBufferBytes = GalleonSdProtocol.WireLength + 1;
    private const int ContinuationPaceMs = 2;
    private const double RejectBackoffMs = 2000;
    private const double MaxRejectBackoffMs = 60_000;
    private const int MaxBackoffDoublings = 5;

    private readonly IHidEnumerator _hid;
    private readonly Func<long> _clock;
    private readonly object _io = new();
    private Slot? _slot;
    private GalleonSdAttached? _snapshot;
    private int _version;
    private bool _disposed;
    private string? _openFailedPath;

    public GalleonSdHub(IHidEnumerator hid, Func<long>? clock = null)
    {
        _hid = hid;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    public GalleonSdAttached? Attached => Volatile.Read(ref _snapshot);

    public bool IsConnected => Attached is not null;

    public int Version => Volatile.Read(ref _version);

    /// <summary>Opens the unit when <paramref name="allowed"/> and closes it (board back in hardware mode) when not. True when the attached state changed.</summary>
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
            foreach (var info in _hid.Find(GalleonSdProtocol.VendorId, GalleonSdProtocol.ProductId))
            {
                // hidraw report lengths are unvalidated for this interface, so Linux units stay unopened.
                if (OperatingSystem.IsLinux()) continue;
                if (!GalleonSdProtocol.IsCommandInterface(info.OutputReportByteLength)) continue;
                if (chosen is null || info.UsagePage == GalleonSdProtocol.VendorUsagePage) chosen = info;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[galleon-sd] HID enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (chosen is null) return false;

        IHidDevice? dev = null;
        try { dev = _hid.Open(chosen.Path, forInput: true); }
        catch (Exception ex)
        {
            ServiceLog.Error($"[galleon-sd] open threw: {ex.GetType().Name}: {ex.Message}");
        }
        if (dev is null)
        {
            if (_openFailedPath != chosen.Path)
            {
                _openFailedPath = chosen.Path;
                ServiceLog.Error($"[galleon-sd] open failed at {chosen.Path}");
            }
            return false;
        }
        _openFailedPath = null;

        lock (_io)
        {
            if (_disposed || _slot is not null)
            {
                try { dev.Dispose(); } catch { /* best effort */ }
                return false;
            }
            var serial = !string.IsNullOrWhiteSpace(chosen.Serial) ? chosen.Serial! : StableIdFromPath(chosen.Path);
            var slot = new Slot(new GalleonSdAttached(serial, chosen.Path), dev);
            _slot = slot;
            Volatile.Write(ref _snapshot, slot.Info);
            Interlocked.Increment(ref _version);
            ServiceLog.Info($"[galleon-sd] connected serial={serial} path={chosen.Path} usage={chosen.UsagePage:X4}/{chosen.Usage:X2} rpt={chosen.InputReportByteLength}/{chosen.OutputReportByteLength}/{chosen.FeatureReportByteLength} layoutConfirmed={GalleonSdProtocol.ReportLayoutConfirmed(chosen.OutputReportByteLength)}");
            ProbeIdentityLocked(slot);
        }
        return true;
    }

    public void Disconnect()
    {
        lock (_io) CloseLocked();
    }

    /// <summary>Streams one frame (<paramref name="leds"/> in <see cref="GalleonSdProtocol.KeyIds"/> order). False when not open or the write failed.</summary>
    public bool WriteFrame(ReadOnlySpan<RgbColor> leds)
    {
        lock (_io)
        {
            var slot = _slot;
            if (slot is null) return false;
            var now = _clock();
            GalleonSdProtocol.EncodeDirect(leds, slot.Payload);
            var sinceSent = StampsToMs(now - slot.LastSentStamp);
            if (slot.LastSentStamp != 0)
            {
                var unchanged = slot.HasLast && slot.Payload.AsSpan().SequenceEqual(slot.LastPayload);
                if (sinceSent < MinFrameGapMs || (unchanged && sinceSent < KeepAliveMs))
                {
                    slot.WindowSkipped++;
                    return true;
                }
            }
            try
            {
                var probe = slot.FramesSent < ProbeFrames;
                if (!slot.SoftwareMode && now < slot.NextNegotiateStamp)
                {
                    slot.WindowSkipped++;
                    return true;
                }
                if (!slot.SoftwareMode || StampsToMs(now - slot.LastModeStamp) >= ModeReassertMs)
                {
                    if (!EnterSoftwareLocked(slot)) return RecordFailureLocked(slot, "software-mode");
                    slot.LastModeStamp = now;
                }
                if (!SendDirectLocked(slot, probe, now, out var sent)) return RecordFailureLocked(slot, "frame");
                if (!sent)
                {
                    slot.WindowSkipped++;
                    return true;
                }
                slot.Payload.CopyTo(slot.LastPayload, 0);
                slot.HasLast = true;
                slot.Failures = 0;
                slot.LastSentStamp = now;
                slot.FramesSent++;
                slot.WindowFrames++;
                LogWindowLocked(slot, _clock());
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[galleon-sd] frame write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordFailureLocked(slot, ex.GetType().Name);
            }
        }
    }

    /// <summary>Returns the board to its onboard lighting. No-op unless Nexus took it.</summary>
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
        ServiceLog.Info($"[galleon-sd] released serial={slot.Info.Serial} frames={slot.FramesSent}");
        return true;
    }

    private void ReleaseLocked(Slot slot)
    {
        if (!slot.ModeTouched) return;
        slot.ModeTouched = false;
        slot.SoftwareMode = false;
        slot.HasLast = false;
        try
        {
            var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "mode->hardware", ProbeReplyMs, log: true);
            if (!reply.IsOk && slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] hardware-mode handback not confirmed (parsed={reply.Parsed} status={reply.Status:X2})");
        }
        catch (Exception ex)
        {
            if (slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] hardware-mode handback threw: {ex.GetType().Name}");
        }
    }

    // Read-only property gets (OpenRGB identity reads, plus the one Web Hub polls) so a support bundle shows how the board answers.
    private void ProbeIdentityLocked(Slot slot)
    {
        try
        {
            DrainInput(slot);
            foreach (var prop in new byte[] { 0x12, 0x11, 0x41, 0x5F, 0xE1 })
            {
                Exchange(slot, GalleonSdProtocol.GetProperty(prop), $"get {prop:X2}", ProbeReplyMs, log: true);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[galleon-sd] identity probe threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool EnterSoftwareLocked(Slot slot)
    {
        var probe = !slot.SoftwareMode && slot.FramesSent < ProbeFrames;
        slot.ModeTouched = true;
        var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeSoftware), "mode->software", ProbeReplyMs, log: probe, check: true);
        if (!reply.IsOk && reply.Parsed) ServiceLog.Warn($"[galleon-sd] software-mode set answered status {reply.Status:X2}; continuing");
        slot.SoftwareMode = true;
        return true;
    }

    // sent=false with a true return means the start was rejected and nothing was written.
    private bool SendDirectLocked(Slot slot, bool log, long now, out bool sent)
    {
        sent = false;
        var replyMs = log ? ProbeReplyMs : FrameReplyMs;
        if (slot.RepliesEver == 0 && slot.FramesSent >= ProbeFrames) replyMs = 0;
        if (!slot.HandleSettled && now < slot.NextNegotiateStamp) return true;

        if (slot.HandleSettled)
        {
            if (!TryStart(slot, slot.Handle, "start tx", replyMs, log, out var start))
            {
                slot.HandleSettled = false;
                NegotiationFailedLocked(slot, now, start);
                return true;
            }
        }
        else
        {
            var logRound = slot.NegotiationRounds++ < ProbeNegotiations;
            byte? accepted = null;
            GalleonReply last = default;
            for (byte handle = 0; handle <= GalleonSdProtocol.MaxHandle; handle++)
            {
                if (TryStart(slot, handle, $"start tx h={handle}", replyMs, logRound, out last))
                {
                    accepted = handle;
                    break;
                }
            }
            if (accepted is null)
            {
                NegotiationFailedLocked(slot, now, last);
                return true;
            }
            slot.Handle = accepted.Value;
            slot.HandleSettled = true;
            slot.NegotiationFailures = 0;
            ServiceLog.Info($"[galleon-sd] direct handle {slot.Handle} (start reply parsed={last.Parsed} status={last.Status:X2})");
        }

        var chunks = GalleonSdProtocol.BlockWrite(slot.Handle, slot.Payload);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i == 0)
            {
                var ack = Exchange(slot, chunks[i], "block first", log ? ProbeReplyMs : Math.Min(replyMs, FirstPacketReplyMs), log, check: true);
                if (!ack.Parsed) PaceWait();
                continue;
            }
            if (!SendWire(slot, chunks[i], $"block next {i}", log)) return false;
            PaceWait();
        }
        Exchange(slot, GalleonSdProtocol.StopTransaction(slot.Handle), "stop tx", replyMs, log, check: true);
        sent = true;
        return true;
    }

    // A start counts as accepted on an OK reply, or when the board never replies at all; a reply that arrives but never parses is a rejection.
    private bool TryStart(Slot slot, byte handle, string label, int replyMs, bool log, out GalleonReply reply)
    {
        DrainInput(slot);
        var before = slot.RepliesEver;
        reply = Exchange(slot, GalleonSdProtocol.StartTransaction(handle, GalleonSdProtocol.DirectResource), label, replyMs, log, check: true);
        var answered = slot.RepliesEver > before;
        return reply.IsOk || (!reply.Parsed && !answered);
    }

    private void NegotiationFailedLocked(Slot slot, long now, GalleonReply last)
    {
        slot.NegotiationFailures++;
        // Past the handback threshold the wait doubles per further rejection, so a Web Hub that keeps every handle busy costs one mode round trip per minute at most, not one per backoff.
        var doublings = Math.Clamp(slot.NegotiationFailures - HandbackAfterFailures + 1, 0, MaxBackoffDoublings);
        var backoffMs = Math.Min(MaxRejectBackoffMs, RejectBackoffMs * (1 << doublings));
        slot.NextNegotiateStamp = now + (long)(backoffMs * Stopwatch.Frequency / 1000);
        if (slot.ErrorLogs++ < MaxErrorLogs)
        {
            ServiceLog.Error($"[galleon-sd] start tx rejected (parsed={last.Parsed} status={last.Status:X2} raw={(slot.LastReplyHex.Length == 0 ? "(no reply)" : slot.LastReplyHex)}); frame skipped, failures={slot.NegotiationFailures}");
        }
        if (slot.NegotiationFailures >= HandbackAfterFailures) ReleaseLocked(slot);
    }

    // OpenRGB CORSAIR_V2_WRITE_PACE: the K100 family drops back-to-back packets, and continuation packets are never acknowledged.
    private static void PaceWait() => Thread.Sleep(ContinuationPaceMs);

    private bool SendWire(Slot slot, byte[] wire, string label, bool log)
    {
        if (log) ServiceLog.Info($"[galleon-sd] OUT {label}: {Hex(wire)}");
        return slot.Device.Write(GalleonSdProtocol.ToHidBuffer(wire));
    }

    private GalleonReply Exchange(Slot slot, byte[] wire, string label, int replyTimeoutMs, bool log, bool check = false)
    {
        if (!SendWire(slot, wire, label, log)) throw new InvalidOperationException($"write failed: {label}");
        if (replyTimeoutMs <= 0)
        {
            DrainInput(slot);
            return default;
        }
        var cmd = wire[GalleonSdProtocol.CommandIndex];
        slot.LastReplyHex = "";
        var buf = new byte[InputBufferBytes];
        var deadline = _clock() + (long)(replyTimeoutMs * Stopwatch.Frequency / 1000.0);
        GalleonReply result = default;
        var gotAny = false;
        while (true)
        {
            var remaining = (int)Math.Max(0, (deadline - _clock()) * 1000.0 / Stopwatch.Frequency);
            var n = slot.Device.Read(buf, remaining);
            if (n <= 0) break;
            gotAny = true;
            slot.RepliesEver++;
            slot.LastReplyHex = Hex(buf.AsSpan(0, n));
            var reply = GalleonSdProtocol.ParseReply(cmd, buf.AsSpan(0, n));
            if (log) ServiceLog.Info($"[galleon-sd] IN  {label}: {Hex(buf.AsSpan(0, n))} parsed={reply.Parsed} status={reply.Status:X2}");
            if (reply.Parsed)
            {
                result = reply;
                break;
            }
            result = reply;
            if (_clock() >= deadline) break;
        }
        if (check) CountReply(slot, label, gotAny, result, log);
        return result;
    }

    private static void CountReply(Slot slot, string label, bool gotAny, GalleonReply reply, bool alreadyLogged)
    {
        if (!gotAny) { slot.WindowSilent++; return; }
        if (reply.IsOk) { slot.WindowOk++; return; }
        if (!reply.Parsed) return;
        slot.WindowErr++;
        if (!alreadyLogged && slot.ErrorLogs++ < MaxErrorLogs)
        {
            ServiceLog.Warn($"[galleon-sd] {label} answered error status {reply.Status:X2}");
        }
    }

    private static void DrainInput(Slot slot)
    {
        var buf = new byte[InputBufferBytes];
        for (var i = 0; i < 8 && slot.Device.Read(buf, 0) > 0; i++) { }
    }

    private void LogWindowLocked(Slot slot, long now)
    {
        if (slot.WindowStartStamp == 0) slot.WindowStartStamp = now;
        var windowMs = StampsToMs(now - slot.WindowStartStamp);
        if (windowMs < StatsWindowSec * 1000) return;
        ServiceLog.Info($"[galleon-sd] stream stats: frames={slot.WindowFrames} skipped={slot.WindowSkipped} replyOk={slot.WindowOk} replyErr={slot.WindowErr} noReply={slot.WindowSilent} handle={slot.Handle} window={windowMs / 1000:F0}s");
        slot.WindowStartStamp = now;
        slot.WindowFrames = 0;
        slot.WindowSkipped = 0;
        slot.WindowOk = 0;
        slot.WindowErr = 0;
        slot.WindowSilent = 0;
    }

    private bool RecordFailureLocked(Slot slot, string where)
    {
        var n = ++slot.Failures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            ServiceLog.Error($"[galleon-sd] {n} consecutive IO failures ({where}) - dropping interface");
            ReleaseLocked(slot);
            try { slot.Device.Dispose(); } catch { /* best effort */ }
            _slot = null;
            Volatile.Write(ref _snapshot, null);
            Interlocked.Increment(ref _version);
        }
        return false;
    }

    private static double StampsToMs(long stamps) => stamps * 1000.0 / Stopwatch.Frequency;

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        if (end == 0) return "(all zero)";
        var sb = new StringBuilder(end * 3);
        for (var i = 0; i < end; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
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
