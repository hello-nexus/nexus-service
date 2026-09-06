using System;
using System.Collections.Generic;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Singleton coordinator for the SLV3 wireless link: owns the TX + RX dongle
/// transports, the master identity, the bind/unbind state machine, the
/// device-list poll, RGB pushes, and the PWM sync. The cadence and every
/// frame mirror L-Connect's MasterDevice loop as captured on the Y70 with
/// USBPcap (2026-09-04): <see cref="PollTick"/> is its 500 ms RefreshList +
/// SyncControlInfo pass, <see cref="DriveTick"/> adds the once-a-second
/// SyncPwm, QuerryMasterMac, SyncMasterClock and SaveConfig work.
/// All hardware I/O and shared state are guarded by one lock: the connection
/// worker's ticks and route-driven bind/unbind/identify calls both touch it.
/// </summary>
public sealed class Slv3Hub : IDisposable
{
    // Shared read-only default (all null = motherboard-sync) for a chain that
    // has never had a duty set. Never mutated - safe to share across keys.
    private static readonly int?[] DefaultDutyTargets = new int?[Slv3Protocol.PortsPerRecord];

    private readonly ISlv3Discovery _discovery;
    private readonly Func<Slv3PortInfo, ISlv3Transport> _transportFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slv3PendingOp> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slv3PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    // Last cmd_seq issued per chain MAC hex, so back-to-back commands stay
    // monotonic even before the chain's record echoes the previous one
    // (L-Connect's per-device targe_cmd_seq).
    private readonly Dictionary<string, byte> _lastIssuedSeq = new(StringComparer.Ordinal);
    private List<Slv3DeviceRecord> _lastFanRecords = new();

    // Last-known chains keyed by MAC hex. Under RGB traffic the RX misses
    // beacons, so a poll's record set is a SAMPLE of the live chains, not the
    // truth; records merge in and only expire after ChainExpiryMs unseen.
    // Wholesale replacement per poll is what made chains flap in and out of
    // cooling/lighting (Y70 log: "device list: 2 -> 1 -> 2" continuously).
    private readonly Dictionary<string, Slv3KnownChain> _knownChains = new(StringComparer.Ordinal);

    // Per-chain PWM port targets keyed by fan MAC hex; a missing key or a
    // null element means that port follows the motherboard PWM header. Read
    // by the PWM sync, written by SetPortDuty; both hold _lock.
    private readonly Dictionary<string, int?[]> _dutyTargets = new(StringComparer.Ordinal);

    private ISlv3Transport? _tx;
    private ISlv3Transport? _rx;
    private byte[] _masterMac = new byte[Slv3Protocol.MacLength];
    private byte _channel = Slv3Protocol.DefaultChannel;
    private bool _disposed;
    private bool _videoModeActive;
    private int _videoModePreppedCount;
    // Round-robin cursor into ChannelScanOrder so one connect attempt probes a
    // bounded slice; a full scan completes across successive attempts instead
    // of holding _lock for ~19 s of read timeouts in one call.
    private int _channelScanCursor;

    // A chain unseen this long is treated as gone (powered off / out of range)
    // and dropped; until then it stays listed so downstream devices are stable.
    private const long ChainExpiryMs = 30_000;
    // Unseen this long = telemetry is last-known, surfaced as Stale.
    private const long ChainStaleMs = 3_500;

    // GetDev failure escalation (lian-li-linux controller.rs): 5 consecutive
    // USB-level failures -> UsbResetAnother to the RX MCU (a handle reopen does
    // not reset a wedged radio), at most 3 resets per connection, then give the
    // worker its disconnect/reconnect path.
    private const int RxFailStreakForReset = 5;
    private const int MaxRxResetsPerConnection = 3;
    // Post-reset settle per the reference's 500 ms sleep after USB_ResetAnother.
    private const int RxResetSettleMs = 500;
    private int _rxFailStreak;
    private int _rxResetCount;

    // SaveCfg after a confirmed bind: L-Connect broadcasts one on every loop
    // pass for a few seconds after Bind() (lastBindTime); here one per poll
    // tick for the same span.
    private const int SaveCfgBurstSends = 10;
    private int _saveCfgBurstRemaining;

    // Throttled SaveCfg after any binding or effect change (RFController.SaveConfig):
    // fires SaveCfgDelayMs after the first change, deferred by the same delay
    // while the last change is younger than SaveCfgQuietMs. A live RGB stream
    // never settles, so it saves once when the stream goes quiet.
    private const long SaveCfgDelayMs = 10_000;
    private const long SaveCfgQuietMs = 5_000;
    private long _saveCfgDueMs;
    private long _lastConfigChangeMs;

