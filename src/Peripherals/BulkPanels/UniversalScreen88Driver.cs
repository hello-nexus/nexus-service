using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8 glass, run as a full widget panel: 1920x480 when mounted
/// landscape, 480x1920 portrait. Its 60-LED bezel is a separate USB function (0416:8050) that
/// the bundled OpenRGB drives.
/// </summary>
public sealed class UniversalScreen88Driver : IBulkPanelDriver
{
    public const string Id = "lianli-screen88";

    private const int ReplyTimeoutMs = 1000;

    /// <summary>The panel render rate; the glass acks frames faster than this (measured), so the link is never the ceiling.</summary>
    private const int FrameRate = 30;
    private const int JpegQuality = 85;

    /// <summary>Larger than one 512-byte reply, so the zero-length packet that ends it completes the same read.</summary>
    private readonly byte[] _reply = new byte[1024];

    private readonly Stopwatch _clock = new();
    private uint _lastTimestamp;
    private BgraJpegEncoder? _jpeg;
    private byte[] _native = Array.Empty<byte>();
    private Func<bool> _wantsPortrait = () => false;
    private volatile bool _portrait;
    private volatile bool _retired;
    private volatile bool _sessionUp;

    public string HandlerId => Id;
    public string Name => "Lian Li Universal Screen 8.8";
    public int VendorId => 0x1CBE;
    public IReadOnlyList<int> ProductIds { get; } = new[] { UniversalScreen88Protocol.ProductId };

    /// <summary>A 4:1 strip carries a page of widgets like a promoted monitor, not one cooler tile.</summary>
    public string Surface => Models.Panel.PanelSurfaces.Monitor;

    public int Fps => FrameRate;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x81;
    public bool NeedsHidChannel => false;
    public bool SupportsBrightness => true;
    public bool SupportsSecondaryMonitor => true;
    public bool SupportsPortrait => true;

    /// <summary>The glass's real density (1920x480 on an 8.8 in diagonal); the monitor surface's desk-monitor estimate doubles the grid's rows.</summary>
    public double? Dpi => 225;

    /// <summary>Firmware string read at connect, or null before it.</summary>
    public string? Firmware { get; private set; }

    /// <summary>The mounting the session must run at differs from the one it negotiated.</summary>
    public bool GeometryStale => _sessionUp && _wantsPortrait() != _portrait;

    /// <summary>Reads the panel record's portrait setting at each handshake.</summary>
    public void BindPortrait(Func<bool> source) => _wantsPortrait = source;

    /// <summary>Set at shutdown once the glass is let go: no reconnect may take it back.</summary>
    public void Retire() => _retired = true;

    /// <summary>Milliseconds since connect, clamped to strictly increase: the firmware drops a repeated stamp.</summary>
    private uint NextTimestamp()
    {
        var now = (uint)_clock.ElapsedMilliseconds;
        _lastTimestamp = now > _lastTimestamp ? now : _lastTimestamp + 1;
        return _lastTimestamp;
    }

    /// <summary>
    /// L-Connect's apply sequence. StopClock takes down the firmware's own screen, and the
    /// empty PNG clears an overlay layer another host left composited over every JPEG.
    /// Re-run on an open pipe when only the mounting changed.
    /// </summary>
    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _sessionUp = false;
        if (_retired)
        {
            return null;
        }
        _clock.Restart();
        _lastTimestamp = 0;

        var version = Exchange(pipe, UniversalScreen88Protocol.CommandGetVersion, UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandGetVersion, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        if (version is null)
        {
            ServiceLog.Warn($"[{HandlerId}] no reply to GetVersion");
            return null;
        }
        Firmware = UniversalScreen88Protocol.DecodeVersion(version);

        var portrait = _wantsPortrait();
        Command(pipe, UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty);
        Command(pipe, UniversalScreen88Protocol.CommandRotate, stackalloc byte[]
        {
            portrait ? UniversalScreen88Protocol.RotatePortrait : UniversalScreen88Protocol.RotateLandscape,
        });
        Exchange(pipe, UniversalScreen88Protocol.CommandSetClock,
            UniversalScreen88Protocol.EncodeSyncClock(DateTime.Now, NextTimestamp()));
        Command(pipe, UniversalScreen88Protocol.CommandStopClock, stackalloc byte[] { 0 });
        Exchange(pipe, UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EncodeImage(
            UniversalScreen88Protocol.CommandPushPng, UniversalScreen88Protocol.EmptyOverlayPng(), NextTimestamp()));

        _jpeg?.Dispose();
        _jpeg = new BgraJpegEncoder(UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height, JpegQuality);
        _native = portrait ? Array.Empty<byte>() : new byte[UniversalScreen88Protocol.Width * UniversalScreen88Protocol.Height * 4];
        _portrait = portrait;
        ServiceLog.Info($"[{HandlerId}] firmware {Firmware ?? "unknown"}, {(portrait ? "portrait" : "landscape")}");
        _sessionUp = true;
        return portrait
            ? (UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height)
            : (UniversalScreen88Protocol.Height, UniversalScreen88Protocol.Width);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (_jpeg is null)
        {
            return false;
        }
        var native = bgra;
        if (!_portrait)
        {
            BgraQuarterTurn.RotateCw(bgra, UniversalScreen88Protocol.Height, UniversalScreen88Protocol.Width, _native);
            native = _native;
        }
        var packet = UniversalScreen88Protocol.EncodeImage(
            UniversalScreen88Protocol.CommandPushJpeg, _jpeg.Encode(native), NextTimestamp());
        if (!pipe.Write(packet))
        {
            return false;
        }
        ReadReply(pipe, UniversalScreen88Protocol.CommandPushJpeg);
        return true;
    }

    /// <summary>The glass takes half the percent (L-Connect sends 50 for 100%).</summary>
    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) =>
        Command(pipe, UniversalScreen88Protocol.CommandBrightness, stackalloc byte[] { (byte)(Math.Clamp(percent, 0, 100) / 2) });

    /// <summary>
    /// Hands the glass back to its own boot screen, which only a reboot brings back; it would
    /// otherwise hold the last frame, stale readings and all. A handshake that never completed
    /// sends no reboot, so a glass still coming back from one is not sent round again.
    /// </summary>
    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (_retired && !_sessionUp)
        {
            return;
        }
        pipe.Write(UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        if (_sessionUp)
        {
            pipe.Write(UniversalScreen88Protocol.EncodeCommand(
                UniversalScreen88Protocol.CommandReboot, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        }
        _sessionUp = false;
        _jpeg?.Dispose();
        _jpeg = null;
    }

    private bool Command(IBulkUsbPipe pipe, byte command, ReadOnlySpan<byte> parameters) =>
        Exchange(pipe, command, UniversalScreen88Protocol.EncodeCommand(command, parameters, NextTimestamp())) is not null;

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
