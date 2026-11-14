using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>
/// Saved colors: writes the settled frame into onboard static layers (see <see cref="GalleonSdOnboardCodec"/>)
/// and undoes that on turn-off. One transaction mirrors a Web Hub edit: software mode, reads, layer bodies,
/// layer list, software mode again, profile (new timestamp), hardware mode, registry, hardware mode. Layers
/// Nexus did not create are never written, deleted or reordered, and the profile's FN slot is never touched.
/// Any refused open, error status or silent step aborts the transaction, restores hardware mode and backs off.
/// A close is sent only for a handle this code opened. All of it runs under the hub's I/O lock.
/// </summary>
public sealed partial class GalleonSdHub
{
    private const double DebounceMs = 3000;
    private const double MinTxGapMs = 5000;
    // A failed transaction is retried soon: the board's replies come back in order, so a stall clears within about a second.
    private const double MinRetryMs = 1500;
    private const double TxBudgetMs = 6000;
    private const double AnimatedLogMs = 15_000;
    private const int MaxOnboardFailures = 5;
    private const int MaxLogLines = 700;
    private const int MaxObjectBytes = 2048;
    private const int ReadChunkBytes = GalleonSdProtocol.WireLength - 5;
    private const int MaxReadChunks = 24;
    private const int MaxLayerReads = 16;
    private const int MaxTxPerHour = 30;
    private const int MaxTxPerDay = 200;
    private const int ColorTolerance = 4;
    // Resource ids seen on the board all sit in this window; allocation never starts from a value outside it.
    private const int AllocMin = 0x6D00;
    private const int AllocMax = 0x6EFF;
    private const int MaxAllocProbes = 24;
    private const byte ReadHandle = 0;
    private const byte LayerHandle = 1;
    private const byte ListHandle = 2;
    private const byte ProfileHandle = 3;

    private readonly GalleonOnboardStore _onboard;

    /// <summary>Diagnostic: after every onboard write, read the resource back and log whether it matches what was written. Costs one extra read per write.</summary>
    public bool VerifyOnboardWrites { get; set; }

    /// <summary>After each successful transaction, one read-only probe walk runs outside the write bracket and logs what the board now holds.</summary>
    public bool PostTxProbe { get; set; } = true;

    /// <summary>Source of the profile timestamp; tests pin it.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Read-backs that differed from what was written, since the hub was created.</summary>
    internal int VerifyMismatchCount { get; private set; }

    private sealed class OnboardState
    {
        public RgbColor[]? LastFrame;
        public long ChangedStamp;
        public long RunStart;
        public bool RunActive;
        public bool AnimatedLogged;
        public bool ForeignLogged;
        public int FrameVersion;
        public int DoneVersion = -1;
        public long NextTxStamp;
        public int Failures;
        public bool GaveUp;
        public List<GalleonQuantLayer>? LastLayers;
        public List<long> TxStamps = new();
        public bool CapLogged;
        public bool ReRun;
        public bool InTail;
        public bool LayersLogged;
        public int LogBudget = MaxLogLines;
        public int OpenHandle = -1;
        public long TxDeadline;
        public bool Unsettled;
        public bool UndoChecked;
        public bool UndoPending;
        public bool UndoGaveUp;
        public int UndoFailures;
        public long NextUndoStamp;
        public bool LastDeterministic;

        public void BeginSaved()
        {
            LastFrame = null;
            ChangedStamp = 0;
            RunStart = 0;
            RunActive = false;
            AnimatedLogged = false;
            ForeignLogged = false;
            FrameVersion = 0;
            DoneVersion = -1;
            NextTxStamp = 0;
            Failures = 0;
            GaveUp = false;
            LastLayers = null;
            TxStamps = new List<long>();
            CapLogged = false;
            ReRun = false;
            LayersLogged = false;
            InTail = false;
            LogBudget = MaxLogLines;
            Unsettled = false;
            UndoChecked = false;
            UndoPending = false;
            UndoGaveUp = false;
            UndoFailures = 0;
            NextUndoStamp = 0;
            LastDeterministic = false;
        }
    }

    private sealed class TxAbort : Exception
    {
        /// <summary>The board answered with something that will not change on a retry (refused open, bad layout); silence and short reads are not.</summary>
        public bool Deterministic { get; }

        public TxAbort(string message, bool deterministic = false) : base(message) => Deterministic = deterministic;
    }

    private enum OpenOutcome { Ok, Refused, Silent }

    private sealed class BoardView
    {
        public ushort[] RegistryRefs = Array.Empty<ushort>();
        public byte[] Registry = Array.Empty<byte>();
        public ushort ActiveRes;
        /// <summary>The profile being edited: the recorded one while Nexus layers are on it, else the active one.</summary>
        public ushort ProfileRes;
        /// <summary>Nexus layers are recorded on a different profile than the active one.</summary>
        public bool Mismatch;
        /// <summary>The recorded profile is no longer in the registry.</summary>
        public bool RecordedGone;
        public byte[] Profile = Array.Empty<byte>();
        public ushort[] ProfileSlots = Array.Empty<ushort>();
        public ushort ListRes;
        public ushort[] ListRefs = Array.Empty<ushort>();
        public byte[] ListBytes = Array.Empty<byte>();
    }

