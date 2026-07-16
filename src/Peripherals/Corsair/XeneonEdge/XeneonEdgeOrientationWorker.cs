using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Corsair.XeneonEdge;

/// <summary>
/// Reads the Xeneon Edge's orientation sensor over its vendor HID interface
/// and applies the matching Windows display rotation, gated on the panel
/// record's AutoOrient preference (null/true = on). Opens its own read
/// handle, independent of any lighting path - the Xeneon Edge has no
/// first-party RGB writer in this codebase (OpenRGB covers its lighting).
/// Modeled on <see cref="Hyte.Keeb.KeebInputWorker"/> (overlapped read loop)
/// and <see cref="Hyte.Keeb.KeebConnectionWorker"/> (USB presence gate).
///
/// Also the single owner of the settings-write/settings-read request/reply
/// exchange (msgid 0x0e/0x0f) exposed to routes via <see cref="ReadSettingsAsync"/>
/// / <see cref="SetControlAsync"/> / <see cref="RestoreDefaultsAsync"/>. A second
/// concurrent HID handle to the same device is allowed by Windows (each open
/// handle gets its own copy of every interrupt-IN report), but a second
/// concurrent *reader* on THIS <see cref="IHidDevice"/> instance is not: Read()
/// reuses one internal buffer/event pair, so two threads calling it on the same
/// instance corrupt each other's transfer. The only thread that ever calls
/// <c>_reader.Read</c> is this worker's own loop thread, so a settings request
/// from an HTTP thread hands its command to that loop instead of reading for
/// itself: it writes the command (Write uses a separate buffer/event from Read,
/// so it is safe alongside an in-flight Read), records what msgid it is waiting
/// for, and awaits a <see cref="TaskCompletionSource{T}"/> that <see cref="Tick"/>
/// completes the next time it reads a report carrying that msgid. Concurrent
/// settings requests are serialized by <see cref="_settingsSemaphore"/> so only
/// one command is outstanding at a time; a generation counter on the pending
/// slot (see <see cref="RequestAsync"/>) keeps a straggling device reply for a
/// timed-out request from being handed to whatever request arms next.
///
/// Orientation applies (the helper RPC in <see cref="ApplyOnce"/>) never run on
/// this loop thread: they are dispatched through <see cref="EnqueueApply"/>, a
/// single-flight queue that coalesces to the latest code, so the loop keeps
/// demuxing settings replies while an apply (up to several seconds, see
/// <see cref="ApplyOnce"/>) is in flight.
/// </summary>
public sealed class XeneonEdgeOrientationWorker : BackgroundService
{
    private const int ReadTimeoutMs = 200;
    private const int RetryDelayMs = 1000;
    // The set ack arrives in ~12ms on the bench but the read loop only checks
    // every ReadTimeoutMs; the settings-block reply is itself ~1s slow.
    private const int SetAckTimeoutMs = 1000;
    private const int SettingsReadTimeoutMs = 2500;
    // One rewrite pass is enough in practice; the second is a backstop.
    private const int RestoreVerifyAttempts = 2;
    // A timed-out request's device reply can still be in flight (a marginal
    // link can delay an ack well past SetAckTimeoutMs). Hold that request's
    // pending slot open for this long so a straggler lands there - a safe
    // no-op against an already-cancelled tcs - instead of on whatever
    // request arms next. Brightness/Backlight/Contrast share group 0x02 and
    // Red/Green/Blue share 0x03, and the ack does not echo item, so nothing
    // downstream can tell a stale same-group ack from a real one.
    private const int StaleReplyDrainMs = 1000;
    // The helper's read loop starts just after Connected fires, so the first
    // replay attempt usually races it; a failed attempt costs up to the
    // helper RPC's own timeout (OrientationDomain's displayOrientation.set,
    // 4000ms) before the gap, so keep the attempt count small.
    private const int HelperReplayAttempts = 2;
    private const int HelperReplayGapMs = 400;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly PanelDeviceRegistry _registry;
    private readonly IDisplayOrientationProvider _orientation;
    private readonly MultiplexHub _hub;
    private readonly object _applyGate = new();
    private readonly object _readerGate = new();
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _settingsSemaphore = new(1, 1);
    private IHidDevice? _reader;

    // Settings request/reply demux (msgid 0x0e/0x0f). See RequestAsync.
    private byte _pendingMsgId;
    private int _pendingGeneration;
    private TaskCompletionSource<byte[]>? _pendingReply;
    private TaskCompletionSource<bool>? _pendingStaleSignal;