    // Poll ticks a pending bind/unbind may re-send before it is dropped as
    // non-converging (fan unreachable).
    internal const int PendingOpTickBudget = 30;

    // Sends a sequenced control frame (RF_Select / RF_RebootLcd) gets before it
    // is dropped without an echo (L-Connect selected / reboot_lcd counters).
    internal const int SequencedCommandBudget = 10;

    // Header packet repeats for an RGB upload, the only profile L-Connect uses
    // (no CRC on this link; a lost header sticks until the effect_index echo
    // shows it).
    private const int RgbHeaderRepeats = 4;
    private const int RgbHeaderGapMs = 20;

    // Device-list records span more than one page once enough chains are bound
    // (up to MaxSlot, plus non-fan devices), so the poll requests
    // ceil(count/RecordsPerPage) pages. Clamp so a corrupt count can't trigger
    // a runaway read.
    private const int MaxDeviceListPages = 3;
    private int _lastRecordCount;

    public Slv3Hub(ISlv3Discovery discovery, Func<Slv3PortInfo, ISlv3Transport> transportFactory, Func<long>? nowMs = null)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    // Monotonic ms clock; injectable so tests drive chain expiry deterministically.
    private readonly Func<long> _nowMs;

    public Slv3State State { get; } = new();

    public bool IsConnected => _tx is { IsOpen: true } && _rx is { IsOpen: true };