    // Debounce, rate cap and skip-identical. Runs only with saved mode on, the board in hardware mode and a frame in hand.
    private void ServiceOnboardLocked(Slot slot, ReadOnlySpan<RgbColor> frame)
    {
        var ob = slot.Ob;
        if (ob.GaveUp) return;
        if (slot.NegotiationFailures > 0 || slot.ForeignHandle)
        {
            _savedBlocked = SavedBlockedForeignHost;
            if (!ob.ForeignLogged)
            {
                ob.ForeignLogged = true;
                ServiceLog.Info("[galleon-sd-onboard] saved colors not written: another host holds the command interface");
            }
            return;
        }

        var now = _clock();
        if (ob.LastFrame is null || !frame.SequenceEqual(ob.LastFrame))
        {
            ob.LastFrame = frame.ToArray();
            ob.ChangedStamp = now;
            ob.FrameVersion++;
            if (!ob.RunActive)
            {
                ob.RunActive = true;
                ob.RunStart = now;
            }
            else if (!ob.AnimatedLogged && StampsToMs(now - ob.RunStart) >= AnimatedLogMs)
            {
                ob.AnimatedLogged = true;
                ServiceLog.Info("[galleon-sd-onboard] the frame keeps changing (animated effect); nothing is written until it holds still");
            }
            return;
        }
        if (StampsToMs(now - ob.ChangedStamp) < DebounceMs) return;
        ob.RunActive = false;
        if (ob.DoneVersion == ob.FrameVersion || now < ob.NextTxStamp) return;

        var desired = GalleonSdOnboardCodec.Quantize(ob.LastFrame);
        if (ob.LastLayers is not null && SameLayers(desired, ob.LastLayers))
        {
            ob.DoneVersion = ob.FrameVersion;
            return;
        }
        if (!CapAllows(ob, now))
        {
            _savedBlocked ??= SavedBlockedWriteCap;
            return;
        }
        var bodies = desired.Select(GalleonSdOnboardCodec.BuildLayerBody).ToList();

        ob.ReRun = false;
        var ok = RunOnboardTx(slot, "update", () => UpdateTxBody(slot, bodies));
        var after = _clock();
        if (ok)
        {
            // A profile switch undid the old layers first; the same frame is written again on the next pass.
            if (!ob.ReRun)
            {
                ob.DoneVersion = ob.FrameVersion;
                ob.LastLayers = desired;
            }
            ob.Failures = 0;
            ob.NextTxStamp = after + (long)(MinTxGapMs * Stopwatch.Frequency / 1000);
            return;
        }
        ob.Failures++;
        var waitMs = ob.LastDeterministic ? MaxRejectBackoffMs : BackoffMs(ob.Failures);
        ob.NextTxStamp = after + (long)(waitMs * Stopwatch.Frequency / 1000);
        if (ob.Failures >= MaxOnboardFailures)
        {
            ob.GaveUp = true;
            _savedBlocked = SavedBlockedWriteFailed;
            ServiceLog.Error($"[galleon-sd-onboard] giving up writing saved colors after {ob.Failures} failed transactions; toggle saved colors off and on to retry");
        }
    }

