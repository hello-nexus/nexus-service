using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The ASUS ROG Strix SLC IV LCD's control channel, replayed from a USB capture of the
/// vendor app. The messages ride <see cref="AsRockLcdProtocol"/>'s escaped, checksummed
/// 0x5A text frame, which the capture shows byte for byte.
///
/// The vendor app opens with <c>conn</c> and <c>power</c> (resume), then its screen-on
/// exchange: <c>waterBlockScreen</c> (enable), <c>devStateGet</c>, <c>realtimeDisplay</c>
/// (enable). It then streams frames with no further control traffic. Interrupt-IN was not
/// captured, so replies are unseen: the attach waits for each one long enough to cover the
/// vendor's own spacing between messages, logs it, and carries on when none comes.
///
/// The enable is the one message a frame stream depends on, so <see cref="BeforeFrame"/>
/// re-sends it at a low rate until a reply says OK. Steady-state traffic never waits on a
/// reply: they are drained without blocking on the frame path, and only the attach pauses,
/// releasing the hub's lock between short reads.
/// </summary>
public sealed class AsusSlc4LcdHandshake : IJpegPanelHandshake, IJpegPanelBrightness, IJpegPanelSession
{
    /// <summary>Covers the vendor app's own gap between a message and the next.</summary>
    private const int ReplyWaitMs = 1500;

    /// <summary>One blocking read; the hub's lock is released between slices.</summary>
    private const int ReadSliceMs = 50;

    private const long EnableRetryMs = 5_000;
    private const int ReplyLogChars = 240;
    private const int MaxOutstanding = 8;
    private const int MaxDrainPerFrame = 4;

    private readonly Queue<string> _outstanding = new();
    private int _sequence;
    private int _brightness = -1;
    private bool _enableConfirmed;
    private long _lastEnableMs;
    private object? _gate;
    private CancellationToken _cancel;

    public int Brightness => _brightness;

    /// <summary>No settle period and no periodic exchange: the vendor sends nothing while streaming.</summary>
    public int SettleMs => 0;

    public int TickIntervalMs => 0;

    public bool Tick(IHidDevice device, int reportLength, long nowMs) => true;

    public void SetBrightness(int percent) => _brightness = Math.Clamp(percent, 0, 100);

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
        _sequence = 0;
        _enableConfirmed = false;
        _outstanding.Clear();

        if (!Exchange(device, reportLength, "conn", null, out var conn)
            || !Exchange(device, reportLength, "power", "{\"event\":\"resume\"}", out _)
            || !Exchange(device, reportLength, "waterBlockScreen", "{\"enable\":true}", out _)
            || !Exchange(device, reportLength, "devStateGet", null, out _)
            || !Exchange(device, reportLength, "realtimeDisplay", "{\"enable\":true}", out var enable))
        {
            ServiceLog.Warn("[asus-slc4-lcd] start sequence write rejected; dropping the handle");
            return false;
        }
        if (conn is not null && !AsRockLcdProtocol.ParseBootFinished(AsRockLcdProtocol.Decode(conn)))
        {
            ServiceLog.Info("[asus-slc4-lcd] connect reply does not report a finished boot");
        }
        _enableConfirmed = enable is not null && IsOk(enable);
        _lastEnableMs = Environment.TickCount64;
        if (!_enableConfirmed)
        {
            ServiceLog.Warn("[asus-slc4-lcd] realtime display enable was not acknowledged; it is re-sent while frames stream");
        }
        // The vendor app sends brightness only on a user change, so a panel that was never
        // told one keeps its own.
        return _brightness < 0 || ApplyBrightness(device, reportLength);
    }

    /// <summary>
    /// The vendor app's "screen off" is <c>realtimeDisplay</c> false; stopping the stream the
    /// same way hands the display back to the panel. No disconnect message was captured, so none is sent.
    /// </summary>
    public void OnDetach(IHidDevice device, int reportLength) =>
        SendOnly(device, reportLength, "realtimeDisplay", "{\"enable\":false}");

    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs)
    {
        for (int i = 0; i < MaxDrainPerFrame; i++)
        {
            var reply = new byte[reportLength];
            int read = device.Read(reply, 0);
            if (read <= 0)
            {
                break;
            }
            Receive(reply.AsSpan(0, read).ToArray());
        }
        if (!_enableConfirmed && nowMs - _lastEnableMs >= EnableRetryMs)
        {
            _lastEnableMs = nowMs;
            SendOnly(device, reportLength, "realtimeDisplay", "{\"enable\":true}");
        }
        return true;
    }

    /// <summary>Write only: the reply is drained by <see cref="BeforeFrame"/>, never waited for here.</summary>
    public bool ApplyBrightness(IHidDevice device, int reportLength) =>
        _brightness >= 0
        && SendOnly(device, reportLength, "brightness", "{\"value\":" + _brightness.ToString(CultureInfo.InvariantCulture) + "}");

    private bool SendOnly(IHidDevice device, int reportLength, string command, string? jsonBody)
    {
        if (!Write(device, reportLength, command, jsonBody))
        {
            return false;
        }
        if (_outstanding.Count >= MaxOutstanding)
        {
            _outstanding.Dequeue();
        }
        _outstanding.Enqueue(command);
        return true;
    }

    private bool Write(IHidDevice device, int reportLength, string command, string? jsonBody)
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
        return device.Write(report);
    }

    /// <summary>
    /// Attach-time send and wait. Reads in short slices and lets go of the hub's lock between
    /// them, so a frame or brightness call is never held behind a slow reply.
    /// </summary>
    private bool Exchange(IHidDevice device, int reportLength, string command, string? jsonBody, out byte[]? reply)
    {
        reply = null;
        if (!Write(device, reportLength, command, jsonBody))
        {
            return false;
        }
        long deadline = Environment.TickCount64 + ReplyWaitMs;
        var buffer = new byte[reportLength];
        while (Environment.TickCount64 < deadline)
        {
            _cancel.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = device.Read(buffer, ReadSliceMs);
            }
            catch (ObjectDisposedException)
            {
                read = -1;
            }
            if (read < 0)
            {
                break;
            }
            if (read > 0)
            {
                reply = buffer.AsSpan(0, read).ToArray();
                LogReply(command, reply);
                return true;
            }
            YieldGate();
        }
        ServiceLog.Info($"[asus-slc4-lcd] {command}: no reply");
        return true;
    }

    private void YieldGate()
    {
        var gate = _gate;
        if (gate is null)
        {
            return;
        }
        Monitor.Exit(gate);
        try
        {
            Thread.Yield();
        }
        finally
        {
            Monitor.Enter(gate);
        }
    }

    private void Receive(byte[] reply)
    {
        string command = _outstanding.Count > 0 ? _outstanding.Dequeue() : "unsolicited";
        LogReply(command, reply);
        if (command == "realtimeDisplay" && IsOk(reply))
        {
            _enableConfirmed = true;
        }
    }

    private static bool IsOk(byte[] reply)
    {
        if (AsRockLcdProtocol.ParseStatus(AsRockLcdProtocol.Decode(reply)) == "200")
        {
            return true;
        }
        // The reply layout was never captured; fall back to the status text itself.
        return Printable(reply).Contains(" 200", StringComparison.Ordinal);
    }

    private static void LogReply(string command, byte[] reply) =>
        ServiceLog.Info($"[asus-slc4-lcd] {command} reply: {Printable(reply)}");

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