    // Orientation apply queue: single-flight, latest-code-wins. See EnqueueApply.
    private bool _applyRunning;
    private byte? _pendingApplyCode;
    private TaskCompletionSource? _applyIdle;
    /// <summary>Last code whose apply failed, replayed once the helper arrives.</summary>
    private byte? _unapplied;
#if WINDOWS
    private readonly Nexus.Service.Helper.HelperRegistry? _helpers;
#endif

    public XeneonEdgeOrientationWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        PanelDeviceRegistry registry,
        IDisplayOrientationProvider orientation,
        MultiplexHub hub
#if WINDOWS
        , Nexus.Service.Helper.HelperRegistry? helpers = null
#endif
        )
    {
        _hid = hid;
        _presence = presence;
        _registry = registry;
        _orientation = orientation;
        _hub = hub;
        // The panel reports orientation once, on change. The service opens the
        // HID device seconds before the user-session helper connects, and the
        // matching panel record can appear even later (auto-promotion runs off
        // a separately debounced topology event) - so the first report's apply
        // can fail before either exists, and no further report arrives until
        // someone physically turns the panel. Replay the unapplied code on
        // whichever of those two signals arrives.
        _registry.DisplayRecordReady += OnDisplayRecordReady;
#if WINDOWS
        _helpers = helpers;
        if (_helpers is not null) _helpers.Connected += OnHelperConnected;
#endif
    }

#if WINDOWS
    private void OnHelperConnected(Nexus.Service.Helper.HelperConnection connection) => TriggerReplay();