    // Same keys per layer and every channel within a few steps: brightness drift does not commit a write.
    private static bool SameLayers(List<GalleonQuantLayer> a, List<GalleonQuantLayer> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var l in a)
        {
            var m = b.FirstOrDefault(x => x.Keys.AsSpan().SequenceEqual(l.Keys));
            if (m is null) return false;
            if (Math.Abs(l.Color.R - m.Color.R) > ColorTolerance || Math.Abs(l.Color.G - m.Color.G) > ColorTolerance || Math.Abs(l.Color.B - m.Color.B) > ColorTolerance) return false;
        }
        return true;
    }

    // Long-window cap on transaction attempts; logs once per window it is hit.
    private bool CapAllows(OnboardState ob, long now)
    {
        var day = (long)(24 * 3600.0 * Stopwatch.Frequency);
        var hour = (long)(3600.0 * Stopwatch.Frequency);
        ob.TxStamps.RemoveAll(t => now - t > day);
        var inHour = ob.TxStamps.Count(t => now - t <= hour);
        if (inHour >= MaxTxPerHour || ob.TxStamps.Count >= MaxTxPerDay)
        {
            if (!ob.CapLogged)
            {
                ob.CapLogged = true;
                ServiceLog.Warn($"[galleon-sd-onboard] transaction cap reached (hour={inHour} day={ob.TxStamps.Count}); saved colors wait until the window frees");
            }
            return false;
        }
        ob.CapLogged = false;
        ob.TxStamps.Add(now);
        return true;
    }

    private static double BackoffMs(int failures) =>
        Math.Min(MaxRejectBackoffMs, MinRetryMs * (1 << Math.Clamp(failures - 1, 0, MaxBackoffDoublings)));

    // Turn-off: undo once per saved-off transition when Nexus layers are on record, retried with backoff, then streaming resumes regardless.
    private void UndoOnboardIfNeededLocked(Slot slot)
    {
        var ob = slot.Ob;
        if (!ob.UndoChecked)
        {
            ob.UndoChecked = true;
            ob.UndoPending = _onboard.Get(slot.Info.Serial) is { Owned.Count: > 0 };
            if (ob.UndoPending) ob.LogBudget = MaxLogLines;
        }
        if (!ob.UndoPending || ob.UndoGaveUp) return;
        if (slot.NegotiationFailures > 0 || slot.ForeignHandle) return;
        var now = _clock();
        if (now < ob.NextUndoStamp) return;

        var ok = RunOnboardTx(slot, "undo", () => UndoTxBody(slot));
        if (ok && _onboard.Get(slot.Info.Serial) is not { Owned.Count: > 0 })
        {
            ob.UndoPending = false;
            return;
        }
        ob.UndoFailures++;
        var undoWaitMs = ob.LastDeterministic ? MaxRejectBackoffMs : BackoffMs(ob.UndoFailures);
        ob.NextUndoStamp = _clock() + (long)(undoWaitMs * Stopwatch.Frequency / 1000);
        if (ob.UndoFailures >= MaxOnboardFailures)
        {
            ob.UndoGaveUp = true;
            ServiceLog.Error("[galleon-sd-onboard] giving up undoing saved colors; the Nexus layers stay on the keyboard and the original list is in the stored backup");
        }
    }

    private bool RunOnboardTx(Slot slot, string kind, Func<bool> body)
    {
        var ob = slot.Ob;
        SettleInput(slot);
        ob.OpenHandle = -1;
        ob.InTail = false;
        ob.TxDeadline = _clock() + (long)(TxBudgetMs * Stopwatch.Frequency / 1000);
        ServiceLog.Info($"[galleon-sd-onboard] {kind} begin serial={slot.Info.Serial}");
        var ok = false;
        var ioFailure = false;
        ob.LastDeterministic = false;
        try { ok = body(); }
        catch (TxAbort ex)
        {
            ob.LastDeterministic = ex.Deterministic;
            ServiceLog.Warn($"[galleon-sd-onboard] {kind} aborted: {ex.Message}");
        }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex)
        {
            ioFailure = true;
            ServiceLog.Warn($"[galleon-sd-onboard] {kind} failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (!ok)
        {
            ob.Unsettled = true;
            if (ob.OpenHandle >= 0)
            {
                try { Exchange(slot, GalleonSdProtocol.StopTransaction((byte)ob.OpenHandle), $"onboard abort close h={ob.OpenHandle}", ProbeReplyMs, log: true); }
                catch (Exception) { /* the device is gone; the failure count below handles it */ }
                ob.OpenHandle = -1;
            }
            if (slot.ModeTouched || slot.SoftwareMode)
            {
                try { RestoreHardwareLocked(slot, "abort"); }
                catch (Exception) { slot.HardwareHeld = false; }
            }
            if (ioFailure) RecordFailureLocked(slot, "onboard-io");
        }
        ServiceLog.Info($"[galleon-sd-onboard] {kind} end ok={ok}");
        if (ok && PostTxProbe && !_stopRequested) RunProbe(slot);
        return ok;
    }

    private bool RestoreHardwareLocked(Slot slot, string tag)
    {
        slot.SoftwareMode = false;
        slot.HasLast = false;
        var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), $"onboard {tag} mode->hardware", SlowReplyMs, log: true, drainFirst: true);
        var confirmed = RestoreConfirmed(reply);
        if (confirmed)
        {
            slot.Failures = 0;
            slot.ModeTouched = false;
            slot.HardwareHeld = true;
        }
        else
        {
            slot.HardwareHeld = false;
            ServiceLog.Warn($"[galleon-sd-onboard] hardware-mode restore answered status {reply.Status:X2}; will retry");
        }
        return confirmed;
    }

    // ---- step helpers (throw TxAbort) ----

    private bool Lg(Slot slot)
    {
        var ob = slot.Ob;
        if (ob.LogBudget > 0) return ob.LogBudget-- > 0;
        if (ob.LogBudget == 0)
        {
            ob.LogBudget = -1;
            ServiceLog.Info("[galleon-sd-onboard] wire log budget reached; further steps are not logged");
        }
        return false;
    }

    private void Step(Slot slot)
    {
        if (_stopRequested) throw new TxAbort("shutting down");
        // Once the list/profile/registry tail has started it is finished; only the stop request can still end it, between resources.
        if (!slot.Ob.InTail && _clock() > slot.Ob.TxDeadline) throw new TxAbort("time budget exceeded");
    }

    private GalleonReply TxEx(Slot slot, byte[] wire, string label, bool force = false, int replyMs = ProbeReplyMs)
    {
        Step(slot);
        return Exchange(slot, wire, $"onboard {label}", replyMs, Lg(slot) || force, check: true, drainFirst: true);
    }

    private static ReadOnlySpan<byte> Wire(Slot slot) => GalleonSdOnboardProbe.Strip(slot.ParsedReply.AsSpan(0, slot.ParsedReplyLength));

    private void EnterSoftware(Slot slot)
    {
        slot.ModeTouched = true;
        slot.HardwareHeld = false;
        var reply = TxEx(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeSoftware), "mode->software");
        if (reply.Parsed && reply.Status != 0) throw new TxAbort($"software mode refused (status {reply.Status:X2})");
    }

    private OpenOutcome TxOpen(Slot slot, byte handle, ushort res, string tag, bool force = false)
    {
        Step(slot);
        var reply = Exchange(slot, GalleonSdProtocol.StartTransaction(handle, res), $"onboard {tag} open", ProbeReplyMs, Lg(slot) || force, check: true, drainFirst: true);
        if (reply.IsOk)
        {
            slot.Ob.OpenHandle = handle;
            return OpenOutcome.Ok;
        }
        return reply.Parsed ? OpenOutcome.Refused : OpenOutcome.Silent;
    }

    private void TxOpenRequired(Slot slot, byte handle, ushort res, string tag, bool refusalIsFinal = true)
    {
        var outcome = TxOpen(slot, handle, res, tag);
        if (outcome != OpenOutcome.Ok) throw new TxAbort($"{tag} open {outcome.ToString().ToLowerInvariant()}", refusalIsFinal && outcome == OpenOutcome.Refused);
    }

    // A close after a write commits it, and the board can answer it long after a normal reply window.
    private void TxClose(Slot slot, byte handle, string tag, int replyMs = ProbeReplyMs)
    {
        var reply = Exchange(slot, GalleonSdProtocol.StopTransaction(handle), $"onboard {tag} close", replyMs, Lg(slot), check: true, drainFirst: true);
        slot.Ob.OpenHandle = -1;
        if (!reply.IsOk) throw new TxAbort($"{tag} close not confirmed (parsed={reply.Parsed} status={reply.Status:X2})");
    }

    // Proves the handle is usable: a known resource opens and closes with confirmed OK replies. A refusal on this handle can mean busy, not missing.
    private void HandleCheck(Slot slot, byte handle, ushort knownRes, string tag)
    {
        TxOpenRequired(slot, handle, knownRes, $"{tag} handle check", refusalIsFinal: false);
        TxClose(slot, handle, $"{tag} handle check");
    }

    private void TxCloseQuiet(Slot slot, byte handle, string tag)
    {
        try { TxClose(slot, handle, tag); }
        catch (Exception) { /* the original failure is reported */ }
    }

    // Full read of one resource: open, size, reads until the size is in hand, close. A refused open is null when optional.
    private byte[]? TxRead(Slot slot, ushort res, string tag, bool optional = false)
    {
        var outcome = TxOpen(slot, ReadHandle, res, tag);
        if (outcome == OpenOutcome.Refused && optional) return null;
        if (outcome != OpenOutcome.Ok) throw new TxAbort($"{tag} open {outcome.ToString().ToLowerInvariant()}", outcome == OpenOutcome.Refused);
        try
        {
            var sizeReply = TxEx(slot, GalleonSdProtocol.ResourceSize(ReadHandle), $"{tag} size");
            var w = Wire(slot);
            if (!sizeReply.IsOk || w.Length < 8) throw new TxAbort($"{tag} size unreadable");
            var size = (w[5] << 16) | (w[6] << 8) | w[7];
            if (size < 0 || size > MaxObjectBytes) throw new TxAbort($"{tag} size {size} out of range", deterministic: true);
            if (size == 0)
            {
                TxClose(slot, ReadHandle, tag);
                return Array.Empty<byte>();
            }
            var data = new byte[size];
            var got = 0;
            for (var i = 0; i < MaxReadChunks && got < size; i++)
            {
                var read = TxEx(slot, GalleonSdProtocol.ResourceRead(ReadHandle), $"{tag} read {i}");
                if (!read.IsOk) break;
                var r = Wire(slot);
                var take = Math.Min(Math.Min(ReadChunkBytes, size - got), r.Length - 5);
                if (take <= 0) break;
                r.Slice(5, take).CopyTo(data.AsSpan(got));
                got += take;
            }
            if (got < size) throw new TxAbort($"{tag} short read {got}/{size}");
            TxClose(slot, ReadHandle, tag);
            return data;
        }
        catch (Exception)
        {
            if (slot.Ob.OpenHandle >= 0) TxCloseQuiet(slot, ReadHandle, tag);
            throw;
        }
    }

    // The 06 packet and its 07 continuations always go out together: stop and deadline are checked only before the block.
    private void TxWrite(Slot slot, byte handle, ushort res, byte[] data, string tag)
    {
        TxOpenRequired(slot, handle, res, tag);
        try
        {
            var log = Lg(slot);
            var chunks = GalleonSdProtocol.BlockWrite(handle, data);
            Step(slot);
            var ack = Exchange(slot, chunks[0], $"onboard {tag} write first", ProbeReplyMs, log, check: true, drainFirst: true);
            for (var i = 1; i < chunks.Count; i++)
            {
                if (!SendWire(slot, chunks[i], $"onboard {tag} write next {i}", log)) throw new InvalidOperationException($"write failed: {tag}");
                PaceWait();
            }
            DrainInput(slot);
            if (!ack.IsOk) throw new TxAbort($"{tag} write not acknowledged (parsed={ack.Parsed} status={ack.Status:X2})");
            TxClose(slot, handle, tag, SlowReplyMs);
        }
        catch (Exception)
        {
            if (slot.Ob.OpenHandle >= 0) TxCloseQuiet(slot, handle, tag);
            throw;
        }
        VerifyWrite(slot, res, data, tag);
    }

    private void VerifyWrite(Slot slot, ushort res, byte[] expected, string tag)
    {
        if (!VerifyOnboardWrites) return;
        try
        {
            var back = TxRead(slot, res, $"{tag} readback") ?? Array.Empty<byte>();
            if (back.AsSpan().SequenceEqual(expected))
            {
                ServiceLog.Info($"[galleon-sd-onboard] verify {tag} {res:X4} ok ({expected.Length} bytes)");
                return;
            }
            VerifyMismatchCount++;
            ServiceLog.Warn($"[galleon-sd-onboard] verify {tag} {res:X4} MISMATCH wrote={Hex(expected.AsSpan(0, Math.Min(expected.Length, 160)))} read={Hex(back.AsSpan(0, Math.Min(back.Length, 160)))}");
        }
        catch (TxAbort ex)
        {
            ServiceLog.Warn($"[galleon-sd-onboard] verify {tag} {res:X4} could not read back: {ex.Message}");
        }
    }


    // Reads registry, the profile to edit and its list. While Nexus layers are recorded on a profile, that profile is the
    // one edited, even when another is active now; Mismatch tells the caller.
    private BoardView ReadBoard(Slot slot, GalleonOnboardRecord? rec)
    {
        var view = new BoardView();
        var index = ReadActiveIndex(slot);

        view.Registry = TxRead(slot, GalleonSdOnboardProbe.RegistryResource, "registry")!;
        if (!GalleonSdOnboardCodec.TryParseRegistry(view.Registry, out view.RegistryRefs)) throw new TxAbort("registry layout not recognized", deterministic: true);
        if (index >= view.RegistryRefs.Length || index >= GalleonSdOnboardProbe.MaxProfiles)
        {
            // Either the board points at a profile slot with no stored profile, or the reply carried stray bytes after its status.
            // Ask once more; a second out-of-range answer aborts with nothing written.
            var again = ReadActiveIndex(slot);
            if (again >= view.RegistryRefs.Length || again >= GalleonSdOnboardProbe.MaxProfiles) throw new TxAbort($"active profile index {again} not in the registry", deterministic: true);
            ServiceLog.Warn($"[galleon-sd-onboard] active profile index read {index} outside the registry, then {again}; using {again}");
            index = again;
        }
        view.ActiveRes = view.RegistryRefs[index];
        view.ProfileRes = view.ActiveRes;
        if (rec is { Owned.Count: > 0, ProfileRes: > 0 } && rec.ProfileRes != view.ActiveRes)
        {
            if (view.RegistryRefs.Take(GalleonSdOnboardProbe.MaxProfiles).Contains((ushort)rec.ProfileRes))
            {
                view.ProfileRes = (ushort)rec.ProfileRes;
                view.Mismatch = true;
            }
            else
            {
                view.RecordedGone = true;
            }
        }

        view.Profile = TxRead(slot, view.ProfileRes, "profile")!;
        if (!GalleonSdOnboardCodec.TryParseProfile(view.Profile, out _, out view.ProfileSlots)) throw new TxAbort("profile layout not recognized", deterministic: true);
        view.ListRes = view.ProfileSlots[GalleonSdOnboardProbe.LayerListSlot];
        if (view.ListRes == 0) throw new TxAbort("profile has no layer list", deterministic: true);

        view.ListBytes = TxRead(slot, view.ListRes, "list")!;
        if (!GalleonSdOnboardCodec.TryParseLayerList(view.ListBytes, out view.ListRefs)) throw new TxAbort("layer list layout not recognized", deterministic: true);
        ServiceLog.Info($"[galleon-sd-onboard] board registry={FormatRefs(view.RegistryRefs)} active={index} profile={view.ProfileRes:X4} list={view.ListRes:X4} layers={FormatRefs(view.ListRefs)} mismatch={view.Mismatch}");
        return view;
    }

    private int ReadActiveIndex(Slot slot)
    {
        var reply = TxEx(slot, GalleonSdProtocol.GetProperty(GalleonSdProtocol.PropActiveProfile), "get 6E");
        var w = Wire(slot);
        if (!reply.IsOk || w.Length < 6) throw new TxAbort("active profile unreadable");
        return w[5];
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b);

    private static byte[]? FromB64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    // Persisted ids sorted by what the board shows: ours (body equals the confirmed or the pending one, or the layer is empty because a
    // create was never followed by its write), gone (open refused), or foreign (someone else changed it; left alone and left in the list).
    private (List<(ushort Id, byte[] Body)> Verified, List<ushort> Gone) VerifyOwned(Slot slot, GalleonOnboardRecord rec, Dictionary<ushort, byte[]> present, ushort knownRes)
    {
        var verified = new List<(ushort Id, byte[] Body)>();
        var gone = new List<ushort>();
        foreach (var o in rec.Owned)
        {
            if (o.Id is <= 0 or > ushort.MaxValue) continue;
            var id = (ushort)o.Id;
            var confirmed = FromB64(o.BodyB64);
            var pending = o.PendingB64.Length > 0 ? FromB64(o.PendingB64) : null;
            if (!present.TryGetValue(id, out var body))
            {
                body = TxRead(slot, id, $"owned {id:X4}", optional: true);
                if (body is not null) present[id] = body;
            }
            if (body is null)
            {
                // A refusal can also mean a busy handle; only a healthy handle makes it proof the layer is gone.
                HandleCheck(slot, ReadHandle, knownRes, $"owned {id:X4}");
                ServiceLog.Info($"[galleon-sd-onboard] owned layer {id:X4} no longer exists; forgotten");
                gone.Add(id);
                continue;
            }
            var matches = body.Length == 0
                || (confirmed is not null && body.AsSpan().SequenceEqual(confirmed))
                || (pending is not null && body.AsSpan().SequenceEqual(pending));
            if (matches)
            {
                verified.Add((id, body));
                continue;
            }
            ServiceLog.Warn($"[galleon-sd-onboard] layer {id:X4} no longer matches what Nexus wrote; treating it as foreign and leaving it alone");
        }
        return (verified, gone);
    }

    private void SetOwned(string serial, GalleonOnboardRecord rec, ushort id, string body, string pending, bool flush)
    {
        var entry = rec.Owned.FirstOrDefault(o => o.Id == id);
        if (entry is null)
        {
            entry = new GalleonOwnedLayer { Id = id };
            rec.Owned.Add(entry);
        }
        entry.BodyB64 = body;
        entry.PendingB64 = pending;
        _onboard.Save(serial, rec, flush);
    }

    private bool UpdateTxBody(Slot slot, List<byte[]> bodies)
    {
        var ob = slot.Ob;
        var serial = slot.Info.Serial;
        var rec = _onboard.Get(serial) ?? new GalleonOnboardRecord();
        var view = ReadBoard(slot, rec);
        if (view.Mismatch)
        {
            ServiceLog.Info($"[galleon-sd-onboard] Nexus layers are on profile {view.ProfileRes:X4} but {view.ActiveRes:X4} is active; undoing there first");
            UndoOnView(slot, view, rec);
            ob.ReRun = true;
            return true;
        }
        if (view.RecordedGone)
        {
            ServiceLog.Warn("[galleon-sd-onboard] the recorded profile is no longer in the registry; forgetting its owned layers");
            rec.Owned = new List<GalleonOwnedLayer>();
        }
        var persistedIds = rec.Owned.Where(o => o.Id is > 0 and <= ushort.MaxValue).Select(o => (ushort)o.Id).ToList();

        var present = new Dictionary<ushort, byte[]>();
        if (!ob.LayersLogged)
        {
            foreach (var id in view.ListRefs.Take(MaxLayerReads)) ReadListedLayer(slot, id, present, view.ListRes);
            ob.LayersLogged = true;
        }
        var others = ReserveAcrossProfiles(slot, view, includeEdited: false);
        var (verifiedAll, gone) = VerifyOwned(slot, rec, present, view.ListRes);
        // A layer another profile references is never rewritten or deleted from here; it stays recorded.
        var shared = verifiedAll.Where(o => others.Contains(o.Id)).ToList();
        var owned = verifiedAll.Where(o => !others.Contains(o.Id)).ToList();
        foreach (var sh in shared) ServiceLog.Warn($"[galleon-sd-onboard] layer {sh.Id:X4} is referenced by another profile; left alone and kept recorded");

        // The backup is the board as found: kept while any Nexus layer is still verifiably on it, refreshed otherwise. It lands on disk before any write.
        if (verifiedAll.Count == 0 || view.RecordedGone || !rec.HasBackup())
        {
            rec.ProfileB64 = B64(view.Profile);
            rec.ListB64 = B64(view.ListBytes);
            rec.RegistryB64 = B64(view.Registry);
        }
        rec.ProfileRes = view.ProfileRes;
        rec.ListRes = view.ListRes;
        rec.Owned = verifiedAll.Select(o => new GalleonOwnedLayer { Id = o.Id, BodyB64 = B64(o.Body) }).ToList();
        _onboard.Save(serial, rec, flush: true);

        // Stable assignment: a color keeps its layer; leftovers take free owned layers (empty ones included); extras are created or deleted.
        var assigned = new ushort?[bodies.Count];
        var pool = owned.ToList();
        for (var d = 0; d < bodies.Count; d++)
        {
            if (!GalleonSdOnboardCodec.TryReadLayerColor(bodies[d], out var color)) continue;
            var at = pool.FindIndex(o => GalleonSdOnboardCodec.TryReadLayerColor(o.Body, out var c) && c == color);
            if (at < 0) continue;
            assigned[d] = pool[at].Id;
            pool.RemoveAt(at);
        }
        pool.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (var d = 0; d < bodies.Count && pool.Count > 0; d++)
        {
            if (assigned[d] is not null) continue;
            assigned[d] = pool[0].Id;
            pool.RemoveAt(0);
        }
        var toDelete = pool.Select(p => p.Id).ToList();
        var existing = owned.ToDictionary(o => o.Id, o => o.Body);

        var ours = new HashSet<ushort>(verifiedAll.Select(o => o.Id).Concat(gone));
        if (NeedsWrite(assigned, bodies, existing, toDelete, rec, view, ours))
        {
            EnterSoftware(slot);
        }
        else
        {
            ServiceLog.Info("[galleon-sd-onboard] board already holds these layers; nothing to write");
            rec.Owned = assigned.Select((a, i) => new GalleonOwnedLayer { Id = a!.Value, BodyB64 = B64(bodies[i]) })
                .Concat(shared.Select(o => new GalleonOwnedLayer { Id = o.Id, BodyB64 = B64(o.Body) })).ToList();
            rec.WrittenListB64 = B64(view.ListBytes);
            _onboard.Save(serial, rec, flush: true);
            return true;
        }

        HashSet<ushort>? reserved = null;
        for (var d = 0; d < bodies.Count; d++)
        {
            if (assigned[d] is { } id)
            {
                if (existing[id].AsSpan().SequenceEqual(bodies[d])) continue;
                SetOwned(serial, rec, id, B64(existing[id]), B64(bodies[d]), flush: true);
                TxWrite(slot, LayerHandle, id, bodies[d], $"layer {id:X4}");
                SetOwned(serial, rec, id, B64(bodies[d]), "", flush: false);
                continue;
            }
            if (reserved is null)
            {
                reserved = new HashSet<ushort>(others);
                foreach (var r in view.ProfileSlots) if (r != 0) reserved.Add(r);
                foreach (var r in view.ListRefs) reserved.Add(r);
                foreach (var g in persistedIds.Concat(gone)) reserved.Add(g);
                foreach (var o in verifiedAll) reserved.Add(o.Id);
            }
            var fresh = AllocateId(slot, reserved, view.ListRes);
            SetOwned(serial, rec, fresh, "", B64(bodies[d]), flush: true);
            var created = TxEx(slot, GalleonSdProtocol.CreateResource(fresh), $"create {fresh:X4}", force: true);
            if (!created.IsOk) throw new TxAbort($"create {fresh:X4} not accepted (parsed={created.Parsed} status={created.Status:X2}); the layer stays recorded so undo can remove it");
            TxWrite(slot, LayerHandle, fresh, bodies[d], $"layer {fresh:X4}");
            SetOwned(serial, rec, fresh, B64(bodies[d]), "", flush: false);
            assigned[d] = fresh;
        }

        // Every id of ours, including ones that vanished, is removed from the list; foreign entries stay exactly as they were.
        var finalIds = assigned.Select(a => a!.Value).ToList();
        var newList = finalIds.Concat(view.ListRefs.Where(r => !ours.Contains(r))).ToList();
        if (newList.Count > GalleonSdOnboardCodec.MaxListLayers) throw new TxAbort("layer list would exceed the supported length", deterministic: true);
        var listBytes = GalleonSdOnboardCodec.BuildLayerList(newList);
        // The list stops referencing a layer before the layer is deleted.
        WriteTail(slot, view, listBytes, () =>
        {
            foreach (var id in toDelete)
            {
                DeleteOwned(slot, id);
                rec.Owned.RemoveAll(o => o.Id == id);
                _onboard.Save(serial, rec);
            }
        }, rec);
        rec.Owned = finalIds.Select((id, i) => new GalleonOwnedLayer { Id = id, BodyB64 = B64(bodies[i]) })
            .Concat(shared.Select(o => new GalleonOwnedLayer { Id = o.Id, BodyB64 = B64(o.Body) })).ToList();
        rec.WrittenListB64 = B64(listBytes);
        _onboard.Save(serial, rec, flush: true);
        return true;
    }

    // Reads of a listed layer are optional: a refused or malformed foreign entry is opaque and stays in the list untouched.
    private void ReadListedLayer(Slot slot, ushort id, Dictionary<ushort, byte[]> present, ushort knownRes)
    {
        try
        {
            var body = TxRead(slot, id, $"layer {id:X4}", optional: true);
            if (body is not null)
            {
                present[id] = body;
            }
            else
            {
                // A refusal can also mean a busy handle; only a healthy handle makes it an opaque entry.
                HandleCheck(slot, ReadHandle, knownRes, $"layer {id:X4}");
                ServiceLog.Info($"[galleon-sd-onboard] layer {id:X4} refused its open; treated as opaque and left in the list");
            }
        }
        catch (TxAbort ex) when (ex.Deterministic)
        {
            ServiceLog.Info($"[galleon-sd-onboard] layer {id:X4} unreadable ({ex.Message}); treated as opaque and left in the list");
        }
    }

    // True when a layer write, create, delete, unfinished tail or list change is due; the board then needs software mode.
    private static bool NeedsWrite(ushort?[] assigned, List<byte[]> bodies, Dictionary<ushort, byte[]> existing, List<ushort> toDelete, GalleonOnboardRecord rec, BoardView view, HashSet<ushort> ours)
    {
        if (toDelete.Count > 0 || rec.TailPending) return true;
        var ids = new List<ushort>();
        for (var d = 0; d < bodies.Count; d++)
        {
            if (assigned[d] is not { } id || !existing[id].AsSpan().SequenceEqual(bodies[d])) return true;
            ids.Add(id);
        }
        var list = GalleonSdOnboardCodec.BuildLayerList(ids.Concat(view.ListRefs.Where(r => !ours.Contains(r))).ToList());
        return !list.AsSpan().SequenceEqual(view.ListBytes);
    }

    private void DeleteOwned(Slot slot, ushort id)
    {
        var reply = TxEx(slot, GalleonSdProtocol.DeleteResource(id), $"delete {id:X4}");
        if (!reply.IsOk) throw new TxAbort($"delete {id:X4} not accepted (parsed={reply.Parsed} status={reply.Status:X2})");
    }

    // Every id the board references anywhere: the registry, every profile's slots, every profile's list. Over-reserving is harmless.
    private HashSet<ushort> ReserveAcrossProfiles(Slot slot, BoardView view, bool includeEdited = true)
    {
        var reserved = new HashSet<ushort>(view.RegistryRefs);
        for (var i = 0; i < Math.Min(view.RegistryRefs.Length, GalleonSdOnboardProbe.MaxProfiles); i++)
        {
            var res = view.RegistryRefs[i];
            ushort[] slots;
            ushort[] list = Array.Empty<ushort>();
            if (res == view.ProfileRes)
            {
                if (!includeEdited) continue;
                slots = view.ProfileSlots;
                list = view.ListRefs;
            }
            else
            {
                var pb = TxRead(slot, res, $"profile {res:X4}")!;
                if (!GalleonSdOnboardCodec.TryParseProfile(pb, out _, out slots)) throw new TxAbort($"profile {res:X4} layout not recognized", deterministic: true);
                var listRes = slots[GalleonSdOnboardProbe.LayerListSlot];
                if (listRes != 0)
                {
                    var lb = TxRead(slot, listRes, $"list {listRes:X4}")!;
                    if (!GalleonSdOnboardCodec.TryParseLayerList(lb, out list)) throw new TxAbort($"list {listRes:X4} layout not recognized", deterministic: true);
                }
            }
            foreach (var r in slots) if (r != 0) reserved.Add(r);
            foreach (var r in list) reserved.Add(r);
        }
        return reserved;
    }

    // A free id: above every observed id, referenced nowhere, and refusing an open on a handle proven usable. An OK open means the
    // resource exists (closed again, next id); silence is ambiguous and aborts. The raw probe-open replies are always logged.
    private ushort AllocateId(Slot slot, HashSet<ushort> reserved, ushort knownRes)
    {
        var inRange = reserved.Where(r => r >= AllocMin && r <= AllocMax).ToList();
        if (inRange.Count == 0) throw new TxAbort("no observed resource id range to allocate from");
        var start = inRange.Max() + 1;
        for (var n = 0; n < MaxAllocProbes; n++)
        {
            var id = start + n;
            if (id > AllocMax) break;
            if (reserved.Contains((ushort)id)) continue;
            HandleCheck(slot, LayerHandle, knownRes, $"probe {id:X4}");
            var outcome = TxOpen(slot, LayerHandle, (ushort)id, $"probe {id:X4}", force: true);
            reserved.Add((ushort)id);
            switch (outcome)
            {
                case OpenOutcome.Ok:
                    TxClose(slot, LayerHandle, $"probe {id:X4}");
                    continue;
                case OpenOutcome.Refused:
                    // The handle must still be healthy afterwards, or the refusal may have been a busy one.
                    HandleCheck(slot, LayerHandle, knownRes, $"probe {id:X4} after");
                    return (ushort)id;
                default:
                    throw new TxAbort($"probe-open of {id:X4} unanswered; its state is ambiguous");
            }
        }
        throw new TxAbort("no free resource id found");
    }

    // Layer list, then (when given) the deletes it made safe, software mode again, profile with a new timestamp, hardware mode, registry unchanged, hardware mode.
    private void WriteTail(Slot slot, BoardView view, byte[] listBytes, Action? afterList, GalleonOnboardRecord rec)
    {
        slot.Ob.InTail = true;
        // Set before the list changes and cleared only when the tail completes, so an aborted tail is finished by the next run
        // even when the board already holds the wanted layers.
        rec.TailPending = true;
        _onboard.Save(slot.Info.Serial, rec, flush: true);
        TxWrite(slot, ListHandle, view.ListRes, listBytes, "list");
        afterList?.Invoke();

        EnterSoftware(slot);
        var profile = TxRead(slot, view.ProfileRes, "profile")!;
        if (!GalleonSdOnboardCodec.TryParseProfile(profile, out var oldStamp, out var slots)) throw new TxAbort("profile layout not recognized on re-read");
        if (slots[GalleonSdOnboardProbe.LayerListSlot] != view.ListRes) throw new TxAbort("profile changed under us; layer list slot moved");
        var stamp = (uint)Math.Max(UtcNow().ToUnixTimeSeconds(), (long)oldStamp + 1);
        TxWrite(slot, ProfileHandle, view.ProfileRes, GalleonSdOnboardCodec.WithTimestamp(profile, stamp), "profile");

        var hw = TxEx(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "mode->hardware", replyMs: SlowReplyMs);
        if (hw.Parsed && hw.Status != 0) throw new TxAbort($"hardware mode refused (status {hw.Status:X2})");
        TxWrite(slot, LayerHandle, GalleonSdOnboardProbe.RegistryResource, view.Registry, "registry");
        RestoreHardwareLocked(slot, "done");
        rec.TailPending = false;
    }

    private bool UndoTxBody(Slot slot)
    {
        var serial = slot.Info.Serial;
        var rec = _onboard.Get(serial);
        if (rec is null || rec.Owned.Count == 0) return true;

        var view = ReadBoard(slot, rec);
        if (view.RecordedGone)
        {
            ServiceLog.Warn("[galleon-sd-onboard] the recorded profile is no longer in the registry; nothing to undo there");
            rec.Owned = new List<GalleonOwnedLayer>();
            rec.WrittenListB64 = "";
            _onboard.Save(serial, rec, flush: true);
            return true;
        }
        UndoOnView(slot, view, rec);
        return true;
    }

    // Writes the list without our ids first, then deletes the layers, so the list never points at a deleted layer.
    private void UndoOnView(Slot slot, BoardView view, GalleonOnboardRecord rec)
    {
        var serial = slot.Info.Serial;
        var others = ReserveAcrossProfiles(slot, view, includeEdited: false);
        var (verified, gone) = VerifyOwned(slot, rec, new Dictionary<ushort, byte[]>(), view.ListRes);
        var ours = new HashSet<ushort>(verified.Select(v => v.Id).Concat(gone));
        // A layer another profile references is not deleted; it stays recorded and the undo is retried later.
        var kept = verified.Where(v => others.Contains(v.Id)).ToList();
        foreach (var k in kept) ServiceLog.Warn($"[galleon-sd-onboard] layer {k.Id:X4} is referenced by another profile; not deleting it, will retry");
        var deletable = verified.Where(v => !others.Contains(v.Id)).ToList();
        var restored = view.ListRefs.Where(r => !ours.Contains(r)).ToList();
        var backupList = FromB64(rec.ListB64);
        ServiceLog.Info($"[galleon-sd-onboard] undo list={FormatRefs(restored.ToArray())} equalsBackup={backupList is not null && GalleonSdOnboardCodec.BuildLayerList(restored).AsSpan().SequenceEqual(backupList)}");

        var listBytes = GalleonSdOnboardCodec.BuildLayerList(restored);
        if (verified.Count > 0 || rec.TailPending || !listBytes.AsSpan().SequenceEqual(view.ListBytes))
        {
            EnterSoftware(slot);
            WriteTail(slot, view, listBytes, () =>
            {
                foreach (var v in deletable) DeleteOwned(slot, v.Id);
            }, rec);
        }

        rec.Owned = kept.Select(k => new GalleonOwnedLayer { Id = k.Id, BodyB64 = B64(k.Body) }).ToList();
        rec.WrittenListB64 = "";
        _onboard.Save(serial, rec, flush: true);
    }
}