    /// <summary>Opens the TX + RX dongles and learns our master MAC. Both must open for the link to be usable.</summary>
    public bool EnsureConnected()
    {
        if (_disposed)
        {
            return false;
        }
        if (IsConnected)
        {
            return true;
        }
        lock (_lock)
        {
            if (IsConnected)
            {
                return true;
            }

            Slv3PortInfo? txPort = null;
            Slv3PortInfo? rxPort = null;
            foreach (var port in _discovery.Discover())
            {
                if (port.Role == Slv3DongleRole.Tx)
                {
                    txPort ??= port;
                }
                else if (port.Role == Slv3DongleRole.Rx)
                {
                    rxPort ??= port;
                }
            }
            if (txPort is null || rxPort is null)
            {
                return false;
            }

            try
            {
                _tx = _transportFactory(txPort);
                _rx = _transportFactory(rxPort);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-wireless] open failed: {ex.GetType().Name}: {ex.Message}");
                DisconnectLocked();
                return false;
            }

            if (!MasterInitLocked())
            {
                ServiceLog.Warn("[lianli-wireless] GetMac failed on connect");
                DisconnectLocked();
                return false;
            }

            State.IsConnected = true;
            ServiceLog.Info($"[lianli-wireless] connected (tx={txPort.PortName}, rx={rxPort.PortName}, master={State.MasterMac})");
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectLocked();
        }
    }

    private void DisconnectLocked()
    {
        try { _tx?.Dispose(); } catch { /* best effort */ }
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _tx = null;
        _rx = null;
        State.IsConnected = false;
        State.Fans = Array.Empty<Slv3FanInfo>();
        State.MotherboardPwmPercent = null;
        _lastFanRecords = new List<Slv3DeviceRecord>();
        _knownChains.Clear();
        _pending.Clear();
        _pendingCommands.Clear();
        _lastIssuedSeq.Clear();
        _rxFailStreak = 0;
        _rxResetCount = 0;
        _saveCfgBurstRemaining = 0;
        _saveCfgDueMs = 0;
        _videoModeActive = false;
        _videoModePreppedCount = 0;
    }

    private bool MasterInitLocked()
    {
        if (_tx is null)
        {
            return false;
        }
        // Probe the configured channel first, then a bounded slice of the scan
        // order: a dongle left on another channel by L-Connect only answers
        // GetMac there, and a zero MAC in the reply means "no master on this
        // channel", not success. Each dead probe costs a full 500 ms read
        // timeout under _lock, so one attempt probes at most ScanProbesPerAttempt
        // channels; the cursor resumes there on the worker's next 5 s retry,
        // covering all 39 channels across a few attempts without starving the
        // routes and writer that share the lock.
        if (TryGetMacOnChannelLocked(_channel))
        {
            return true;
        }
        const int ScanProbesPerAttempt = 8;
        var order = Slv3Protocol.ChannelScanOrder();
        for (var probes = 0; probes < ScanProbesPerAttempt && probes < order.Length; _channelScanCursor++)
        {
            var channel = order[_channelScanCursor % order.Length];
            if (channel == _channel)
            {
                continue;
            }
            probes++;
            if (TryGetMacOnChannelLocked(channel))
            {
                ServiceLog.Info($"[lianli-wireless] master found on channel {channel} (scanned from {_channel})");
                _channel = channel;
                State.Channel = channel;
                return true;
            }
        }
        return false;
    }

    private bool TryGetMacOnChannelLocked(byte channel)
    {
        if (_tx is null || !_tx.RfSend(Slv3Protocol.BuildGetMac(channel)))
        {
            return false;
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        if (!Slv3Protocol.TryParseGetMac(reply, out var mac, out _, out var fw)
            || Slv3Protocol.MacIsZero(mac))
        {
            return false;
        }
        _masterMac = mac;
        State.MasterMac = Convert.ToHexString(mac);
        State.TxFirmwareVersion = fw;
        State.Channel = channel;
        return true;
    }

    /// <summary>
    /// The 500 ms pass (L-Connect RefreshList + SyncControlInfo): refresh the
    /// device list, resolve pending bind/unbind/select/reboot against the fresh
    /// report, and re-send whatever is still pending. Returns false when the
    /// link is down or the poll failed past its reset budget.
    /// </summary>
    public bool PollTick()
    {
        lock (_lock)
        {
            return PollLocked();
        }
    }

    /// <summary>
    /// The once-a-second pass: everything <see cref="PollTick"/> does, then
    /// L-Connect's SyncPwm (a bind/PWM frame only for a chain whose reported
    /// duty drifted from its target), QuerryMasterMac, SyncMasterClock, and
    /// the SaveCfg burst/throttle.
    /// </summary>
    public bool DriveTick()
    {
        lock (_lock)
        {
            if (!PollLocked())
            {
                return false;
            }
            SyncPwmLocked();
            RefreshMasterMacLocked();
            SendClockHeartbeatLocked();
            RunSaveCfgScheduleLocked();
            return true;
        }
    }

    private bool PollLocked()
    {
        if (!IsConnected)
        {
            return false;
        }
        if (!RefreshDeviceListLocked())
        {
            return false;
        }
        ResolvePendingLocked();
        SyncControlLocked();
        return true;
    }

    // Re-send every pending bind/unbind frame and sequenced command, and one
    // SaveCfg of a post-bind burst. L-Connect does this on every loop pass
    // until the device list echoes the result.
    private void SyncControlLocked()
    {
        foreach (var op in _pending.Values)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                SendBindFrameLocked(record, op.TargetSlot, op.Unbind);
            }
        }

        List<string>? done = null;
        List<(string Key, Slv3PendingCommand Cmd)>? ticked = null;
        foreach (var (key, cmd) in _pendingCommands)
        {
            var found = TryFindRecordLocked(cmd.Mac, out var record);
            if ((found && record.CmdSeq == cmd.TargetSeq) || cmd.SendsRemaining <= 0)
            {
                (done ??= new List<string>()).Add(key);
                continue;
            }
            if (found)
            {
                SendSequencedCommandLocked(record, cmd.RfCmd, cmd.TargetSeq);
            }
            // A chain that dropped out of the list still spends its budget, so a
            // stale command cannot outlive the chain and fire on its return.
            (ticked ??= new List<(string, Slv3PendingCommand)>()).Add((key, cmd with { SendsRemaining = cmd.SendsRemaining - 1 }));
        }
        if (ticked is not null)
        {
            foreach (var (key, cmd) in ticked)
            {
                _pendingCommands[key] = cmd;
            }
        }
        if (done is not null)
        {
            foreach (var key in done)
            {
                _pendingCommands.Remove(key);
            }
        }

        if (_saveCfgBurstRemaining > 0)
        {
            _saveCfgBurstRemaining--;
            SendSaveCfgLocked();
        }
    }

    // L-Connect SyncPwm: for every chain bound to us that reports fans, send
    // the bind/PWM frame only when a port's reported duty byte is more than
    // PwmDriftThreshold from its target. The chain echoes the tuple it was
    // last given, so a changed target converges after one frame and a settled
    // chain gets nothing. A chain with a pending bind/unbind is left to the
    // control pass. L-Connect skips a chain that enumerates no fans; here such
    // a chain is still driven once the user sets a port duty on it (the cooling
    // provider exposes its ports), and left alone on the mobo-sync default.
    private void SyncPwmLocked()
    {
        foreach (var record in _lastFanRecords)
        {
            if (!IsBoundToUsLocked(record))
            {
                continue;
            }
            var key = Convert.ToHexString(record.Mac);
            if (_pending.ContainsKey(key))
            {
                continue;
            }
            var targets = DutyTargetsLocked(record.Mac);
            if (record.FanCount <= 0 && !HasManualTarget(targets))
            {
                continue;
            }
            var pwm = Slv3Protocol.BuildPwmTuple(targets, record.FanCount, record.Family);
            if (Slv3Protocol.NeedSyncPwm(record.Pwm, pwm))
            {
                SendBindFrameLocked(record, record.RxType, unbind: false);
            }
        }
    }

    private static bool HasManualTarget(int?[] targets)
    {
        foreach (var target in targets)
        {
            if (target is not null)
            {
                return true;
            }
        }
        return false;
    }

    // L-Connect QuerryMasterMac once a second: refreshes the master identity,
    // RF timer and TX firmware version. A missed reply keeps the last known
    // values; the link is not torn down for it.
    private void RefreshMasterMacLocked()
    {
        TryGetMacOnChannelLocked(_channel);
    }

    private void ResolvePendingLocked()
    {
        List<string>? resolved = null;
        List<string>? expired = null;
        List<(string Key, Slv3PendingOp Op)>? ticked = null;
        var bindConfirmed = false;
        foreach (var (key, op) in _pending)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                var done = op.Unbind
                    ? !IsBoundToUsLocked(record)
                    : IsBoundToUsLocked(record);
                if (done)
                {
                    (resolved ??= new List<string>()).Add(key);
                    bindConfirmed |= !op.Unbind;
                    continue;
                }
            }
            if (op.TicksRemaining <= 1)
            {
                (expired ??= new List<string>()).Add(key);
            }
            else
            {
                (ticked ??= new List<(string, Slv3PendingOp)>()).Add((key, op with { TicksRemaining = op.TicksRemaining - 1 }));
            }
        }
        if (ticked is not null)
        {
            foreach (var (key, op) in ticked)
            {
                _pending[key] = op;
            }
        }
        if (resolved is not null)
        {
            foreach (var key in resolved)
            {
                _pending.Remove(key);
            }
            // Persist the confirmed binding change to fan flash so it survives
            // a power cycle; a bind without SaveCfg lives only in firmware RAM.
            if (bindConfirmed)
            {
                _saveCfgBurstRemaining = SaveCfgBurstSends;
            }
            NoteConfigChangedLocked();
        }
        if (expired is not null)
        {
            foreach (var key in expired)
            {
                ServiceLog.Warn($"[lianli-wireless] bind/unbind for {key} did not converge in {PendingOpTickBudget} polls, dropping");
                _pending.Remove(key);
            }
        }
    }

    private void NoteConfigChangedLocked()
    {
        var now = _nowMs();
        _lastConfigChangeMs = now;
        if (_saveCfgDueMs == 0)
        {
            _saveCfgDueMs = now + SaveCfgDelayMs;
        }
    }

    private void RunSaveCfgScheduleLocked()
    {
        if (_saveCfgDueMs == 0)
        {
            return;
        }
        var now = _nowMs();
        if (now < _saveCfgDueMs)
        {
            return;
        }
        if (now - _lastConfigChangeMs < SaveCfgQuietMs)
        {
            _saveCfgDueMs = now + SaveCfgDelayMs;
            return;
        }
        _saveCfgDueMs = 0;
        SendSaveCfgLocked();
    }

    private void SendSaveCfgLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = Slv3Protocol.BuildSaveCfg(_masterMac);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            _tx.RfSend(frame);
        }
    }

    // Pages needed to hold recordCount records at RecordsPerPage each, >=1 and
    // capped at MaxDeviceListPages so a corrupt count can't request a huge read.
    private static byte DeviceListPagesFor(int recordCount) =>
        (byte)Math.Clamp(
            (recordCount + Slv3Protocol.RecordsPerPage - 1) / Slv3Protocol.RecordsPerPage,
            1, MaxDeviceListPages);

    private bool RefreshDeviceListLocked()
    {
        if (_rx is null)
        {
            return false;
        }
        // <=10 records/page - request ceil(count/10) pages so a device list that
        // spans more than one page (up to MaxSlot chains plus non-fan devices)
        // isn't truncated to the first 10. Dropped records vanish from the list,
        // so those chains can't be seen, paired, or confirm a bind. Seed pageCount
        // from the last poll's reported count (the reference auto-tunes the same
        // way); a chain that just appeared is picked up on the next poll.
        var pageCount = DeviceListPagesFor(_lastRecordCount);
        if (!_rx.RfSend(Slv3Protocol.BuildGetDev(pageCount)))
        {
            // A failed USB write means the handle itself is dead (replug,
            // suspend); fail the tick so the worker reopens promptly.
            return false;
        }
        var reply = _rx.RfRead(Slv3Protocol.PageLength * pageCount);
        // A missing/invalid GetDev echo with a healthy handle is a wedged RX
        // MCU (streak -> 0x15 reset); a valid reply listing zero devices is an
        // RF sampling gap and takes the normal merge path.
        if (reply.Length < Slv3Protocol.RecordHeaderLength || reply[0] != Slv3Protocol.UsbSendRf)
        {
            return HandleGetDevFailureLocked();
        }
        _rxFailStreak = 0;

        State.MotherboardPwmPercent = Slv3Protocol.ParseGetDevMoboDuty(reply);

        var count = Slv3Protocol.RecordCount(reply);
        var nowMs = _nowMs();
        for (var i = 0; i < count; i++)
        {
            var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
            if (Slv3Protocol.TryParseRecord(reply, offset, out var record) && record.IsWirelessFan)
            {
                var key = Convert.ToHexString(record.Mac);
                if (!_knownChains.ContainsKey(key))
                {
                    ServiceLog.Info($"[lianli-wireless] chain {key} appeared ({record.FanCount} fan(s), {record.Family})");
                }
                _knownChains[key] = new Slv3KnownChain(record, nowMs);
            }
        }

        List<string>? gone = null;
        foreach (var (key, chain) in _knownChains)
        {
            if (nowMs - chain.LastSeenMs > ChainExpiryMs)
            {
                (gone ??= new List<string>()).Add(key);
            }
        }
        if (gone is not null)
        {
            foreach (var key in gone)
            {
                ServiceLog.Info($"[lianli-wireless] chain {key} dropped ({ChainExpiryMs / 1000}s unseen)");
                _knownChains.Remove(key);
            }
        }

        // Page estimate follows the larger of the reply's count and the tracked
        // set, so a partial report can't shrink the next read below the full list.
        _lastRecordCount = Math.Max(count, _knownChains.Count);

        var records = new List<Slv3DeviceRecord>(_knownChains.Count);
        var fans = new List<Slv3FanInfo>(_knownChains.Count);
        foreach (var key in SortedChainKeysLocked())
        {
            var chain = _knownChains[key];
            records.Add(chain.Record);
            fans.Add(ToFanInfo(chain.Record, stale: nowMs - chain.LastSeenMs > ChainStaleMs));
        }
        _lastFanRecords = records;
        State.Fans = fans.ToArray();
        return true;
    }

    // MAC-ordered keys so the surfaced list is stable across polls regardless
    // of dictionary iteration order.
    private List<string> SortedChainKeysLocked()
    {
        var keys = new List<string>(_knownChains.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private bool HandleGetDevFailureLocked()
    {
        _rxFailStreak++;
        if (_rxFailStreak < RxFailStreakForReset)
        {
            // Transient: keep the last-known list and let the next tick retry.
            return true;
        }
        if (_rxResetCount >= MaxRxResetsPerConnection)
        {
            // Out of resets. Streak stays past the threshold, so every further
            // failure lands here and the worker's consecutive-failure path
            // gets its disconnect/reopen.
            return false;
        }
        _rxFailStreak = 0;
        _rxResetCount++;
        ServiceLog.Warn($"[lianli-wireless] {RxFailStreakForReset} consecutive GetDev failures, resetting RX MCU ({_rxResetCount}/{MaxRxResetsPerConnection})");
        if (_rx is not null && _rx.RfSend(Slv3Protocol.BuildResetAnother()))
        {
            _rx.RfRead(Slv3Protocol.UsbPacketSize);
        }
        // Reference sleeps 500 ms after USB_ResetAnother before the next poll.
        Thread.Sleep(RxResetSettleMs);
        return true;
    }

    // Bound to us = our master MAC and a valid slot. A release clears both in
    // the chain's record; a stale master with slot 0 is unbound.
    private bool IsBoundToUsLocked(Slv3DeviceRecord record) =>
        Slv3Protocol.MacEquals(record.MasterMac, _masterMac)
        && record.RxType >= Slv3Protocol.MinSlot
        && record.RxType <= Slv3Protocol.MaxSlot;

    private Slv3FanInfo ToFanInfo(Slv3DeviceRecord record, bool stale) => new()
    {
        Mac = Convert.ToHexString(record.Mac),
        MasterMac = Slv3Protocol.MacIsZero(record.MasterMac) ? "" : Convert.ToHexString(record.MasterMac),
        BoundToUs = IsBoundToUsLocked(record),
        Channel = record.Channel,
        Slot = record.RxType,
        DevType = record.DevType,
        FanType = record.EffectiveFanType,
        FanCount = record.FanCount,
        Rpm = (int[])record.Rpm.Clone(),
        Pwm = (int[])record.Pwm.Clone(),
        EffectIndex = Convert.ToHexString(record.EffectIndex),
        Stale = stale,
    };

    private bool TryFindRecordLocked(byte[] mac, out Slv3DeviceRecord record)
    {
        foreach (var r in _lastFanRecords)
        {
            if (Slv3Protocol.MacEquals(r.Mac, mac))
            {
                record = r;
                return true;
            }
        }
        record = default;
        return false;
    }

    // L-Connect writes the chain's 1-based position among the chains bound to
    // this master (SyncControlInfo's running counter) at bind-frame byte [16],
    // not its rx_type slot. Equal for a single chain; they diverge once a slot
    // is freed in the middle.
    private byte BindOrdinalLocked(byte[] mac)
    {
        var ordinal = 0;
        foreach (var key in SortedChainKeysLocked())
        {
            var record = _knownChains[key].Record;
            var bound = IsBoundToUsLocked(record)
                || (_pending.TryGetValue(key, out var op) && !op.Unbind);
            if (!bound)
            {
                continue;
            }
            ordinal++;
            if (Slv3Protocol.MacEquals(record.Mac, mac))
            {
                return (byte)ordinal;
            }
        }
        return (byte)Math.Max(1, ordinal + 1);
    }

    // Sent addressed at the fan's CURRENT (channel, rxType) pipe from the last
    // device-list report, so the dongle steers to it regardless of which master
    // it is presently bound to; the payload's target fields carry what to
    // reconfigure to. Caller holds _lock.
    private bool SendBindFrameLocked(Slv3DeviceRecord record, byte targetSlot, bool unbind)
    {
        if (_tx is null)
        {
            return false;
        }
        var pwm = Slv3Protocol.BuildPwmTuple(DutyTargetsLocked(record.Mac), record.FanCount, record.Family);
        var payload = unbind
            ? Slv3Protocol.BuildUnbind(record.Mac, _channel, pwm)
            : Slv3Protocol.BuildBind(record.Mac, _masterMac, targetRx: targetSlot, targetChannel: _channel, slot: BindOrdinalLocked(record.Mac), pwm);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(record.Channel, record.RxType, payload))
        {
            if (!_tx.RfSend(frame))
            {
                return false;
            }
        }
        return true;
    }

    private bool SendSequencedCommandLocked(Slv3DeviceRecord record, byte rfCmd, byte cmdSeq)
    {
        if (_tx is null)
        {
            return false;
        }
        var payload = Slv3Protocol.BuildSequencedCommand(rfCmd, record.Mac, _masterMac, record.RxType, record.Channel, cmdSeq);
        return SendRfPayloadLocked(record.Channel, record.RxType, payload);
    }

    // Caller holds _lock.
    private int?[] DutyTargetsLocked(byte[] mac)
    {
        var key = Convert.ToHexString(mac);
        return _dutyTargets.TryGetValue(key, out var targets) ? targets : DefaultDutyTargets;
    }

    // L-Connect SyncMasterClock: once a second, broadcast pipe (USB rx 0xFF).
    private void SendClockHeartbeatLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = Slv3Protocol.BuildClockSync(_masterMac, DateTime.Now);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            _tx.RfSend(frame);
        }
    }

    /// <summary>
    /// Requests a bind to the first free slot (1..13); the poll ticks drive the
    /// state machine to completion. Fails if the MAC has never been seen in a
    /// device-list report. A fan already bound to us is left on its current
    /// slot rather than being reassigned a new one.
    /// </summary>
    public bool Bind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            if (IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            var slot = FirstFreeSlotLocked();
            if (slot < 0)
            {
                return false;
            }
            _pending[key] = new Slv3PendingOp(mac, (byte)slot, Unbind: false, PendingOpTickBudget);
            // First frame goes out now; the poll re-sends until the device list
            // confirms, so a request does not wait up to a full tick to start.
            SendBindFrameLocked(existing, (byte)slot, unbind: false);
        }
        return true;
    }

    /// <summary>
    /// Requests a release (slot 0, master cleared); the poll ticks drive the
    /// state machine to completion. Fails if the MAC has never been seen in a
    /// device-list report. A fan already unbound is a no-op.
    /// </summary>
    public bool Unbind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            if (!IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            _pending[key] = new Slv3PendingOp(mac, 0, Unbind: true, PendingOpTickBudget);
            SendBindFrameLocked(existing, 0, unbind: true);
        }
        return true;
    }

    /// <summary>
    /// Arms the TX for wireless-LCD frame traffic: CMD_VIDEO_START then one
    /// prep frame per known device (lian-li-linux ensure_video_mode). L-Connect
    /// sends this before streaming to the SL-LCD screens; the screens' video RF
    /// shares the 2.4 GHz air with this control link (Y70: fan telemetry
    /// collapsed once 3 screens started streaming without it). Idempotent until
    /// the next disconnect.
    /// </summary>
    public bool EnsureVideoMode()
    {
        lock (_lock)
        {
            // Re-arm when chains appeared after the last arming: the LCD loops
            // call this on USB attach, typically before the first GetDev poll
            // has populated the chain list, and un-prepped chains would keep
            // colliding with the video link.
            var deviceCount = Math.Max(1, _knownChains.Count);
            if (_videoModeActive && deviceCount <= _videoModePreppedCount)
            {
                return true;
            }
            if (_tx is null)
            {
                return false;
            }
            if (!_tx.RfSend(Slv3Protocol.BuildVideoStart()))
            {
                return false;
            }
            // Video-start is wire-identical to GetMac(channel 1); drain the
            // reply so it cannot be consumed by a later channel scan.
            _tx.RfRead(Slv3Protocol.UsbPacketSize);
            // Reference gaps: 2 ms after video-start, 1 ms between prep frames.
            Thread.Sleep(2);
            for (var i = 0; i < deviceCount; i++)
            {
                if (!_tx.RfSend(Slv3Protocol.BuildVideoPrep((byte)i, _channel)))
                {
                    return false;
                }
                Thread.Sleep(1);
            }
            _videoModeActive = true;
            _videoModePreppedCount = deviceCount;
            ServiceLog.Info($"[lianli-wireless] video mode armed ({deviceCount} device(s))");
            return true;
        }
    }

    /// <summary>
    /// Sends RF RebootLcd (0x16) at a chain: the first frame now, then once per
    /// poll until the chain's record echoes the command sequence (L-Connect
    /// RebootLcdGroup, up to <see cref="SequencedCommandBudget"/> sends).
    /// Recovery attempt for a chain that beacons header-only records (0 fans,
    /// no RPM) while staying reachable.
    /// </summary>
    public bool ResetChain(string macHex) => QueueSequencedCommand(macHex, Slv3Protocol.RfRebootChain, "chain reset");

    /// <summary>
    /// Flashes a fan for identification: RF_Select now, then once per poll
    /// until the chain echoes the command sequence (L-Connect SelectedGroup).
    /// </summary>
    public bool Identify(string macHex) => QueueSequencedCommand(macHex, Slv3Protocol.RfSelect, "identify");

    private bool QueueSequencedCommand(string macHex, byte rfCmd, string label)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            _lastIssuedSeq.TryGetValue(key, out var lastIssued);
            var seq = Slv3Protocol.NextCmdSeq((byte)Math.Max(lastIssued, record.CmdSeq));
            if (!SendSequencedCommandLocked(record, rfCmd, seq))
            {
                return false;
            }
            _lastIssuedSeq[key] = seq;
            _pendingCommands[key] = new Slv3PendingCommand(mac, rfCmd, seq, SequencedCommandBudget - 1);
            ServiceLog.Info($"[lianli-wireless] {label} sent to {macHex} (seq {seq})");
            return true;
        }
    }

    /// <summary>
    /// Uploads one animation frame set to a bound fan chain exactly as
    /// L-Connect's SyncRgbData does: the header packet (index 0) four times
    /// ~20 ms apart, then each data packet once, addressed to the fan's
    /// current (channel, rxType) pipe. Returns false (and sends nothing) if
    /// the fan is unknown, not bound to us, or a send fails partway;
    /// <paramref name="effectIndexHex"/> carries the effect_index actually
    /// sent on success, so the caller can compare it against the fan's next
    /// device-list echo to detect a dropped push.
    /// </summary>
    public bool SendRgbFrame(
        string macHex, ReadOnlySpan<RgbColor> leds, int brightnessPercent, int intervalMs, out string effectIndexHex)
    {
        effectIndexHex = "";
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        byte channel, rxType;
        byte[] effectIndex;
        byte[][] packets;
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record) || !IsBoundToUsLocked(record))
            {
                return false;
            }

            var raw = Slv3RgbFrame.BuildFrameBuffer(leds, brightnessPercent);
            byte[] compressed;
            try
            {
                compressed = TinyUz.Compress(raw);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            effectIndex = Slv3RgbFrame.BuildEffectIndex(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            packets = Slv3RgbFrame.BuildPackets(
                record.Mac, _masterMac, effectIndex, compressed, leds.Length, totalFrames: 1, intervalMs);
            channel = record.Channel;
            rxType = record.RxType;
        }

        // The header gaps run with _lock RELEASED so the device-list poll keeps
        // running even with several chains streaming. The repeats are
        // identical, so a keepalive/poll frame slipping into a gap is harmless.
        for (var i = 0; i < RgbHeaderRepeats; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(RgbHeaderGapMs);
            }
            if (!SendRfPayload(channel, rxType, packets[0]))
            {
                return false;
            }
        }

        // Data parts carry the reassembly sequence - send them contiguously under
        // one lock so a concurrent frame can't split them.
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            for (var p = 1; p < packets.Length; p++)
            {
                if (!SendRfPayloadLocked(channel, rxType, packets[p]))
                {
                    return false;
                }
            }
            NoteConfigChangedLocked();
        }

        effectIndexHex = Convert.ToHexString(effectIndex);
        return true;
    }

    // Same as SendRfPayloadLocked but takes _lock itself, for callers that pace
    // sends across released-lock gaps (SendRgbFrame's header repeats).
    private bool SendRfPayload(byte channel, byte rxType, byte[] payload)
    {
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            return SendRfPayloadLocked(channel, rxType, payload);
        }
    }

    // Caller holds _lock.
    private bool SendRfPayloadLocked(byte channel, byte rxType, byte[] payload)
    {
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(channel, rxType, payload))
        {
            if (!_tx!.RfSend(frame))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Sets a fan chain's port duty target for the next PWM sync
    /// (plans/lianli-wireless-support.md section 3): null follows the
    /// motherboard PWM header, otherwise a manual percent (0..100). Takes
    /// effect on the connection worker's next DriveTick, where the reported
    /// duty's drift from the new target sends the bind/PWM frame. Returns
    /// false for a malformed MAC or a port outside
    /// [0, <see cref="Slv3Protocol.PortsPerRecord"/>).
    /// </summary>
    public bool SetPortDuty(string macHex, int port, int? percent)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        if (port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return false;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            if (!_dutyTargets.TryGetValue(key, out var targets))
            {
                targets = new int?[Slv3Protocol.PortsPerRecord];
                _dutyTargets[key] = targets;
            }
            targets[port] = percent is null ? null : Math.Clamp(percent.Value, 0, 100);
        }
        return true;
    }

    /// <summary>Current duty target for a chain's port (null = unset/motherboard-sync).</summary>
    public int? GetPortDuty(string macHex, int port)
    {
        if (!TryParseMac(macHex, out var mac) || port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return null;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            return _dutyTargets.TryGetValue(key, out var targets) ? targets[port] : null;
        }
    }

    /// <summary>Sets our operating channel; must be the default or an odd value (firmware rejects even). Reaches a bound chain with its next bind/PWM frame.</summary>
    public bool SetChannel(int channel)
    {
        if (channel != Slv3Protocol.DefaultChannel && (channel < 1 || channel > 39 || channel % 2 == 0))
        {
            return false;
        }
        lock (_lock)
        {
            _channel = (byte)channel;
            State.Channel = channel;
        }
        return true;
    }

    // Slots already used by a fan bound to us, or already claimed by an in-flight
    // bind, are excluded. Caller holds _lock.
    private int FirstFreeSlotLocked()
    {
        var used = new HashSet<int>();
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record))
            {
                used.Add(record.RxType);
            }
        }
        foreach (var op in _pending.Values)
        {
            if (!op.Unbind)
            {
                used.Add(op.TargetSlot);
            }
        }
        for (var slot = Slv3Protocol.MinSlot; slot <= Slv3Protocol.MaxSlot; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    private static bool TryParseMac(string macHex, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(macHex))
        {
            return false;
        }
        var clean = macHex.Replace(":", "").Replace("-", "").Trim();
        if (clean.Length != Slv3Protocol.MacLength * 2)
        {
            return false;
        }
        try
        {
            mac = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisconnectLocked();
        }
    }

    private readonly record struct Slv3PendingOp(byte[] Mac, byte TargetSlot, bool Unbind, int TicksRemaining);

    private readonly record struct Slv3PendingCommand(byte[] Mac, byte RfCmd, byte TargetSeq, int SendsRemaining);

    private readonly record struct Slv3KnownChain(Slv3DeviceRecord Record, long LastSeenMs);
}
