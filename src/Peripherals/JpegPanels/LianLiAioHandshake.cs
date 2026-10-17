using System;
using System.Text;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Puts a Lian Li AIO LCD into application mode - the state in which it renders pushed
/// frames instead of its own firmware UI - and hands the glass back on detach.
///
/// Shared by the HydroShift LCD and Galahad II families, which run one protocol over three
/// report ids: A (report 1, 64 bytes) for pump/fan/firmware, B (report 2, 1024 bytes) for
/// LCD control and frames, C (report 3, 512 bytes) for frames on newer firmware. Streaming
/// needs only the B-command, so only it is implemented here.
///
/// Without this the panel accepts every frame report and keeps showing its local UI, which
/// is why this family cannot be driven by fixed init reports the way ID-Cooling's is.
/// </summary>
public sealed class LianLiAioHandshake : IJpegPanelHandshake, IJpegPanelBrightness, IJpegPanelSession
{
    /// <summary>B-command carrying control mode, brightness, rotation and frame rate.</summary>
    private const byte CmdLcdControl = 0x0C;

    /// <summary>A-command returning the firmware string, then a second date/time response.</summary>
    private const byte CmdGetFirmware = 0x86;

    /// <summary>Longest firmware string the 64-byte A-frame can carry.</summary>
    private const int AMaxPayload = 58;

    /// <summary>LcdControlMode: 0 leaves the firmware UI in charge, 1 takes the glass.</summary>
    private const byte ModeLocalUi = 0x00;
    private const byte ModeApplication = 0x01;

    /// <summary>
    /// Family-wide LcdSetting mode used for brightness-only control packets (reference
    /// driver: not Galahad-specific, HydroShift takes it too).
    /// </summary>
    internal const byte LcdSettingMode = 0x04;

    /// <summary>Backlight a panel runs at until the user sets one.</summary>
    public const byte DefaultBrightness = 100;

    private const byte Rotate0 = 0x00;

    /// <summary>A silent panel does not fail the attach; the frame path does not need the firmware.</summary>
    private const int FirmwareTimeoutMs = 1000;

    /// <summary>Matches the reference driver's per-frame ack read; the ack is normally already queued.</summary>
    private const int AckTimeoutMs = 20;

    /// <summary>A-command status poll; the reference driver's background reader also uses it as the keepalive.</summary>
    private const byte CmdHandshake = 0x81;

    /// <summary>A-command that resets the panel's MCU; the recovery step when the LCD reports unavailable.</summary>
    private const byte CmdResetDevice = 0x8E;

    /// <summary>B-command asking whether the LCD is available; the reply carries one data byte, 0 meaning available.</summary>
    private const byte CmdLcdAvailable = 0x17;

    /// <summary>The reference driver waits this long after the device opens before it talks to it.</summary>
    public const int SettleDelayMs = 10_000;

    /// <summary>Pause between the status handshake and the application-mode claim.</summary>
    private const int PostHandshakeDelayMs = 2000;

    private const int KeepaliveIntervalMs = 1000;

    /// <summary>Reads a keepalive spends looking for the status reply behind queued acks.</summary>
    private const int ObservedMaxReads = 4;

    /// <summary>Consecutive failed status writes before the handle counts as dead.</summary>
    private const int DeadHandleTicks = 3;
    private const int HandshakeTimeoutMs = 1000;
    private const int FirmwareAttempts = 3;
    private const int FirmwareRetryDelayMs = 2000;

    /// <summary>Minimum spacing of availability checks and recovery attempts.</summary>
    private const int RecoveryCooldownMs = 2000;
    private const int UnavailableThreshold = 3;
    private const int AvailabilityTimeoutMs = 1000;
    private const int ResetTimeoutMs = 1000;
    private const int ResetMaxReads = 20;
    private const int PostResetDelayMs = 500;
    private const int MaxStaleReads = 16;
    private const int InputBufferLength = 512;

    /// <summary>An availability check is skipped while frames flow, as the reference driver does for an active stream.</summary>
    private const int StreamActiveWindowMs = 2000;

    /// <summary>B-frame header length: the availability reply's data length sits in bytes 9 and 10, its data after them.</summary>
    private const int BHeaderLength = 11;

    private const byte ReportIdB = 0x02;

    /// <summary>Far enough back that a timestamp difference never wraps.</summary>
    private const long NeverMs = long.MinValue / 2;

    private readonly string _handlerId;
    private readonly byte _fps;
    private readonly bool _referenceSession;
    private readonly Action<int, CancellationToken> _sleep;
    private object? _gate;
    private CancellationToken _cancel;
    private int _tickWriteFailures;
    private readonly Func<long> _clock;
    private byte[]? _report;

    private Action<IHidDevice, byte[], LianLiAioStatus?>? _keepaliveObserver;
    private int _observedProductId;

