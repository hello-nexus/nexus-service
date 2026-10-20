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
/// re-sends it a few times at a low rate until a reply says OK. Steady-state traffic never
/// waits on a reply: replies are drained without blocking on the frame path, and only the
/// attach waits, with the hub's lock released for the wait itself. Replies are matched to
/// messages by SeqNumber, else by command name.
/// </summary>
public sealed class AsusSlc4LcdHandshake : IJpegPanelHandshake, IJpegPanelBrightness, IJpegPanelSession
{
    /// <summary>Covers the vendor app's own gap between a message and the next.</summary>
    private const int ReplyWaitMs = 1500;

    /// <summary>Wait between polls for a reply, with the hub's lock released.</summary>
    private const int ReadSliceMs = 50;

    private const long EnableRetryMs = 5_000;
    private const int MaxEnableResends = 3;
    private const int ReplyLogChars = 240;
    private const int MaxOutstanding = 16;
    private const int MaxDrainPerFrame = 4;

    /// <summary>Messages sent and not yet answered, by SeqNumber.</summary>
    private readonly Dictionary<int, string> _outstanding = new();
    private byte[] _drain = Array.Empty<byte>();
    private int _sequence;
    private int _brightness = -1;
    private bool _enableConfirmed;
    private int _enableResends;
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
        _enableResends = 0;
        _outstanding.Clear();

        if (!Exchange(device, reportLength, "conn", null, out var conn)
            || !Exchange(device, reportLength, "power", "{\"event\":\"resume\"}", out _)
            || !Exchange(device, reportLength, "waterBlockScreen", "{\"enable\":true}", out _)
            || !Exchange(device, reportLength, "devStateGet", null, out _)
            || !Exchange(device, reportLength, "realtimeDisplay", "{\"enable\":true}", out _))
        {
            ServiceLog.Warn("[asus-slc4-lcd] start sequence write rejected; dropping the handle");
            return false;
        }
        if (conn is not null && !AsRockLcdProtocol.ParseBootFinished(AsRockLcdProtocol.Decode(conn)))
        {
            ServiceLog.Info("[asus-slc4-lcd] connect reply does not report a finished boot");
        }
        _lastEnableMs = Environment.TickCount64;
        if (!_enableConfirmed)
        {
            ServiceLog.Warn("[asus-slc4-lcd] realtime display enable was not acknowledged; it is re-sent a few times while frames stream");
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
        Write(device, reportLength, "realtimeDisplay", "{\"enable\":false}");

    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs)
    {
        if (_drain.Length != reportLength)
        {
            _drain = new byte[reportLength];
        }
        for (int i = 0; i < MaxDrainPerFrame; i++)
        {
            int read = device.Read(_drain, 0);
            if (read <= 0)
            {
                break;
            }
            Dispatch(_drain.AsSpan(0, read).ToArray());
        }
        if (!_enableConfirmed && nowMs - _lastEnableMs >= EnableRetryMs)
        {
            if (_enableResends >= MaxEnableResends)
            {
                if (_enableResends == MaxEnableResends)
                {
                    _enableResends++;
                    ServiceLog.Info("[asus-slc4-lcd] realtime display enable never acknowledged; not re-sending again");
                }
                return true;
            }
            _enableResends++;
            _lastEnableMs = nowMs;
            Write(device, reportLength, "realtimeDisplay", "{\"enable\":true}");
        }
        return true;
    }

    /// <summary>Write only: the reply is drained by <see cref="BeforeFrame"/>, never waited for here.</summary>
    public bool ApplyBrightness(IHidDevice device, int reportLength) =>
        _brightness >= 0
        && Write(device, reportLength, "brightness", "{\"value\":" + _brightness.ToString(CultureInfo.InvariantCulture) + "}");

    private bool Write(IHidDevice device, int reportLength, string command, string? jsonBody)
    {
        int sequence = _sequence++;
        var text = AsRockLcdProtocol.BuildMessage(
            "POST", command, sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), jsonBody);
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
        if (_outstanding.Count >= MaxOutstanding)
        {
            // The pending enable is the one entry a stream depends on, so it is never the one evicted.
            int oldest = int.MaxValue;
            foreach (var pair in _outstanding)
            {
                if (pair.Value != "realtimeDisplay")
                {
                    oldest = Math.Min(oldest, pair.Key);
                }
            }
            _outstanding.Remove(oldest);
        }
        _outstanding[sequence] = command;
        return true;
    }

    /// <summary>
    /// Attach-time send and wait. Each pass polls without blocking while holding the hub's
    /// lock, then waits out a slice with the lock released, so a frame or brightness call is
    /// never held behind a slow reply. Replies for other commands that arrive meanwhile are
    /// dispatched, not mistaken for this one.
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
        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = device.Read(buffer, 0);
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
                var copy = buffer.AsSpan(0, read).ToArray();
                if (Dispatch(copy) == command)
                {
                    reply = copy;
                    return true;
                }
                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }
                continue;
            }
            if (Environment.TickCount64 >= deadline)
            {
                break;
            }
            WaitSlice();
        }
        ServiceLog.Info($"[asus-slc4-lcd] {command}: no reply");
        return true;
    }

    private void WaitSlice()
    {
        var gate = _gate;
        if (gate is not null)
        {
            Monitor.Exit(gate);
        }
        try
        {
            _cancel.WaitHandle.WaitOne(ReadSliceMs);
        }
        finally
        {
            if (gate is not null)
            {
                Monitor.Enter(gate);
            }
        }
    }

    /// <summary>
    /// Matches a reply to the message it answers by SeqNumber, else by command name, logs it and
    /// returns the command; an unmatched reply is logged and returns null, and confirms nothing.
    /// </summary>
    private string? Dispatch(byte[] reply)
    {
        var decoded = AsRockLcdProtocol.Decode(reply);
        string text = decoded.Length > 0 ? Encoding.ASCII.GetString(decoded) : Printable(reply);
        string? command = null;
        int key = -1;
        if (TryParseSequence(text, out int sequence) && _outstanding.TryGetValue(sequence, out var bySeq))
        {
            command = bySeq;
            key = sequence;
        }
        else if (!TryParseSequence(text, out _))
        {
            foreach (var pair in _outstanding)
            {
                if (text.Contains(pair.Value, StringComparison.Ordinal) && (key < 0 || pair.Key < key))
                {
                    command = pair.Value;
                    key = pair.Key;
                }
            }
        }
        if (command is null)
        {
            ServiceLog.Info($"[asus-slc4-lcd] unmatched reply: {Printable(reply)}");
            return null;
        }
        _outstanding.Remove(key);
        LogReply(command, reply);
        if (command == "realtimeDisplay" && IsOk(reply))
        {
            _enableConfirmed = true;
        }
        return command;
    }

    private static bool TryParseSequence(string text, out int sequence)
    {
        sequence = 0;
        const string tag = "SeqNumber=";
        int at = text.IndexOf(tag, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }
        int end = at + tag.Length;
        int start = end;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }
        return end > start && int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private static bool IsOk(byte[] reply)
    {
        var decoded = AsRockLcdProtocol.Decode(reply);
        if (decoded.Length > 0)
        {
            // A decoded reply answers for itself; one with no status is not an OK.
            return AsRockLcdProtocol.ParseStatus(decoded) == "200";
        }
        // Nothing decoded and the reply layout was never captured; fall back to the status text itself.
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
