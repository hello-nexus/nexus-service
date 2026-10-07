using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The HydroShift II LCD-S pump head. The glass, pump, fan headers and ring all share
/// this one WinUSB pipe, so <see cref="HydroShift2Aio"/> drives the AIO half through
/// <see cref="BulkPanelHub.Exchange{T}"/> between frames.
/// </summary>
public sealed class HydroShift2LcdDriver : IBulkPanelDriver
{
    public const string Id = "lianli-hydroshift2";

    private const int ReplyTimeoutMs = 1000;
    private const int FrameRate = 30;

    /// <summary>Larger than one 512-byte reply, so the zero-length packet that ends it completes the same read.</summary>
    private readonly byte[] _reply = new byte[1024];

    private readonly Stopwatch _clock = new();
    private uint _lastTimestamp;
    private BgraJpegEncoder? _jpeg;

    /// <summary>The last pump/fan write was a Nexus-driven target, so disconnecting must hand back the defaults.</summary>
    private bool _handBack;

    public string HandlerId => Id;
    public string Name => "Lian Li HydroShift II LCD-S";
    public int VendorId => 0x1CBE;
    public IReadOnlyList<int> ProductIds { get; } = new[] { HydroShift2Protocol.ProductIdSquare };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;
    public int Fps => FrameRate;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x81;
    public bool NeedsHidChannel => false;
    public bool SupportsBrightness => true;

    /// <summary>Firmware string read at connect, or null before it.</summary>
    public string? Firmware { get; private set; }

    /// <summary>Milliseconds since connect, clamped to strictly increase: the firmware drops a repeated stamp.</summary>
    private uint NextTimestamp()
    {
        var now = (uint)_clock.ElapsedMilliseconds;
        _lastTimestamp = now > _lastTimestamp ? now : _lastTimestamp + 1;
        return _lastTimestamp;
    }

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _clock.Restart();
        _lastTimestamp = 0;

        var version = Exchange(pipe, HydroShift2Protocol.CommandGetVersion, HydroShift2Protocol.EncodeCommand(
            HydroShift2Protocol.CommandGetVersion, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        if (version is null)
        {
            ServiceLog.Warn($"[{HandlerId}] no reply to GetVersion");
            return null;
        }
        Firmware = HydroShift2Protocol.DecodeVersion(version);

        // StopPlay ends any video the panel was looping; SetClock sync-only plus StopClock
        // keep the firmware clock face off the glass.
        Command(pipe, HydroShift2Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, HydroShift2Protocol.CommandFrameRate, stackalloc byte[] { FrameRate });
        Exchange(pipe, HydroShift2Protocol.CommandSetClock, HydroShift2Protocol.EncodeSetClock(DateTime.Now, NextTimestamp()));
        Command(pipe, HydroShift2Protocol.CommandStopClock, stackalloc byte[] { 0 });
        ClearOverlay(pipe);

        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(HydroShift2Protocol.Width, HydroShift2Protocol.Height);
        ServiceLog.Info($"[{HandlerId}] firmware {Firmware ?? "unknown"}");
        return (HydroShift2Protocol.Width, HydroShift2Protocol.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (_jpeg is null)
        {
            return false;
        }
        var packet = HydroShift2Protocol.EncodeImage(
            HydroShift2Protocol.CommandPushJpeg, _jpeg.Encode(bgra), NextTimestamp());
        if (!pipe.Write(packet))
        {
            return false;
        }
        ReadReply(pipe, HydroShift2Protocol.CommandPushJpeg);
        return true;
    }

    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) =>
        Command(pipe, HydroShift2Protocol.CommandBrightness, stackalloc byte[] { (byte)Math.Clamp(percent, 0, 100) });

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (_handBack)
        {
            Span<byte> fans = stackalloc byte[HydroShift2Protocol.FanSlots];
            fans.Fill(HydroShift2Protocol.DefaultFanByte);
            SyncPumpFan(pipe, HydroShift2Protocol.DefaultPumpRpm, fans, driven: false);
        }
        pipe.Write(HydroShift2Protocol.EncodeCommand(
            HydroShift2Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        _jpeg?.Dispose();
        _jpeg = null;
    }

    /// <summary>Reads pump, fan and coolant telemetry. Call only under the hub's lock.</summary>
    public HydroShift2Params? ReadParams(IBulkUsbPipe pipe)
    {
        var reply = Exchange(pipe, HydroShift2Protocol.CommandGetParams, HydroShift2Protocol.EncodeCommand(
            HydroShift2Protocol.CommandGetParams, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        return reply is null ? null : HydroShift2Protocol.DecodeParams(reply);
    }

    /// <summary>Sets the pump target and raw fan bytes; <paramref name="driven"/> marks a Nexus target rather than the defaults. Call only under the hub's lock.</summary>
    public bool SyncPumpFan(IBulkUsbPipe pipe, int pumpRpm, ReadOnlySpan<byte> fans, bool driven)
    {
        var answered = Exchange(pipe, HydroShift2Protocol.CommandSyncPumpFan,
            HydroShift2Protocol.EncodeSyncPumpFan(pumpRpm, fans, NextTimestamp())) is not null;
        // A driven target is assumed latched even unanswered; a hand-back only counts once answered.
        if (driven || answered)
        {
            _handBack = driven;
        }
        return answered;
    }

    /// <summary>
    /// Pushes a transparent overlay, which wipes what the firmware draws over the frames: its
    /// coolant readout, or its own wireless screen after a switch to RF control. Call only
    /// under the hub's lock.
    /// </summary>
    public bool ClearOverlay(IBulkUsbPipe pipe) =>
        Exchange(pipe, HydroShift2Protocol.CommandPushPng, HydroShift2Protocol.EncodeImage(
            HydroShift2Protocol.CommandPushPng, HydroShift2Protocol.EmptyOverlayPng(), NextTimestamp())) is not null;

    /// <summary>Uploads a ring animation (packed RGB frames) for the firmware to loop. Call only under the hub's lock.</summary>
    public bool PushRing(IBulkUsbPipe pipe, ReadOnlySpan<byte> frames, int frameCount, byte intervalTicks) =>
        Exchange(pipe, HydroShift2Protocol.CommandPushRgb,
            HydroShift2Protocol.EncodeRing(frames, frameCount, intervalTicks, NextTimestamp())) is not null;

    private bool Command(IBulkUsbPipe pipe, byte command, ReadOnlySpan<byte> parameters) =>
        Exchange(pipe, command, HydroShift2Protocol.EncodeCommand(command, parameters, NextTimestamp())) is not null;

    /// <summary>Writes one command and returns its reply, or null when the write failed or nothing answered.</summary>
    private byte[]? Exchange(IBulkUsbPipe pipe, byte command, byte[] packet) =>
        pipe.Write(packet) ? ReadReply(pipe, command) : null;

    /// <summary>
    /// The reply to <paramref name="command"/>. Replies from earlier commands that timed out
    /// can still be queued on the IN pipe, so anything that does not echo the command is skipped.
    /// </summary>
    private byte[]? ReadReply(IBulkUsbPipe pipe, byte command)
    {
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return null;
            }
            int read = pipe.Read(_reply, (int)remaining);
            if (read < 0)
            {
                return null;
            }
            if (read > 0 && _reply[0] == command)
            {
                return _reply.AsSpan(0, read).ToArray();
            }
        }
    }
}
