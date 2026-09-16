using System;
using System.Text;
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
public sealed class LianLiAioHandshake : IJpegPanelHandshake, IJpegPanelBrightness
{
    /// <summary>B-command carrying control mode, brightness, rotation and frame rate.</summary>
    private const byte CmdLcdControl = 0x0C;

    /// <summary>A-command returning the firmware string, then a second date/time response.</summary>
    private const byte CmdGetFirmware = 0x86;

    private const byte ReportIdA = 0x01;

    /// <summary>A-command frame: report id, command, three pad bytes, payload length.</summary>
    private const int AHeaderLength = 6;

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

    private readonly string _handlerId;
    private readonly byte _fps;
    private byte[]? _report;

    // The panel forgets the backlight across a power cycle; holding it here is what lets
    // the attach packet carry it back.
    private byte _brightness = DefaultBrightness;

    public LianLiAioHandshake(string handlerId, int fps)
    {
        _handlerId = handlerId;
        _fps = (byte)Math.Clamp(fps, 1, 60);
    }

    public bool OnAttach(IHidDevice device, int reportLength)
    {
        var report = Buffer(reportLength);

        // The one two-way exchange this panel offers, so the only evidence the handle reaches
        // the panel rather than just the HID stack.
        var firmware = ReadFirmware(device, report);
        if (firmware != null)
        {
            ServiceLog.Info($"[{_handlerId}] firmware: {firmware}");
        }
        else
        {
            ServiceLog.Warn($"[{_handlerId}] no firmware response; continuing to application mode");
        }

        if (!SendLcdControl(device, report, ModeApplication, _brightness))
        {
            ServiceLog.Warn($"[{_handlerId}] application-mode command rejected; dropping the handle");
            return false;
        }
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
    /// Writes the firmware A-command and reads until the matching response arrives, since
    /// a stale reply from a previous session can still be queued. Returns null on timeout.
    /// </summary>
    private string? ReadFirmware(IHidDevice device, byte[] report)
    {
        // A-commands are 64-byte frames, but Windows wants exactly the collection's output
        // report length, so the frame goes out zero-padded to the full report.
        report.AsSpan().Clear();
        report[0] = ReportIdA;
        report[1] = CmdGetFirmware;
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
            return Encoding.ASCII.GetString(reply.Slice(AHeaderLength, length)).TrimEnd('\0').Trim();
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
