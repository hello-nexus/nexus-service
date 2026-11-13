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
        public bool SavedMode { get; set; }
        public bool HardwareHeld { get; set; }
        public bool ProbeDone { get; set; }
        public byte[] ParsedReply { get; } = new byte[GalleonSdProtocol.WireLength + 1];
        public int ParsedReplyLength { get; set; }
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
    private const double ProbeBudgetMs = 1200;
    private const int OpenReplyMs = 300;
    private const int MinProbeWaitMs = 20;
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

    private readonly bool _matchOnLinux;

    /// <param name="matchOnLinux">Lets the interface match run on Linux. Off in production; tests turn it on so the sequencing is exercised on every OS.</param>
    public GalleonSdHub(IHidEnumerator hid, Func<long>? clock = null, bool matchOnLinux = false)
    {
        _matchOnLinux = matchOnLinux;
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
                if (OperatingSystem.IsLinux() && !_matchOnLinux) continue;
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
            if (slot.SavedMode) return true;
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
                Nexus.Service.Lighting.LightingWriteHealth.Shared.NoteSuccess(slot.Info.DeviceId, GalleonSdProtocol.HandlerId);
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

    /// <summary>
    /// Applies the per-device "save colors to the keyboard" toggle. On: stop live frames, hand the
    /// board back to hardware mode (keys work) and hold it there; the read-only onboard probe runs
    /// once per session. Off: live streaming resumes through <see cref="WriteFrame"/> on its next call.
    /// Idempotent, safe to call every writer tick.
    /// </summary>
    public void SetSavedToDevice(bool saved)
    {
        Slot? probe = null;
        lock (_io)
        {
            var slot = _slot;
            if (slot is null) return;
            if (!saved)
            {
                slot.SavedMode = false;
                return;
            }
            try
            {
                if (!slot.SavedMode)
                {
                    slot.SavedMode = true;
                    slot.HasLast = false;
                    slot.HandleSettled = false;
                }
                if (slot.ModeTouched)
                {
                    slot.HardwareHeld = ReleaseLocked(slot);
                    // ReleaseLocked counts each unconfirmed attempt; at the threshold the interface is dropped like a failing stream.
                    if (!slot.HardwareHeld && slot.Failures >= ConsecutiveWriteFailureThreshold) RecordFailureLocked(slot, "saved-hold");
                }
                else if (!slot.HardwareHeld)
                {
                    var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "mode->hardware (saved)", ProbeReplyMs, log: slot.ErrorLogs < MaxErrorLogs);
                    slot.HardwareHeld = RestoreConfirmed(reply);
                    if (!slot.HardwareHeld)
                    {
                        if (slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] saved-mode hardware hold answered status {reply.Status:X2}");
                        RecordFailureLocked(slot, "saved-hold");
                    }
                }
                if (!slot.ProbeDone && slot.HardwareHeld)
                {
                    slot.ProbeDone = true;
                    if (slot.NegotiationFailures > 0)
                    {
                        ServiceLog.Info("[galleon-sd-onboard] probe skipped: the direct resource was contended");
                    }
                    else
                    {
                        probe = slot;
                    }
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                if (slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] saved-mode hold threw: {ex.GetType().Name}: {ex.Message}");
                RecordFailureLocked(slot, "saved-hold");
                return;
            }
        }
        if (probe is not null) RunProbe(probe);
    }

    // A restore counts as confirmed unless the board answered with an error status; silence is accepted like the stream path does.
    private static bool RestoreConfirmed(GalleonReply reply) => !(reply.Parsed && reply.Status != 0);

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

    // True when the board is in hardware mode afterwards (or nothing was taken). An error-status reply counts like a throw:
    // ModeTouched stays armed so the next release retries, until the failure threshold gives up.
    private bool ReleaseLocked(Slot slot)
    {
        if (!slot.ModeTouched) return true;
        slot.SoftwareMode = false;
        slot.HasLast = false;
        var confirmed = false;
        try
        {
            var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "mode->hardware", ProbeReplyMs, log: true);
            confirmed = RestoreConfirmed(reply);
            if (!confirmed && slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] hardware-mode handback not confirmed (parsed={reply.Parsed} status={reply.Status:X2})");
        }
        catch (Exception ex)
        {
            if (slot.ErrorLogs++ < MaxErrorLogs) ServiceLog.Warn($"[galleon-sd] hardware-mode handback threw: {ex.GetType().Name}");
        }
        if (confirmed || ++slot.Failures >= ConsecutiveWriteFailureThreshold) slot.ModeTouched = false;
        return confirmed;
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

    // Read-only walk of the onboard lighting objects, run once per session while saving is on. Mirrors Web Hub:
    // software mode, reads, hardware mode. Sends only mode sets, a property get and open(0D)/size(09)/read(08)/close(05);
    // never 06/07 (write), 0B or 0C. Raw replies are logged so the open/size/read reply formats can be learned.
    // The I/O lock is taken per step, not for the whole walk, and the walk stops at a total time budget.
    private void RunProbe(Slot slot)
    {
        ServiceLog.Info($"[galleon-sd-onboard] probe begin serial={slot.Info.Serial}");
        var deadline = _clock() + (long)(ProbeBudgetMs * Stopwatch.Frequency / 1000);
        try
        {
            var begun = WithSlot(slot, s =>
            {
                DrainInput(s);
                s.ModeTouched = true;
                s.HardwareHeld = false;
                Exchange(s, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeSoftware), "onboard mode->software", ProbeReplyMs, log: true);
                Exchange(s, GalleonSdProtocol.GetProperty(GalleonSdProtocol.PropActiveProfile), "onboard get 6E", ProbeReplyMs, log: true);
            });
            if (!begun) return;

            var budget = GalleonSdOnboardProbe.MaxResources;
            if (!ProbeRead(slot, GalleonSdOnboardProbe.RegistryResource, deadline, ref budget, out var registry)) return;
            if (!GalleonSdOnboardProbe.TryParseRegistry(registry, out var profiles))
            {
                ServiceLog.Info("[galleon-sd-onboard] registry not recognized in reply");
                return;
            }
            ServiceLog.Info($"[galleon-sd-onboard] registry refs={FormatRefs(profiles)}");
            for (var p = 0; p < Math.Min(profiles.Length, GalleonSdOnboardProbe.MaxProfiles); p++)
            {
                if (!ProbeRead(slot, profiles[p], deadline, ref budget, out var profile)) return;
                if (!GalleonSdOnboardProbe.TryParseProfileLayerList(profile, out var listRes))
                {
                    ServiceLog.Info($"[galleon-sd-onboard] profile {profiles[p]:X4} layer list slot not recognized");
                    continue;
                }
                ServiceLog.Info($"[galleon-sd-onboard] profile {profiles[p]:X4} layerList={listRes:X4}");
                if (!ProbeRead(slot, listRes, deadline, ref budget, out var list)) return;
                if (!GalleonSdOnboardProbe.TryParseLayerList(list, out var layers))
                {
                    ServiceLog.Info($"[galleon-sd-onboard] layer list {listRes:X4} not recognized");
                    continue;
                }
                ServiceLog.Info($"[galleon-sd-onboard] layer list {listRes:X4} layers={FormatRefs(layers)}");
                for (var l = 0; l < Math.Min(layers.Length, GalleonSdOnboardProbe.MaxLayers); l++)
                {
                    if (!ProbeRead(slot, layers[l], deadline, ref budget, out _)) return;
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[galleon-sd-onboard] probe threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RestoreAfterProbe(slot);
            ServiceLog.Info("[galleon-sd-onboard] probe end");
        }
    }

    // A slot that was released or replaced in the meantime already had hardware mode written by its release, so there is nothing to restore.
    private void RestoreAfterProbe(Slot slot)
    {
        lock (_io)
        {
            if (_disposed || _slot != slot) return;
            try
            {
                var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "onboard mode->hardware", ProbeReplyMs, log: true);
                if (RestoreConfirmed(reply))
                {
                    slot.ModeTouched = false;
                    slot.HardwareHeld = true;
                }
                else
                {
                    ServiceLog.Warn($"[galleon-sd-onboard] hardware-mode restore answered status {reply.Status:X2}; will retry");
                    RecordFailureLocked(slot, "probe-restore");
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[galleon-sd-onboard] hardware-mode restore threw: {ex.GetType().Name}");
                RecordFailureLocked(slot, "probe-restore");
            }
        }
    }

    // Runs one probe step under the I/O lock; false when the slot was released or replaced meanwhile. Saved mode cannot change underneath: only the writer thread that runs the probe toggles it.
    private bool WithSlot(Slot slot, Action<Slot> step)
    {
        lock (_io)
        {
            if (_disposed || _slot != slot) return false;
            step(slot);
            return true;
        }
    }

    private bool ProbeRead(Slot slot, ushort resource, long deadline, ref int budget, out byte[] body)
    {
        var read = Array.Empty<byte>();
        body = read;
        if (budget <= 0 || _clock() >= deadline)
        {
            ServiceLog.Info("[galleon-sd-onboard] probe budget reached");
            return false;
        }
        budget--;
        var completed = false;
        var held = WithSlot(slot, s => completed = ReadResourceLocked(s, resource, deadline, out read));
        body = read;
        return held && completed;
    }

    // Reply wait for a step: the usual window, clamped to what is left of the probe budget (never below a small floor).
    private int ProbeWait(long deadline, int maxMs)
    {
        var left = (int)Math.Min(int.MaxValue, (deadline - _clock()) * 1000.0 / Stopwatch.Frequency);
        return Math.Clamp(left, MinProbeWaitMs, maxMs);
    }

    // open, size, two reads (Web Hub reads big objects twice), close. The body is the first read reply that parses as an OK 08 reply.
    // Open outcomes: OK proceeds; an explicit refusal (parsed, non-OK) sends nothing more because no handle exists; silence is
    // treated as a possibly late OK, so a close for handle 0 is sent (this probe only runs when no other host was seen contending,
    // so a stray close can only hit a handle this probe itself opened). Steps past the deadline are skipped; the close never is.
    private bool ReadResourceLocked(Slot slot, ushort resource, long deadline, out byte[] body)
    {
        const byte handle = 0;
        var tag = $"onboard {resource:X4}";
        body = Array.Empty<byte>();
        var open = Exchange(slot, GalleonSdProtocol.StartTransaction(handle, resource), $"{tag} open", ProbeWait(deadline, OpenReplyMs), log: true);
        if (!open.IsOk)
        {
            if (open.Parsed)
            {
                ServiceLog.Info($"[galleon-sd-onboard] {tag} open refused (status={open.Status:X2}); probe stops");
            }
            else
            {
                ServiceLog.Info($"[galleon-sd-onboard] {tag} open unanswered; closing handle in case it opened late, probe stops");
                Exchange(slot, GalleonSdProtocol.StopTransaction(handle), $"{tag} close (unanswered open)", ProbeReplyMs, log: true);
            }
            return false;
        }
        try
        {
            if (_clock() < deadline) Exchange(slot, GalleonSdProtocol.ResourceSize(handle), $"{tag} size", ProbeWait(deadline, ProbeReplyMs), log: true);
            for (var i = 0; i < 2 && _clock() < deadline; i++)
            {
                var read = Exchange(slot, GalleonSdProtocol.ResourceRead(handle), $"{tag} read {i}", ProbeWait(deadline, ProbeReplyMs), log: true);
                if (body.Length == 0 && read.IsOk && slot.ParsedReplyLength > 0) body = slot.ParsedReply.AsSpan(0, slot.ParsedReplyLength).ToArray();
            }
        }
        finally
        {
            Exchange(slot, GalleonSdProtocol.StopTransaction(handle), $"{tag} close", ProbeReplyMs, log: true);
        }
        return true;
    }

    private static string FormatRefs(ushort[] refs)
    {
        var sb = new StringBuilder(refs.Length * 5);
        for (var i = 0; i < refs.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(refs[i].ToString("X4"));
        }
        return sb.ToString();
    }

    private bool EnterSoftwareLocked(Slot slot)
    {
        var probe = !slot.SoftwareMode && slot.FramesSent < ProbeFrames;
        slot.ModeTouched = true;
        slot.HardwareHeld = false;
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
        slot.ParsedReplyLength = 0;
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
                slot.ParsedReplyLength = Math.Min(n, slot.ParsedReply.Length);
                buf.AsSpan(0, slot.ParsedReplyLength).CopyTo(slot.ParsedReply);
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
        Nexus.Service.Lighting.LightingWriteHealth.Shared.NoteFailure(slot.Info.DeviceId, GalleonSdProtocol.HandlerId);
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