#endif

    private void OnDisplayRecordReady(PanelDeviceRecord record) => TriggerReplay();

    /// <summary>
    /// Kicks off a replay attempt when there is something to replay. Cheap to
    /// call from either trigger: this read never blocks (the apply queue's
    /// helper RPC never runs under <see cref="_applyGate"/>), unlike a direct
    /// call into the apply itself would.
    /// </summary>
    private void TriggerReplay()
    {
        lock (_applyGate) { if (_unapplied is null) return; }
        _ = Task.Run(ReplayWhenHelperServesAsync);
    }

    /// <summary>
    /// Replays the last-unapplied orientation once a trigger (helper connect
    /// or a panel record appearing) fires. Re-reads <see cref="_unapplied"/>
    /// at the top of every attempt instead of a captured code: if a real
    /// sensor report lands and applies successfully during the retry gap, it
    /// must win, not get overwritten by a stale replay of what it just
    /// superseded.
    /// </summary>
    private async Task ReplayWhenHelperServesAsync()
    {
        for (var attempt = 0; attempt < HelperReplayAttempts; attempt++)
        {
            byte? code;
            lock (_applyGate) { code = _unapplied; }
            if (code is null) return;
            await EnqueueApply(code.Value).ConfigureAwait(false);
            lock (_applyGate) { if (_unapplied is null) return; }
            try { await Task.Delay(HelperReplayGapMs).ConfigureAwait(false); }
            catch { return; }
        }
        ServiceLog.Warn($"[xeneon-orient] helper replay gave up after {HelperReplayAttempts} attempts");
    }

    public override void Dispose()
    {
        _registry.DisplayRecordReady -= OnDisplayRecordReady;
#if WINDOWS
        if (_helpers is not null) _helpers.Connected -= OnHelperConnected;
#endif
        _settingsSemaphore.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Loop(stoppingToken), stoppingToken);

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool active;
            try
            {
                active = Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[xeneon-orient] tick exception: {ex.GetType().Name}: {ex.Message}");
                CloseReader();
                active = false;
            }
            if (!active)
            {
                try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseReader();
    }

    /// <summary>
    /// One presence-check + read cycle. Public so tests can step it
    /// deterministically. Returns false when the caller should back off
    /// (not present / open failed / device gone) instead of ticking again
    /// immediately - a busy retry otherwise pegs a core.
    /// </summary>
    public bool Tick()
    {
        if (!_presence.UsbPresent(XeneonEdgeProtocol.VendorId, XeneonEdgeProtocol.ProductId))
        {
            CloseReader();
            return false;
        }
        if (_reader is null && !OpenAndArm())
        {
            return false;
        }

        var buf = new byte[XeneonEdgeProtocol.ReportLength];
        var n = _reader!.Read(buf, ReadTimeoutMs);
        if (n == 0) return true; // idle: the read blocked up to ReadTimeoutMs, nothing arrived
        if (n < 0)
        {
            // Device gone: tear down and back off; OpenAndArm re-acquires
            // (and re-arms) when the panel returns.
            CloseReader();
            return false;
        }

        // A report matching an outstanding settings request is delivered to
        // that waiter instead of the orientation parser below - the two
        // msgid ranges (0x11 vs 0x0e/0x0f) never overlap, so an in-flight
        // settings request never blocks orientation reports from applying.
        if (TryDeliverPendingReply(buf, n))
        {
            return true;
        }

        if (XeneonEdgeProtocol.TryParseOrientationReport(buf.AsSpan(0, n), out var code))
        {
            HandleOrientation(code);
        }
        return true;
    }

    private bool TryDeliverPendingReply(byte[] buf, int n)
    {
        TaskCompletionSource<byte[]>? pending;
        TaskCompletionSource<bool>? staleSignal;
        byte expectedMsgId;
        int generation;
        lock (_pendingGate)
        {
            pending = _pendingReply;
            staleSignal = _pendingStaleSignal;
            expectedMsgId = _pendingMsgId;
            generation = _pendingGeneration;
        }
        if (pending is null || n < 2 || buf[1] != expectedMsgId) return false;

        lock (_pendingGate)
        {
            if (_pendingGeneration == generation)
            {
                _pendingReply = null;
                _pendingStaleSignal = null;
            }
        }
        if (!pending.TrySetResult(buf.AsSpan(0, n).ToArray()))
        {
            // The waiter already gave up (timed out): this is a straggling
            // device reply for an abandoned request, not a match for
            // whatever arms next. Signal the drain so that request's grace
            // wait ends now instead of riding out its full window.
            staleSignal?.TrySetResult(true);
        }
        return true;
    }

    private bool OpenAndArm()
    {
        HidDeviceInfo? info = null;
        foreach (var i in _hid.Find(XeneonEdgeProtocol.VendorId, XeneonEdgeProtocol.ProductId))
        {
            if (i.UsagePage == XeneonEdgeProtocol.UsagePage && i.Usage == XeneonEdgeProtocol.Usage)
            {
                info = i;
                break;
            }
        }
        if (info is null) return false;

        var device = _hid.Open(info.Path, forInput: true);
        if (device is null) return false;

        // The device reports nothing until armed: one query primes the push
        // stream (immediate reply with the current orientation, then an
        // unsolicited report on every change).
        if (!device.Write(XeneonEdgeProtocol.BuildOrientationQuery()))
        {
            device.Dispose();
            return false;
        }

        lock (_readerGate) { _reader = device; }
        ServiceLog.Info($"[xeneon-orient] reader opened + armed on {info.Path}");
        return true;
    }

    private void HandleOrientation(byte code) => _ = EnqueueApply(code);

    /// <summary>
    /// Queues an orientation apply, coalescing with whatever is already
    /// waiting: only the latest code survives when one lands while another
    /// is in flight (the panel reports absolute state, not deltas, so the
    /// newest report always wins - and each apply can cost seconds, so
    /// applying every intermediate report would only delay converging on
    /// the true current state). Applies never run concurrently with each
    /// other; a single background loop drains the queue one code at a time.
    /// Returns a task that completes once the queue has drained back to
    /// idle (which may cover a code coalesced in after this call), so a
    /// caller that needs to observe the outcome (the replay loop) can await
    /// a real signal instead of polling.
    /// </summary>
    private Task EnqueueApply(byte code)
    {
        lock (_applyGate)
        {
            _pendingApplyCode = code;
            _applyIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var idle = _applyIdle;
            if (!_applyRunning)
            {
                _applyRunning = true;
                _ = Task.Run(RunApplyLoop);
            }
            return idle.Task;
        }
    }

    private void RunApplyLoop()
    {
        while (true)
        {
            byte code;
            lock (_applyGate)
            {
                if (_pendingApplyCode is not byte next)
                {
                    _applyRunning = false;
                    var idle = _applyIdle;
                    _applyIdle = null;
                    idle?.TrySetResult();
                    return;
                }
                _pendingApplyCode = null;
                code = next;
            }
            try
            {
                ApplyOnce(code);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[xeneon-orient] apply exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves, records, and (when a matching auto-orienting panel exists)
    /// applies one orientation code. Always runs off <see cref="RunApplyLoop"/>
    /// on a background task, never on the HID reader thread:
    /// <see cref="IDisplayOrientationProvider.SetDisplayOrientation"/> is a
    /// blocking helper RPC (up to 4s), and the reader must keep demuxing
    /// settings replies while it is in flight.
    /// </summary>
    private void ApplyOnce(byte code)
    {
        var mapped = XeneonEdgeProtocol.ResolveOrientation(code);
        if (mapped is null)
        {
            ServiceLog.Warn($"[xeneon-orient] unknown sensor code={code}");
            return;
        }

        var found = FindActiveRecord();
        if (found is null)
        {
            // The panel record can appear after auto-promotion; keep the code
            // so the next apply attempt is against the real orientation.
            lock (_applyGate) { _unapplied = code; }
            ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} (no attached xeneon edge panel)");
            return;
        }
        var (record, displayId) = found.Value;
        if (record.AutoOrient == false)
        {
            lock (_applyGate) { _unapplied = null; }
            ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} ignored (AutoOrient off) displayId={displayId}");
            return;
        }

        // The helper paints this over the panel across the mode set, so the
        // strip ChangeDisplaySettingsEx exposes shows the panel's own colour
        // instead of the desktop.
        var coverColorHex = PanelDeviceRegistry.ResolveCoverBackgroundHex(record);
        var (ok, error) = _orientation.SetDisplayOrientation(displayId, mapped, coverColorHex);
        if (ok)
        {
            lock (_applyGate) { _unapplied = null; }
            // Settings permanence: same model as the manual
            // /displays/{id}/rotation route (DisplayRoutes.cs).
            _registry.UpdateDisplayOrientation(displayId, mapped);
            PanelTopics.BroadcastPanelDevice(_hub, record.Id);
        }
        else
        {
            lock (_applyGate) { _unapplied = code; }
        }
        ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} ok={ok} detail='{error}' displayId={displayId}");
    }

    private (PanelDeviceRecord Record, string DisplayId)? FindActiveRecord()
    {
        foreach (var record in _registry.List())
        {
            if (string.IsNullOrEmpty(record.DisplayId)) continue;
            if (record.Enabled == false) continue;
            if (!string.Equals(record.Capabilities?.Family, KnownPanelDisplays.XeneonEdgeFamily, StringComparison.Ordinal)) continue;
            return (record, record.DisplayId);
        }
        return null;
    }

    private void CloseReader()
    {
        lock (_readerGate)
        {
            try { _reader?.Dispose(); } catch { }
            _reader = null;
        }
        TaskCompletionSource<byte[]>? pending;
        TaskCompletionSource<bool>? staleSignal;
        lock (_pendingGate)
        {
            pending = _pendingReply;
            staleSignal = _pendingStaleSignal;
            _pendingReply = null;
            _pendingStaleSignal = null;
        }
        // Fail fast instead of leaving a settings caller waiting out its
        // whole timeout for a reply that can no longer arrive; also releases
        // a request's stale-reply drain immediately, since a torn-down
        // connection cannot deliver the straggler it was waiting for.
        pending?.TrySetCanceled();
        staleSignal?.TrySetResult(false);
    }

    /// <summary>
    /// Reads the whole settings block (msgid 0x0e) live from the panel.
    /// Slow (~1s on the bench); null when the device is absent or the read
    /// times out.
    /// </summary>
    public async Task<XeneonEdgeSettingsBlock?> ReadSettingsAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(XeneonEdgeProtocol.BuildSettingsQuery(), XeneonEdgeProtocol.MsgIdSettingsBlock, SettingsReadTimeoutMs, ct)
            .ConfigureAwait(false);
        if (reply is null) return null;
        return XeneonEdgeProtocol.TryParseSettingsBlock(reply, out var block) ? block : null;
    }

    /// <summary>
    /// Writes one control (msgid 0x0f) and returns the value the panel
    /// echoed back in its ack, clamped to the control's documented range.
    /// Null when the device is absent, the write failed, or the ack timed
    /// out or reported failure.
    /// </summary>
    public async Task<int?> SetControlAsync(XeneonEdgeControl control, int value, CancellationToken ct)
    {
        var coords = XeneonEdgeControls.Coords[control];
        var clamped = Math.Clamp(value, coords.Min, coords.Max);
        var command = XeneonEdgeProtocol.BuildSetCommand(coords.Group, coords.Item, (byte)clamped);
        var reply = await RequestAsync(command, XeneonEdgeProtocol.MsgIdSet, SetAckTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return null;
        if (!XeneonEdgeProtocol.TryParseSetAck(reply, out var ackGroup, out var ackValue)) return null;
        if (ackGroup != coords.Group) return null;
        return ackValue;
    }

    /// <summary>
    /// Restores every control to its factory value. The panel's own 0xff
    /// command only covers RGB, so each control is written individually.
    ///
    /// The write is then verified against a settings read and any control that
    /// did not land is rewritten. A 0x0f ack means the panel received the
    /// command, not that it committed it: a burst of writes intermittently
    /// leaves some controls at their old value despite every ack arriving
    /// (bench-observed on T1, both a stale-value and a fully-correct outcome
    /// from the identical burst). Verifying is the only reliable signal, so it
    /// is done rather than pacing the writes against a guessed delay.
    /// </summary>
    public async Task<bool> RestoreDefaultsAsync(CancellationToken ct)
    {
        foreach (var (control, value) in XeneonEdgeDefaults.All)
        {
            if (await SetControlAsync(control, value, ct).ConfigureAwait(false) is null) return false;
        }

        // RestoreVerifyAttempts rewrite passes, each followed by a verifying
        // read - the loop always ends on a read, so the LAST rewrite gets
        // checked too instead of being assumed to have failed.
        for (var attempt = 0; ; attempt++)
        {
            var block = await ReadSettingsAsync(ct).ConfigureAwait(false);
            if (block is null) return false;

            var stale = XeneonEdgeDefaults.All
                .Where(d => XeneonEdgeControls.Read(block.Value, d.Control) != d.Value)
                .ToList();
            if (stale.Count == 0) return true;
            if (attempt >= RestoreVerifyAttempts) return false;

            ServiceLog.Info($"[xeneon-orient] restore: {stale.Count} control(s) did not take, rewriting ({string.Join(",", stale.Select(s => s.Control))})");
            foreach (var (control, value) in stale)
            {
                if (await SetControlAsync(control, value, ct).ConfigureAwait(false) is null) return false;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="command"/> and awaits the loop thread's next
    /// matching-msgid read (see <see cref="TryDeliverPendingReply"/>).
    /// Serialized by <see cref="_settingsSemaphore"/> so only one command is
    /// outstanding at a time. On timeout the pending slot is not released
    /// immediately: <see cref="DrainStaleReplyThenReleaseAsync"/> holds it for
    /// a bounded grace window so a late device reply for THIS request cannot
    /// be handed to whatever request arms next.
    /// </summary>
    private async Task<byte[]?> RequestAsync(byte[] command, byte expectedMsgId, int timeoutMs, CancellationToken ct)
    {
        await _settingsSemaphore.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;
        lock (_pendingGate)
        {
            generation = ++_pendingGeneration;
            _pendingMsgId = expectedMsgId;
            _pendingReply = tcs;
            _pendingStaleSignal = staleSignal;
        }

        bool wrote;
        lock (_readerGate)
        {
            wrote = _reader is not null && _reader.Write(command);
        }
        if (!wrote)
        {
            ClearPendingSlot(generation);
            ReleaseSettingsSemaphore();
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var registration = linked.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            var result = await tcs.Task.ConfigureAwait(false);
            ClearPendingSlot(generation);
            ReleaseSettingsSemaphore();
            return result;
        }
        catch (OperationCanceledException)
        {
            // The device's reply for this request may still be in flight.
            // Hold this generation's slot open for a bounded drain instead of
            // releasing the semaphore now - see StaleReplyDrainMs.
            _ = DrainStaleReplyThenReleaseAsync(generation, staleSignal);
            return null;
        }
    }

    private void ClearPendingSlot(int generation)
    {
        lock (_pendingGate)
        {
            if (_pendingGeneration != generation) return;
            _pendingReply = null;
            _pendingStaleSignal = null;
        }
    }

    private void ReleaseSettingsSemaphore()
    {
        // A request's drain can still be running when Dispose runs (shutdown
        // mid-request); releasing a disposed semaphore is expected there,
        // not a bug to surface.
        try { _settingsSemaphore.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task DrainStaleReplyThenReleaseAsync(int generation, TaskCompletionSource<bool> staleSignal)
    {
        using var cts = new CancellationTokenSource(StaleReplyDrainMs);
        try { await staleSignal.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        ClearPendingSlot(generation);
        ReleaseSettingsSemaphore();
    }
}
