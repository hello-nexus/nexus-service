using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The HydroShift II OLED Curved glass. Its pump, LEDs and head motors sit on a separate
/// USB function that <see cref="HydroShift2CurveBoard"/> drives.
/// </summary>
public sealed class HydroShift2CurveLcdDriver : IBulkPanelDriver
{
    public const string Id = "lianli-hydroshift2-curve";

    private const int ReplyTimeoutMs = 1000;
    private const int FrameRate = 30;

    /// <summary>The glass takes frames at ~7 MB/s whatever their content (measured), so frame size sets the frame rate.</summary>
    private const int JpegQuality = 70;

    /// <summary>Larger than one 512-byte reply, so the zero-length packet that ends it completes the same read.</summary>
    private readonly byte[] _reply = new byte[1024];

    private readonly Stopwatch _clock = new();
    private uint _lastTimestamp;
    private BgraJpegEncoder? _jpeg;
    private byte[] _portrait = Array.Empty<byte>();

    public string HandlerId => Id;
    public string Name => "Lian Li HydroShift II OLED Curved";
    public int VendorId => 0x1CBE;
    public IReadOnlyList<int> ProductIds { get; } = new[] { HydroShift2CurveProtocol.GlassProductId };
    public string Surface => Models.Panel.PanelSurfaces.LcdWide;
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

        // L-Connect's apply sequence. StopClock takes down the firmware's own coolant/pump
        // screen; until it goes, pushed frames are acknowledged and never shown.
        Command(pipe, HydroShift2Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, HydroShift2CurveProtocol.CommandWarnSwitch, stackalloc byte[] { 0 });
        Command(pipe, HydroShift2CurveProtocol.CommandHideShow, new byte[32]);
        Exchange(pipe, HydroShift2Protocol.CommandSetClock, HydroShift2Protocol.EncodeSetClock(DateTime.Now, NextTimestamp()));
        Command(pipe, HydroShift2Protocol.CommandStopClock, stackalloc byte[] { 0 });
        Command(pipe, HydroShift2CurveProtocol.CommandClearPng, ReadOnlySpan<byte>.Empty);
        Command(pipe, HydroShift2CurveProtocol.CommandClearPng, ReadOnlySpan<byte>.Empty);

        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(HydroShift2CurveProtocol.Height, HydroShift2CurveProtocol.Width, JpegQuality);
        _portrait = new byte[HydroShift2CurveProtocol.Width * HydroShift2CurveProtocol.Height * 4];
        ServiceLog.Info($"[{HandlerId}] firmware {Firmware ?? "unknown"}");
        return (HydroShift2CurveProtocol.Width, HydroShift2CurveProtocol.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (_jpeg is null)
        {
            return false;
        }
        BgraQuarterTurn.RotateCw(bgra, HydroShift2CurveProtocol.Width, HydroShift2CurveProtocol.Height, _portrait);
        var packet = HydroShift2Protocol.EncodeImage(
            HydroShift2Protocol.CommandPushJpeg, _jpeg.Encode(_portrait), NextTimestamp());
        if (!pipe.Write(packet))
        {
            return false;
        }
        ReadReply(pipe, HydroShift2Protocol.CommandPushJpeg);
        return true;
    }

    /// <summary>The glass takes half the percent (L-Connect sends 50 for 100%).</summary>
    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) =>
        Command(pipe, HydroShift2Protocol.CommandBrightness, stackalloc byte[] { (byte)(Math.Clamp(percent, 0, 100) / 2) });

    /// <summary>The glass keeps the last frame; nothing restores the firmware's own screen short of a power cycle.</summary>
    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        pipe.Write(HydroShift2Protocol.EncodeCommand(
            HydroShift2Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        _jpeg?.Dispose();
        _jpeg = null;
    }

    private bool Command(IBulkUsbPipe pipe, byte command, ReadOnlySpan<byte> parameters) =>
        Exchange(pipe, command, HydroShift2Protocol.EncodeCommand(command, parameters, NextTimestamp())) is not null;

    /// <summary>Writes one command and returns its reply, or null when the write failed or nothing answered.</summary>
    private byte[]? Exchange(IBulkUsbPipe pipe, byte command, byte[] packet) =>
        pipe.Write(packet) ? ReadReply(pipe, command) : null;

    /// <summary>The reply to <paramref name="command"/>, skipping stale replies from commands that timed out.</summary>
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
