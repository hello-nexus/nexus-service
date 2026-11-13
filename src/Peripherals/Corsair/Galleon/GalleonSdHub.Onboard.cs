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
    private const double DebounceMs = 5000;
    private const double MinTxGapMs = 5000;
    private const double TxBudgetMs = 12_000;
    private const double AnimatedLogMs = 15_000;
    private const int MaxOnboardFailures = 5;
    private const int MaxLogLines = 700;
    private const int MaxObjectBytes = 2048;
    private const int ReadChunkBytes = GalleonSdProtocol.WireLength - 5;
    private const int MaxReadChunks = 24;
    private const int MaxLayerReads = 16;
    private const int MaxAllocProbes = 24;
    private const byte ReadHandle = 0;
    private const byte LayerHandle = 1;
    private const byte ListHandle = 2;
    private const byte ProfileHandle = 3;

    private readonly GalleonOnboardStore _onboard;

    /// <summary>Diagnostic: after every onboard write, read the resource back and log whether it matches what was written. Costs one extra read per write.</summary>
    public bool VerifyOnboardWrites { get; set; } = true;

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
        public byte[]? LastSig;
        public bool LayersLogged;
        public int LogBudget = MaxLogLines;
        public int OpenHandle = -1;
        public long TxDeadline;
        public bool UndoChecked;
        public bool UndoPending;
        public bool UndoGaveUp;
        public int UndoFailures;
        public long NextUndoStamp;

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
            LastSig = null;
            LayersLogged = false;
            LogBudget = MaxLogLines;
            UndoChecked = false;
            UndoPending = false;
            UndoGaveUp = false;
            UndoFailures = 0;
            NextUndoStamp = 0;
        }
    }

    private sealed class TxAbort : Exception
    {
        public TxAbort(string message) : base(message) { }
    }

    private enum OpenOutcome { Ok, Refused, Silent }

    private sealed class BoardView
    {
        public ushort[] RegistryRefs = Array.Empty<ushort>();
        public byte[] Registry = Array.Empty<byte>();
        public ushort ProfileRes;
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
        var bodies = desired.Select(GalleonSdOnboardCodec.BuildLayerBody).ToList();
        var sig = Signature(bodies);
        if (ob.LastSig is not null && sig.AsSpan().SequenceEqual(ob.LastSig))
        {
            ob.DoneVersion = ob.FrameVersion;
            return;
        }

        var ok = RunOnboardTx(slot, "update", () => UpdateTxBody(slot, bodies));
        var after = _clock();
        if (ok)
        {
            ob.DoneVersion = ob.FrameVersion;
            ob.Failures = 0;
            ob.LastSig = sig;
            ob.NextTxStamp = after + (long)(MinTxGapMs * Stopwatch.Frequency / 1000);
            return;
        }
        ob.Failures++;
        ob.NextTxStamp = after + (long)(BackoffMs(ob.Failures) * Stopwatch.Frequency / 1000);
        if (ob.Failures >= MaxOnboardFailures)
        {
            ob.GaveUp = true;
            ServiceLog.Error($"[galleon-sd-onboard] giving up writing saved colors after {ob.Failures} failed transactions; toggle saved colors off and on to retry");
        }
    }

    private static double BackoffMs(int failures) =>
        Math.Max(MinTxGapMs, Math.Min(MaxRejectBackoffMs, RejectBackoffMs * (1 << Math.Min(failures, MaxBackoffDoublings))));

    private static byte[] Signature(List<byte[]> bodies)
    {
        var sig = new List<byte>();
        foreach (var b in bodies)
        {
            sig.Add((byte)b.Length);
            sig.AddRange(b);
        }
        return sig.ToArray();
    }

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
        if (ok)
        {
            ob.UndoPending = false;
            return;
        }
        ob.UndoFailures++;
        ob.NextUndoStamp = _clock() + (long)(BackoffMs(ob.UndoFailures) * Stopwatch.Frequency / 1000);
        if (ob.UndoFailures >= MaxOnboardFailures)
        {
            ob.UndoGaveUp = true;
            ServiceLog.Error("[galleon-sd-onboard] giving up undoing saved colors; the Nexus layers stay on the keyboard and the original list is in the stored backup");
        }
    }

    private bool RunOnboardTx(Slot slot, string kind, Func<bool> body)
    {
        var ob = slot.Ob;
        ob.OpenHandle = -1;
        ob.TxDeadline = _clock() + (long)(TxBudgetMs * Stopwatch.Frequency / 1000);
        ServiceLog.Info($"[galleon-sd-onboard] {kind} begin serial={slot.Info.Serial}");
        var ok = false;
        var ioFailure = false;
        try { ok = body(); }
        catch (TxAbort ex) { ServiceLog.Warn($"[galleon-sd-onboard] {kind} aborted: {ex.Message}"); }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex)
        {
            ioFailure = true;
            ServiceLog.Warn($"[galleon-sd-onboard] {kind} failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (!ok)
        {
            if (ob.OpenHandle >= 0)
            {
                try { Exchange(slot, GalleonSdProtocol.StopTransaction((byte)ob.OpenHandle), $"onboard abort close h={ob.OpenHandle}", ProbeReplyMs, log: true); }
                catch (Exception) { /* the device is gone; the failure count below handles it */ }
                ob.OpenHandle = -1;
            }
            try { RestoreHardwareLocked(slot, "abort"); }
            catch (Exception) { slot.HardwareHeld = false; }
            if (ioFailure) RecordFailureLocked(slot, "onboard-io");
        }
        ServiceLog.Info($"[galleon-sd-onboard] {kind} end ok={ok}");
        return ok;
    }

    private bool RestoreHardwareLocked(Slot slot, string tag)
    {
        slot.SoftwareMode = false;
        slot.HasLast = false;
        var reply = Exchange(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), $"onboard {tag} mode->hardware", ProbeReplyMs, log: true);
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
        if (_clock() > slot.Ob.TxDeadline) throw new TxAbort("time budget exceeded");
    }

    private GalleonReply TxEx(Slot slot, byte[] wire, string label)
    {
        Step(slot);
        return Exchange(slot, wire, $"onboard {label}", ProbeReplyMs, Lg(slot), check: true);
    }

    private static ReadOnlySpan<byte> Wire(Slot slot) => GalleonSdOnboardProbe.Strip(slot.ParsedReply.AsSpan(0, slot.ParsedReplyLength));

    private void EnterSoftware(Slot slot)
    {
        slot.ModeTouched = true;
        slot.HardwareHeld = false;
        var reply = TxEx(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeSoftware), "mode->software");
        if (reply.Parsed && reply.Status != 0) throw new TxAbort($"software mode refused (status {reply.Status:X2})");
    }

    private OpenOutcome TxOpen(Slot slot, byte handle, ushort res, string tag)
    {
        Step(slot);
        DrainInput(slot);
        var reply = Exchange(slot, GalleonSdProtocol.StartTransaction(handle, res), $"onboard {tag} open", ProbeReplyMs, Lg(slot), check: true);
        if (reply.IsOk)
        {
            slot.Ob.OpenHandle = handle;
            return OpenOutcome.Ok;
        }
        return reply.Parsed ? OpenOutcome.Refused : OpenOutcome.Silent;
    }

    private void TxOpenRequired(Slot slot, byte handle, ushort res, string tag)
    {
        var outcome = TxOpen(slot, handle, res, tag);
        if (outcome != OpenOutcome.Ok) throw new TxAbort($"{tag} open {outcome.ToString().ToLowerInvariant()}");
    }

    private void TxClose(Slot slot, byte handle, string tag)
    {
        Exchange(slot, GalleonSdProtocol.StopTransaction(handle), $"onboard {tag} close", ProbeReplyMs, Lg(slot), check: true);
        slot.Ob.OpenHandle = -1;
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
        if (outcome != OpenOutcome.Ok) throw new TxAbort($"{tag} open {outcome.ToString().ToLowerInvariant()}");
        try
        {
            var sizeReply = TxEx(slot, GalleonSdProtocol.ResourceSize(ReadHandle), $"{tag} size");
            var w = Wire(slot);
            if (!sizeReply.IsOk || w.Length < 8) throw new TxAbort($"{tag} size unreadable");
            var size = (w[5] << 16) | (w[6] << 8) | w[7];
            if (size <= 0 || size > MaxObjectBytes) throw new TxAbort($"{tag} size {size} out of range");
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

    private void TxWrite(Slot slot, byte handle, ushort res, byte[] data, string tag)
    {
        TxOpenRequired(slot, handle, res, tag);
        try
        {
            var log = Lg(slot);
            var chunks = GalleonSdProtocol.BlockWrite(handle, data);
            for (var i = 0; i < chunks.Count; i++)
            {
                Step(slot);
                if (i == 0)
                {
                    var ack = Exchange(slot, chunks[i], $"onboard {tag} write first", ProbeReplyMs, log, check: true);
                    if (!ack.IsOk) throw new TxAbort($"{tag} write not acknowledged (parsed={ack.Parsed} status={ack.Status:X2})");
                    continue;
                }
                if (!SendWire(slot, chunks[i], $"onboard {tag} write next {i}", log)) throw new InvalidOperationException($"write failed: {tag}");
                PaceWait();
            }
            DrainInput(slot);
            TxClose(slot, handle, tag);
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

    private BoardView ReadBoard(Slot slot)
    {
        var view = new BoardView();
        var active = TxEx(slot, GalleonSdProtocol.GetProperty(GalleonSdProtocol.PropActiveProfile), "get 6E");
        var aw = Wire(slot);
        if (!active.IsOk || aw.Length < 6) throw new TxAbort("active profile unreadable");
        int index = aw[5];

        view.Registry = TxRead(slot, GalleonSdOnboardProbe.RegistryResource, "registry")!;
        if (!GalleonSdOnboardCodec.TryParseRegistry(view.Registry, out view.RegistryRefs)) throw new TxAbort("registry layout not recognized");
        if (index >= view.RegistryRefs.Length || index >= GalleonSdOnboardProbe.MaxProfiles) throw new TxAbort($"active profile index {index} not in the registry");
        view.ProfileRes = view.RegistryRefs[index];

        view.Profile = TxRead(slot, view.ProfileRes, "profile")!;
        if (!GalleonSdOnboardCodec.TryParseProfile(view.Profile, out _, out view.ProfileSlots)) throw new TxAbort("profile layout not recognized");
        view.ListRes = view.ProfileSlots[GalleonSdOnboardProbe.LayerListSlot];
        if (view.ListRes == 0) throw new TxAbort("profile has no layer list");

        view.ListBytes = TxRead(slot, view.ListRes, "list")!;
        if (!GalleonSdOnboardCodec.TryParseLayerList(view.ListBytes, out view.ListRefs)) throw new TxAbort("layer list layout not recognized");
        ServiceLog.Info($"[galleon-sd-onboard] board registry={FormatRefs(view.RegistryRefs)} active={index} profile={view.ProfileRes:X4} list={view.ListRes:X4} layers={FormatRefs(view.ListRefs)}");
        return view;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b);

    private static byte[]? FromB64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    // Persisted ids whose body on the board still equals what was last written. A refused open means the layer is gone; a different body means someone else edited it.
    private List<(ushort Id, byte[] Body)> VerifyOwned(Slot slot, GalleonOnboardRecord rec, Dictionary<ushort, byte[]> present)
    {
        var verified = new List<(ushort Id, byte[] Body)>();
        foreach (var o in rec.Owned)
        {
            if (o.Id is <= 0 or > ushort.MaxValue) continue;
            var id = (ushort)o.Id;
            var expected = FromB64(o.BodyB64);
            if (!present.TryGetValue(id, out var body))
            {
                body = TxRead(slot, id, $"owned {id:X4}", optional: true);
                if (body is not null) present[id] = body;
            }
            if (body is null)
            {
                ServiceLog.Info($"[galleon-sd-onboard] owned layer {id:X4} no longer exists; forgotten");
                continue;
            }
            if (expected is not null && body.AsSpan().SequenceEqual(expected))
            {
                verified.Add((id, body));
                continue;
            }
            ServiceLog.Warn($"[galleon-sd-onboard] layer {id:X4} no longer matches what Nexus wrote; treating it as foreign and leaving it alone");
        }
        return verified;
    }

    private bool UpdateTxBody(Slot slot, List<byte[]> bodies)
    {
        var ob = slot.Ob;
        var serial = slot.Info.Serial;
        EnterSoftware(slot);
        var view = ReadBoard(slot);
        var rec = _onboard.Get(serial) ?? new GalleonOnboardRecord();

        var present = new Dictionary<ushort, byte[]>();
        if (!ob.LayersLogged)
        {
            foreach (var id in view.ListRefs.Take(MaxLayerReads))
            {
                var body = TxRead(slot, id, $"layer {id:X4}")!;
                present[id] = body;
            }
            ob.LayersLogged = true;
        }
        var owned = VerifyOwned(slot, rec, present);

        // The backup is the board as found: kept while Nexus layers are on it, refreshed when none verified. It lands on disk before any write.
        if (owned.Count == 0 || !rec.HasBackup)
        {
            rec.ProfileRes = view.ProfileRes;
            rec.ProfileB64 = B64(view.Profile);
            rec.ListRes = view.ListRes;
            rec.ListB64 = B64(view.ListBytes);
            rec.RegistryB64 = B64(view.Registry);
        }
        rec.Owned = owned.Select(o => new GalleonOwnedLayer { Id = o.Id, BodyB64 = B64(o.Body) }).ToList();
        _onboard.Save(serial, rec, flush: true);

        // Stable assignment: a color keeps its layer; leftovers take free owned layers; extras are created or deleted.
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

        var reserved = new HashSet<ushort>(view.RegistryRefs);
        foreach (var r in view.ProfileSlots) if (r != 0) reserved.Add(r);
        foreach (var r in view.ListRefs) reserved.Add(r);
        foreach (var o in _onboard.Get(serial)?.Owned ?? new List<GalleonOwnedLayer>()) if (o.Id is > 0 and <= ushort.MaxValue) reserved.Add((ushort)o.Id);

        var existing = owned.ToDictionary(o => o.Id, o => o.Body);
        var wrote = false;
        for (var d = 0; d < bodies.Count; d++)
        {
            if (assigned[d] is { } id)
            {
                if (existing[id].AsSpan().SequenceEqual(bodies[d])) continue;
                TxWrite(slot, LayerHandle, id, bodies[d], $"layer {id:X4}");
                SetOwnedBody(serial, rec, id, bodies[d]);
                wrote = true;
                continue;
            }
            var fresh = AllocateId(slot, reserved);
            SetOwnedBody(serial, rec, fresh, bodies[d], flush: true);
            var created = TxEx(slot, GalleonSdProtocol.CreateResource(fresh), $"create {fresh:X4}");
            if (!created.IsOk) throw new TxAbort($"create {fresh:X4} not accepted (parsed={created.Parsed} status={created.Status:X2})");
            TxWrite(slot, LayerHandle, fresh, bodies[d], $"layer {fresh:X4}");
            assigned[d] = fresh;
            wrote = true;
        }
        foreach (var id in toDelete)
        {
            DeleteOwned(slot, id);
            rec.Owned.RemoveAll(o => o.Id == id);
            _onboard.Save(serial, rec);
            wrote = true;
        }

        var ours = new HashSet<ushort>(owned.Select(o => o.Id));
        var finalIds = assigned.Select(a => a!.Value).ToList();
        var newList = finalIds.Concat(view.ListRefs.Where(r => !ours.Contains(r))).Distinct().ToList();
        if (newList.Count > GalleonSdOnboardCodec.MaxListLayers) throw new TxAbort("layer list would exceed the supported length");
        var listBytes = GalleonSdOnboardCodec.BuildLayerList(newList);
        if (!wrote && listBytes.AsSpan().SequenceEqual(view.ListBytes))
        {
            ServiceLog.Info("[galleon-sd-onboard] board already holds these layers; nothing to write");
            RestoreHardwareLocked(slot, "no-op");
            return true;
        }

        WriteTail(slot, view, listBytes);
        rec = _onboard.Get(serial) ?? rec;
        rec.Owned = finalIds.Select((id, i) => new GalleonOwnedLayer { Id = id, BodyB64 = B64(bodies[i]) }).ToList();
        rec.WrittenListB64 = B64(listBytes);
        _onboard.Save(serial, rec, flush: true);
        return true;
    }

    private void SetOwnedBody(string serial, GalleonOnboardRecord rec, ushort id, byte[] body, bool flush = false)
    {
        rec.Owned.RemoveAll(o => o.Id == id);
        rec.Owned.Add(new GalleonOwnedLayer { Id = id, BodyB64 = B64(body) });
        _onboard.Save(serial, rec, flush);
    }

    private void DeleteOwned(Slot slot, ushort id)
    {
        var reply = TxEx(slot, GalleonSdProtocol.DeleteResource(id), $"delete {id:X4}");
        if (!reply.IsOk) throw new TxAbort($"delete {id:X4} not accepted (parsed={reply.Parsed} status={reply.Status:X2})");
    }

    // A free id: not referenced anywhere and not answering an open. A refused or silent probe-open leaves nothing open, so nothing is closed.
    private ushort AllocateId(Slot slot, HashSet<ushort> reserved)
    {
        var start = reserved.Count == 0 ? 1 : reserved.Max() + 1;
        for (var n = 0; n < MaxAllocProbes; n++)
        {
            var id = start + n;
            if (id >= ushort.MaxValue) break;
            if (reserved.Contains((ushort)id)) continue;
            var outcome = TxOpen(slot, LayerHandle, (ushort)id, $"probe {id:X4}");
            reserved.Add((ushort)id);
            if (outcome == OpenOutcome.Ok)
            {
                TxClose(slot, LayerHandle, $"probe {id:X4}");
                continue;
            }
            return (ushort)id;
        }
        throw new TxAbort("no free resource id found");
    }

    // Layer list, software mode again, profile with a new timestamp, hardware mode, registry unchanged, hardware mode.
    private void WriteTail(Slot slot, BoardView view, byte[] listBytes)
    {
        TxWrite(slot, ListHandle, view.ListRes, listBytes, "list");

        EnterSoftware(slot);
        var profile = TxRead(slot, view.ProfileRes, "profile")!;
        if (!GalleonSdOnboardCodec.TryParseProfile(profile, out var oldStamp, out var slots)) throw new TxAbort("profile layout not recognized on re-read");
        if (slots[GalleonSdOnboardProbe.LayerListSlot] != view.ListRes) throw new TxAbort("profile changed under us; layer list slot moved");
        var stamp = (uint)Math.Max(UtcNow().ToUnixTimeSeconds(), (long)oldStamp + 1);
        TxWrite(slot, ProfileHandle, view.ProfileRes, GalleonSdOnboardCodec.WithTimestamp(profile, stamp), "profile");

        var hw = TxEx(slot, GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware), "mode->hardware");
        if (hw.Parsed && hw.Status != 0) throw new TxAbort($"hardware mode refused (status {hw.Status:X2})");
        TxWrite(slot, LayerHandle, GalleonSdOnboardProbe.RegistryResource, view.Registry, "registry");
        RestoreHardwareLocked(slot, "done");
    }

    private bool UndoTxBody(Slot slot)
    {
        var serial = slot.Info.Serial;
        var rec = _onboard.Get(serial);
        if (rec is null || rec.Owned.Count == 0) return true;

        EnterSoftware(slot);
        var view = ReadBoard(slot);
        var verified = VerifyOwned(slot, rec, new Dictionary<ushort, byte[]>());
        var ours = new HashSet<ushort>(verified.Select(v => v.Id));
        var restored = view.ListRefs.Where(r => !ours.Contains(r)).ToList();
        var backupList = FromB64(rec.ListB64);
        ServiceLog.Info($"[galleon-sd-onboard] undo list={FormatRefs(restored.ToArray())} equalsBackup={backupList is not null && GalleonSdOnboardCodec.BuildLayerList(restored).AsSpan().SequenceEqual(backupList)}");

        foreach (var id in verified.Select(v => v.Id))
        {
            DeleteOwned(slot, id);
        }
        var listBytes = GalleonSdOnboardCodec.BuildLayerList(restored);
        if (ours.Count > 0 || !listBytes.AsSpan().SequenceEqual(view.ListBytes))
        {
            WriteTail(slot, view, listBytes);
        }
        else
        {
            RestoreHardwareLocked(slot, "no-op");
        }

        rec.Owned = new List<GalleonOwnedLayer>();
        rec.WrittenListB64 = "";
        _onboard.Save(serial, rec, flush: true);
        return true;
    }
}
