using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The feature-report session iCUE runs around the Corsair Nautilus LCD Cap: an open
/// handshake, a keepalive while streaming, and a close pair. All reports are 32 bytes on
/// report id 3, and byte 1 selects the command.
///
/// The keepalive's bytes 4 to 7 carry the size of the last completed frame as it left the
/// host (JPEG bytes plus one chunk header per chunk but the last), split into whole
/// 1024-byte blocks and the remainder, each little-endian u16. Bytes 8 and on are stale
/// buffer contents in the capture and are sent as zero.
/// </summary>
public sealed class NautilusLcdHandshake : IJpegPanelHandshake, IJpegPanelSession, IJpegPanelFrameObserver
{
    internal const int FeatureLength = 32;
    internal const byte ReportId = 0x03;

    internal const byte CmdSession = 0x1D;
    internal const byte CmdKeepalive = 0x19;
    internal const byte CmdFirmwareEcho = 0x0B;
    internal const byte CmdUnknownClaim = 0x1B;
    internal const byte CmdCloseStreaming = 0x1E;

    private const byte GetInfo = 0x13;
    private const byte GetFirmware = 0x05;
    private const byte GetStatus = 0x11;
    private const byte GetLimits = 0x0F;

    /// <summary>iCUE waits this long between reading the limits and claiming the stream.</summary>
    private const int ClaimDelayMs = 350;

    private const int KeepaliveIntervalMs = 4500;

    /// <summary>Values iCUE sent on the captured unit when the limits read gives nothing.</summary>
    private const byte FallbackClaimHigh = 0x03;
    private const byte FallbackClaimLow = 0xC4;

    private const int ChunkHeaderBytes = 8;

    private bool _streaming;
    private int _frameBytes;

    public int SettleMs => 0;

    public int TickIntervalMs => KeepaliveIntervalMs;

    public bool OnAttach(IHidDevice device, int reportLength) =>
        Attach(device, reportLength, null, CancellationToken.None);

    public bool Attach(IHidDevice device, int reportLength, object? gate, CancellationToken cancel)
    {
        _streaming = false;
        _frameBytes = 0;

        if (!device.SetFeature(Report(CmdSession, 0x01)))
        {
            ServiceLog.Warn("[corsair-nautilus-lcd] session open rejected; dropping the handle");
            return false;
        }
        Get(device, GetInfo);
        if (!device.SetFeature(Report(CmdKeepalive, 0x00, 0x00)))
        {
            return false;
        }

        var firmware = Get(device, GetFirmware);
        var echo = Report(CmdFirmwareEcho, 0x64, 0x01);
        firmware?.AsSpan(4).CopyTo(echo.AsSpan(4));
        if (!device.SetFeature(echo))
        {
            return false;
        }

        Get(device, GetStatus);
        var limits = Get(device, GetLimits);

        if (cancel.WaitHandle.WaitOne(ClaimDelayMs))
        {
            return false;
        }
        byte high = limits is null ? FallbackClaimHigh : limits[3];
        byte low = limits is null ? FallbackClaimLow : limits[4];
        return device.SetFeature(Report(CmdUnknownClaim, 0x00, high, low));
    }

    public void OnDetach(IHidDevice device, int reportLength)
    {
        device.SetFeature(Keepalive(CmdCloseStreaming, 0x00));
        device.SetFeature(Keepalive(CmdSession, 0x00));
    }

    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs) => true;

    public bool Tick(IHidDevice device, int reportLength, long nowMs) =>
        device.SetFeature(Keepalive(CmdKeepalive, 0x00));

    public void OnFrameSent(int jpegBytes, int chunks)
    {
        _streaming = true;
        _frameBytes = jpegBytes + ((chunks - 1) * ChunkHeaderBytes);
    }

    internal byte[] Keepalive(byte command, byte arg)
    {
        var report = Report(command, arg, _streaming ? (byte)1 : (byte)0);
        report[4] = (byte)(_frameBytes >> 10);
        report[5] = (byte)(_frameBytes >> 18);
        report[6] = (byte)(_frameBytes & 0xFF);
        report[7] = (byte)((_frameBytes >> 8) & 0x03);
        return report;
    }

    private static byte[] Report(byte command, params byte[] args)
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = command;
        args.CopyTo(report, 2);
        return report;
    }

    private static byte[]? Get(IHidDevice device, byte reportId)
    {
        var buffer = new byte[FeatureLength];
        buffer[0] = reportId;
        return device.GetFeature(buffer) ? buffer : null;
    }
}