    private long _lastKeepaliveMs = NeverMs;
    private long _lastRecoveryMs = NeverMs;
    private long _lastFrameMs = NeverMs;
    private int _unavailableCount;

    // The panel forgets the backlight across a power cycle; holding it here is what lets
    // the attach packet carry it back.
    private byte _brightness = DefaultBrightness;

    /// <param name="referenceSession">
    /// Follows the reference driver's full session: settle before attach, firmware retries,
    /// status handshake before the claim, a status keepalive, and LCD availability recovery.
    /// Off keeps the bare firmware-then-claim attach the Galahad II family was verified with.
    /// </param>
    public LianLiAioHandshake(string handlerId, int fps, bool referenceSession = false)
        : this(handlerId, fps, referenceSession, WaitCancellable, () => Environment.TickCount64)
    {
    }

    internal LianLiAioHandshake(string handlerId, int fps, bool referenceSession, Action<int, CancellationToken> sleep, Func<long> clock)
    {
        _handlerId = handlerId;
        _fps = (byte)Math.Clamp(fps, 1, 60);
        _referenceSession = referenceSession;
        _sleep = sleep;
        _clock = clock;
    }

    private static void WaitCancellable(int ms, CancellationToken cancel)
    {
        if (cancel.WaitHandle.WaitOne(ms))
        {
            cancel.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Waits without holding the hub's lock, so brightness and serial reads do not stall
    /// behind the settle pauses, and ends early with <see cref="OperationCanceledException"/>
    /// on shutdown.
    /// </summary>
    private void Pause(int ms)
    {
        _cancel.ThrowIfCancellationRequested();
        var gate = _gate;
        if (gate != null)
        {
            Monitor.Exit(gate);
        }
        try
        {
            _sleep(ms, _cancel);
        }
        finally
        {
            if (gate != null)
            {
                Monitor.Enter(gate);
            }
        }
        _cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Hands the observer the status reply of each keepalive on <paramref name="productId"/>, under the
    /// hub lock. Other products keep the single drained read.
    /// </summary>
    public void SetKeepaliveObserver(int productId, Action<IHidDevice, byte[], LianLiAioStatus?> observer)
    {
        _observedProductId = productId;
        _keepaliveObserver = observer;
    }

    public int SettleMs => _referenceSession ? SettleDelayMs : 0;

    public int TickIntervalMs => _referenceSession ? KeepaliveIntervalMs : 0;

    public bool OnAttach(IHidDevice device, int reportLength) =>
        Attach(device, reportLength, null, CancellationToken.None);

    public bool Attach(IHidDevice device, int reportLength, object? gate, CancellationToken cancel)
    {
        _gate = gate;
        _cancel = cancel;
        try
        {
            return AttachCore(device, reportLength);
        }
        finally
        {
            _gate = null;
            _cancel = CancellationToken.None;
        }
    }

    private bool AttachCore(IHidDevice device, int reportLength)
    {
        var report = Buffer(reportLength);
        _tickWriteFailures = 0;

        // The one two-way exchange this panel offers, so the only evidence the handle reaches
        // the panel rather than just the HID stack.
        string? firmware = null;
        int attempts = _referenceSession ? FirmwareAttempts : 1;
        for (int attempt = 1; attempt <= attempts && firmware == null; attempt++)
        {
            firmware = ReadFirmware(device, report);
            if (firmware == null && attempt < attempts)
            {
                Pause(FirmwareRetryDelayMs);
            }
        }
        if (firmware != null)
        {
            ServiceLog.Info($"[{_handlerId}] firmware: {firmware}");
        }
        else
        {
            ServiceLog.Warn($"[{_handlerId}] no firmware response; continuing to application mode");
        }

        // The reference driver polls status once before claiming the glass, then gives the
        // panel a moment; a panel claimed straight out of boot accepts the claim and stays blank.
        if (_referenceSession)
        {
            if (!SendHandshake(device, report, readReply: true))
            {
                ServiceLog.Warn($"[{_handlerId}] status handshake not answered; continuing to application mode");
            }
            Pause(PostHandshakeDelayMs);
        }

        if (!SendLcdControl(device, report, ModeApplication, _brightness))
        {
            ServiceLog.Warn($"[{_handlerId}] application-mode command rejected; dropping the handle");
            return false;
        }
        _lastKeepaliveMs = _clock();
        _lastRecoveryMs = _lastKeepaliveMs;
        _unavailableCount = 0;
        return true;
    }

    public void OnDetach(IHidDevice device, int reportLength)
    {
        // Best effort: leaving the panel in application mode would strand it on the last
        // frame Nexus pushed, with nothing driving it. Full brightness goes with it - a
        // firmware UI left dimmed cannot be undone once the handle is gone.
        SendLcdControl(device, Buffer(reportLength), ModeLocalUi, DefaultBrightness);
    }

    /// <summary>
    /// Drains the ack the panel posts for the previous frame. Application mode holds without
    /// re-asserting, but the IN endpoint must be emptied: left unread it backs up and the
    /// panel stops accepting output reports, so one chunk per frame blocks for the write
    /// timeout and the frame is dropped. The reference driver reads the same ack per frame.
    /// </summary>
    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs)
    {
        _lastFrameMs = nowMs;
        Span<byte> ack = stackalloc byte[64];
        // Negative is a dead handle, distinct from 0 for "no ack queued"; writing a frame
        // into it would burn the write timeout per chunk.
        return device.Read(ack, AckTimeoutMs) >= 0;
    }

    public int Brightness => _brightness;

    public void SetBrightness(int percent) => _brightness = (byte)Math.Clamp(percent, 0, 100);

    /// <summary>Application mode, so the write that carries the backlight keeps the glass claimed.</summary>
    public bool ApplyBrightness(IHidDevice device, int reportLength)
    {
        var report = Buffer(reportLength);
        if (!SendLcdControl(device, report, LcdSettingMode, _brightness))
        {
            return false;
        }
        // The frame path drains one ack per frame, so a control write must drain its own or
        // the IN endpoint backs up until the panel stops taking output reports.
        Span<byte> ack = stackalloc byte[64];
        device.Read(ack, AckTimeoutMs);
        return true;
    }

    /// <summary>
    /// LCD control payload: mode, brightness, rotation, four reserved bytes, frame rate.
    /// It rides the same 11-byte sequenced header as a frame chunk, so the frame builder
    /// composes it rather than a second hand-rolled copy of that layout.
    /// </summary>
    private bool SendLcdControl(IHidDevice device, byte[] report, byte mode, byte brightness)
    {
        Span<byte> payload = stackalloc byte[8];
        payload.Clear();
        payload[0] = mode;
        payload[1] = brightness;
        payload[2] = Rotate0;
        payload[7] = _fps;

        JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.LianLiSequenced, CmdLcdControl, payload, 0, 0);
        return device.Write(report);
    }

    /// <summary>
    /// The status poll the reference driver runs for the whole session, then the LCD
    /// availability check with reset recovery. Both ride the hub's lock, so neither
    /// interleaves with a frame.
    /// </summary>
    public bool Tick(IHidDevice device, int reportLength, long nowMs)
    {
        if (!_referenceSession)
        {
            return true;
        }
        var report = Buffer(reportLength);
        if (nowMs - _lastKeepaliveMs >= KeepaliveIntervalMs)
        {
            _lastKeepaliveMs = nowMs;
            var observer = _keepaliveObserver;
            bool observed = observer != null && device.ProductId == _observedProductId;
            LianLiAioStatus? status = null;
            bool sent = observed
                ? SendObservedHandshake(device, report, out status)
                : SendHandshake(device, report, readReply: false);
            _tickWriteFailures = sent ? 0 : _tickWriteFailures + 1;
            if (_tickWriteFailures >= DeadHandleTicks)
            {
                ServiceLog.Warn($"[{_handlerId}] status writes keep failing; dropping the handle to re-attach");
                return false;
            }
            if (observed && sent)
            {
                observer!(device, report, status);
            }
        }

        if (nowMs - _lastRecoveryMs < RecoveryCooldownMs || nowMs - _lastFrameMs < StreamActiveWindowMs)
        {
            return true;
        }
        return CheckAndRecover(device, report, nowMs);
    }

    /// <summary>False when the handle can no longer reach the panel, so the hub must re-attach from scratch.</summary>
    private bool CheckAndRecover(IHidDevice device, byte[] report, long nowMs)
    {
        bool? available = IsLcdAvailable(device, report);
        if (available == true)
        {
            _unavailableCount = 0;
            return true;
        }
        if (++_unavailableCount < UnavailableThreshold)
        {
            return true;
        }
        _unavailableCount = 0;
        _lastRecoveryMs = nowMs;
        ServiceLog.Warn($"[{_handlerId}] LCD unavailable ({(available == false ? "reported" : "no reply")}); resetting the panel");
        if (ResetDevice(device, report))
        {
            ServiceLog.Info($"[{_handlerId}] panel reset; reclaiming the glass");
            _sleep(PostResetDelayMs, CancellationToken.None);
        }
        else
        {
            ServiceLog.Warn($"[{_handlerId}] panel reset failed; reclaiming the glass anyway");
        }
        // A reset that re-enumerates USB leaves this handle dead: the claim write fails, and
        // only a fresh attach (settle included) reopens the panel.
        if (!SendLcdControl(device, report, ModeApplication, _brightness))
        {
            ServiceLog.Warn($"[{_handlerId}] reclaim after reset was rejected; dropping the handle to re-attach");
            return false;
        }
        DrainAck(device);
        return true;
    }

    /// <summary>
    /// True when the panel says the LCD is available (or stays silent, as the reference
    /// driver reads it), false when it says unavailable, null when the exchange itself failed.
    /// </summary>
    private bool? IsLcdAvailable(IHidDevice device, byte[] report)
    {
        report.AsSpan().Clear();
        report[0] = ReportIdB;
        report[1] = CmdLcdAvailable;
        if (!device.Write(report))
        {
            return null;
        }
        Span<byte> reply = stackalloc byte[InputBufferLength];
        for (int i = 0; i < MaxStaleReads; i++)
        {
            reply.Clear();
            int read = device.Read(reply, AvailabilityTimeoutMs);
            if (read < 0)
            {
                return null;
            }
            if (read == 0)
            {
                return true;
            }
            if (reply[1] != CmdLcdAvailable)
            {
                continue;
            }
            int length = (reply[9] << 8) | reply[10];
            return length == 1 && reply[BHeaderLength] == 0;
        }
        return null;
    }

    private bool ResetDevice(IHidDevice device, byte[] report)
    {
        LianLiAioProtocol.FillACommand(report, CmdResetDevice, ReadOnlySpan<byte>.Empty);
        if (!device.Write(report))
        {
            return false;
        }
        Span<byte> reply = stackalloc byte[InputBufferLength];
        for (int i = 0; i < ResetMaxReads; i++)
        {
            reply.Clear();
            int read = device.Read(reply, ResetTimeoutMs);
            if (read <= 0)
            {
                return false;
            }
            if (read > LianLiAioProtocol.AHeaderLength && reply[1] == CmdResetDevice)
            {
                return reply[LianLiAioProtocol.AHeaderLength] == 1;
            }
        }
        return false;
    }

    private bool SendHandshake(IHidDevice device, byte[] report, bool readReply)
    {
        LianLiAioProtocol.FillACommand(report, CmdHandshake, ReadOnlySpan<byte>.Empty);
        if (!device.Write(report))
        {
            return false;
        }
        Span<byte> reply = stackalloc byte[64];
        if (!readReply)
        {
            // Keepalive: the reply is only drained, so the IN endpoint cannot back up.
            device.Read(reply, AckTimeoutMs);
            return true;
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            reply.Clear();
            if (device.Read(reply, HandshakeTimeoutMs) <= 0)
            {
                return false;
            }
            if (reply[1] == CmdHandshake)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The keepalive for an observed product: frame acks can queue ahead of the status reply, so a few reads look for it.</summary>
    private bool SendObservedHandshake(IHidDevice device, byte[] report, out LianLiAioStatus? status)
    {
        status = null;
        LianLiAioProtocol.FillACommand(report, CmdHandshake, ReadOnlySpan<byte>.Empty);
        if (!device.Write(report))
        {
            return false;
        }
        Span<byte> reply = stackalloc byte[64];
        for (int i = 0; i < ObservedMaxReads; i++)
        {
            reply.Clear();
            int read = device.Read(reply, AckTimeoutMs);
            if (read <= 0)
            {
                break;
            }
            status = LianLiAioProtocol.ParseStatus(reply[..read]);
            if (status != null)
            {
                break;
            }
        }
        return true;
    }

    private static void DrainAck(IHidDevice device)
    {
        Span<byte> ack = stackalloc byte[64];
        device.Read(ack, AckTimeoutMs);
    }

    /// <summary>
    /// Writes the firmware A-command and reads until the matching response arrives, since
    /// a stale reply from a previous session can still be queued. Returns null on timeout.
    /// </summary>
    private string? ReadFirmware(IHidDevice device, byte[] report)
    {
        LianLiAioProtocol.FillACommand(report, CmdGetFirmware, ReadOnlySpan<byte>.Empty);
        if (!device.Write(report))
        {
            return null;
        }

        Span<byte> reply = stackalloc byte[64];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            reply.Clear();
            int read = device.Read(reply, FirmwareTimeoutMs);
            if (read <= 0)
            {
                return null;
            }
            if (reply[1] != CmdGetFirmware)
            {
                continue;
            }
            int length = Math.Min((int)reply[5], AMaxPayload);
            if (length <= 0)
            {
                return null;
            }
            return Encoding.ASCII.GetString(reply.Slice(LianLiAioProtocol.AHeaderLength, length)).TrimEnd('\0').Trim();
        }
        return null;
    }

    private byte[] Buffer(int reportLength)
    {
        if (_report == null || _report.Length != reportLength)
        {
            _report = new byte[reportLength];
        }
        return _report;
    }
}
