using System;
using System.Text;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The ASUS ROG Strix SLC IV LCD's control channel, replayed from a USB capture of the
/// vendor app. The messages ride <see cref="AsRockLcdProtocol"/>'s escaped, checksummed
/// 0x5A text frame, which the capture shows byte for byte.
///
/// The vendor app opens with <c>conn</c>, <c>power</c> (resume), <c>devStateGet</c> and
/// <c>realtimeDisplay</c> (enable), then streams frames with no further control traffic;
/// it does not re-assert the display, so neither does this. Interrupt-IN was not captured,
/// so replies are read, logged for diagnosis and never required: a missing or unfamiliar
/// reply does not fail the attach.
/// </summary>
public sealed class AsusSlc4LcdHandshake : IJpegPanelHandshake, IJpegPanelBrightness
{
    private const int ReplyTimeoutMs = 500;
    private const int ReplyLogChars = 240;

    private int _sequence;
    private int _brightness = -1;

    public int Brightness => _brightness;

    public void SetBrightness(int percent) => _brightness = Math.Clamp(percent, 0, 100);

    public bool OnAttach(IHidDevice device, int reportLength)
    {
        _sequence = 0;
        if (!Send(device, reportLength, "conn", null)
            || !Send(device, reportLength, "power", "{\"event\":\"resume\"}")
            || !Send(device, reportLength, "devStateGet", null)
            || !Send(device, reportLength, "realtimeDisplay", "{\"enable\":true}"))
        {
            ServiceLog.Warn("[asus-slc4-lcd] start sequence write rejected; dropping the handle");
            return false;
        }
        // The vendor app sends brightness only on a user change, so a panel that was never
        // told one keeps its own.
        return _brightness < 0 || ApplyBrightness(device, reportLength);
    }

    /// <summary>
    /// The vendor app's "screen off" is <c>realtimeDisplay</c> false; stopping the stream the
    /// same way hands the glass back to the panel. No disconnect message was captured, so none is sent.
    /// </summary>
    public void OnDetach(IHidDevice device, int reportLength) =>
        Send(device, reportLength, "realtimeDisplay", "{\"enable\":false}");

    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs) => true;

    public bool ApplyBrightness(IHidDevice device, int reportLength) =>
        _brightness >= 0
        && Send(device, reportLength, "brightness", "{\"value\":" + _brightness.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    private bool Send(IHidDevice device, int reportLength, string command, string? jsonBody)
    {
        var text = AsRockLcdProtocol.BuildMessage(
            "POST", command, _sequence++, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), jsonBody);
        var framed = AsRockLcdProtocol.Encode(Encoding.ASCII.GetBytes(text));
        var report = new byte[reportLength];
        if (framed.Length > report.Length)
        {
            ServiceLog.Warn($"[asus-slc4-lcd] {command} message does not fit one report");
            return false;
        }
        framed.CopyTo(report, 0);
        if (!device.Write(report))
        {
            return false;
        }

        var reply = new byte[reportLength];
        int read = device.Read(reply, ReplyTimeoutMs);
        ServiceLog.Info(read > 0
            ? $"[asus-slc4-lcd] {command} reply: {Printable(reply.AsSpan(0, read))}"
            : $"[asus-slc4-lcd] {command}: no reply");
        return true;
    }

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length && sb.Length < ReplyLogChars; i++)
        {
            byte b = bytes[i];
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }
}
