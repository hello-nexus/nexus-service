using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Singleton coordinator for an NP50 hub. Owns the open transport, the
/// shared <see cref="Np50State"/> snapshot, and the high-level read/write
/// operations the cooling provider, lighting capability, and REST routes
/// call into. Polling is driven by <see cref="Np50HeartbeatWorker"/>; this
/// class itself has no timers.
///
/// Hot-plug is self-healing: each <see cref="EnsureConnected"/> attempt
/// re-runs port discovery, so plugging in or unplugging the hub just shows
/// up on the next heartbeat tick.
/// </summary>
public sealed class Np50Hub : IDisposable
{
    /// <summary>
    /// Single source of truth for this device's user-facing product label.
    /// Both the lighting and cooling providers reference this so the panel
    /// shows the SAME name on every page that surfaces this device.
    /// </summary>
    public const string ProductName = "HYTE NP50";

    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public Np50Hub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    /// <summary>Latest state snapshot. Reads are safe without a lock (POCO; eventual consistency is fine for UI).</summary>
    public Np50State State { get; } = new();

    /// <summary>
    /// Desired hub cooling mode. When set, the heartbeat re-asserts it on
    /// every tick if the hub's reported mode drifts. Lets us recover from
    /// a single mode-switch command being lost (firmware 2.0.3.1 sometimes
    /// needs the command twice to actually flip) and from the hub reverting
    /// after a brief heartbeat lapse. Null = "don't care", leave the hub in
    /// whatever state the firmware default puts it in.
    /// </summary>
    public byte? DesiredCoolingMode { get; private set; }

    /// <summary>Set the persistent desired cooling mode. Heartbeat enforces it.</summary>
    public void SetDesiredCoolingMode(byte? mode)
    {
        DesiredCoolingMode = mode;
        if (mode is byte m) SetCoolingMode(m);
    }

    /// <summary>True iff the hub is currently open and reachable.</summary>
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"np50:&lt;serial&gt;" device id, or empty when never connected.</summary>
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"np50:{State.Serial}";

    /// <summary>
    /// Try to open the first NP50 port we can find. No-op if already connected.
    /// Returns true iff a transport is open after the call.
    /// </summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            foreach (var port in _discovery.Discover())
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    Console.Error.WriteLine($"[np50] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    // Port enumeration found something but open failed (in use,
                    // permission denied, etc). Move on; next tick tries again.
                    Console.Error.WriteLine($"[np50] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>Drop the transport (used when a read/write fails so the next tick re-discovers).</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    // ── Polling ──

    /// <summary>Send the heartbeat ("Get NP50 Info") and parse the response into state.</summary>
    public bool PollHubInfo()
    {
        return Exchange(
            Np50Protocol.BuildGetInfo(),
            expectedLength: 20,
            timeoutMs: 250,
            response => Np50Protocol.ParseHubInfo(response, State.HubInfo));
    }

    /// <summary>Poll one port's connected fan list.</summary>
    public bool PollPort(int port)
    {
        // 240 bytes per spec, but the device sometimes sends slightly less when
        // fewer than 19 slots are populated. Read up to the full size and let
        // ParseChannelInfo stop at the first empty slot.
        var p = State.Ports.FirstOrDefault(x => x.Index == port);
        if (p is null) return false;
        return Exchange(
            Np50Protocol.BuildGetChannelInfo(port),
            expectedLength: 240,
            timeoutMs: 400,
            response =>
            {
                // Temporary diagnostic: dump the raw response so we can verify
                // the on-wire byte layout against the spec. Remove once the
                // parser is known correct against real hardware.
                if (_logRawPort && port == _logRawPort_PortIndex)
                {
                    _logRawPort = false;
                    var hex = new System.Text.StringBuilder(response.Length * 3);
                    for (var i = 0; i < response.Length; i++)
                    {
                        if (i > 0 && i % 12 == 0) hex.Append('|');
                        hex.Append(response[i].ToString("X2")).Append(' ');
                    }
                    Console.Error.WriteLine($"[np50] port{port} raw ({response.Length}B): {hex}");
                }
                Np50Protocol.ParseChannelInfo(response, p);
            });
    }

    // One-shot raw dump trigger. Set via DumpNextPortResponse() then cleared
    // after the next poll fires. Lets routes / debugging code capture the
    // bytes without filling the log on every tick.
    private bool _logRawPort;
    private int _logRawPort_PortIndex = 1;

    /// <summary>Capture the next channel-info response for <paramref name="port"/> into the service log as hex.</summary>
    public void DumpNextPortResponse(int port)
    {
        _logRawPort_PortIndex = port;
        _logRawPort = true;
    }

    public bool PollWarningDetail()
    {
        return Exchange(
            Np50Protocol.BuildGetWarningDetail(),
            expectedLength: 6,
            timeoutMs: 200,
            response => Np50Protocol.ParseWarningDetail(response, State.Warnings));
    }

    public bool PollFirmwareVersion()
    {
        return Exchange(
            Np50Protocol.BuildGetFirmwareVersion(),
            expectedLength: 7,
            timeoutMs: 300,
            response =>
            {
                var v = Np50Protocol.ParseFirmwareVersion(response);
                if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            });
    }

    // ── Writes ──

    public bool SetCoolingMode(byte mode)
    {
        // v2 mode-switch wants every parameter filled — pass through the
        // current firmware-animation state so changing cooling mode doesn't
        // accidentally clobber the user's LED setup.
        var hi = State.HubInfo;
        return SendOnly(Np50Protocol.BuildSetCoolingMode(
            mode,
            staticSpeedPercent: 50,
            turboOff: true,
            fwAnimation: hi.FirmwareAnimation,
            fwR: hi.FirmwareAnimR, fwG: hi.FirmwareAnimG, fwB: hi.FirmwareAnimB,
            fwBrightness: hi.FirmwareAnimBrightness == 0 ? (byte)100 : hi.FirmwareAnimBrightness));
    }

    public bool SetLegacyFanSpeed(int percent)
    {
        // Spec: hub must be in software mode for the legacy 4-pin write to take.
        // Don't re-send mode every call; assume the heartbeat / curve engine
        // owns mode state. Callers that need a mode change call SetCoolingMode first.
        return SendOnly(Np50Protocol.BuildSetLegacyFanSpeed(percent));
    }

    public bool SetPortFanSpeeds(int port, IReadOnlyList<int> perFanPercent)
        => SendOnly(Np50Protocol.BuildSetPortFanSpeeds(port, perFanPercent));

    /// <summary>
    /// Disable the firmware's boot-up rainbow animation. Pair with
    /// <see cref="SetFirmwareLightingOff"/> when entering software lighting
    /// mode so the hub's defaults don't bleed through our software stream.
    /// </summary>
    public bool SetStartAnimationOff(bool off)
        => SendOnly(Np50Protocol.BuildSetStartAnimationOff(off));

    /// <summary>
    /// Disable the firmware's steady-state default animation. MUST be sent
    /// when running in software lighting mode — without it, any LED our
    /// wire frame doesn't address (e.g. the first LED of each port's
    /// daisy-chain on some firmware revs) keeps cycling the firmware
    /// rainbow on top of the software stream.
    /// </summary>
    public bool SetFirmwareLightingOff(bool off)
        => SendOnly(Np50Protocol.BuildSetFirmwareLightingOff(off));

    /// <summary>
    /// Push the firmware-animation state directly to the MCU. Pair with
    /// <see cref="SetFirmwareLightingOff"/>: the EEPROM-saved off flag
    /// (0x07) doesn't appear to clear an animation already running on
    /// the live MCU — only this 0x0C direct-write does. Call with
    /// <c>animation=0, r=g=b=0, brightness=0</c> to fully silence the
    /// firmware default animation while we stream software LED frames.
    /// </summary>
    public bool WriteFirmwareAnimationToMcu(byte animation, byte r, byte g, byte b, byte brightness)
        => SendOnly(Np50Protocol.BuildWriteFirmwareAnimationToMcu(animation, r, g, b, brightness));

    /// <summary>
    /// Read the 17-byte EEPROM-persisted firmware default-mode block (opcode
    /// 0xCC 0x04). Use this before issuing any SAVE-byte write to default-mode
    /// fields to verify what's actually there — `SendOnly` returning true only
    /// means the bytes left the wire, not that the firmware accepted them.
    /// See `.agents/rules/failure-log.md` 2026-05-21 EEPROM read-before-write.
    /// </summary>
    public byte[]? GetFirmwareDefaultModeRaw()
    {
        byte[]? result = null;
        var ok = Exchange(
            Np50Protocol.BuildGetFirmwareDefaultMode(),
            expectedLength: 17,
            timeoutMs: 300,
            response => result = response.ToArray());
        return ok ? result : null;
    }

    /// <summary>
    /// Read raw 9-byte firmware-animation state (opcode 0xCC 0x0D). Payload
    /// layout after the 4-byte header: anim, R, G, B, brightness. Pair with
    /// <see cref="WriteFirmwareAnimationToMcu"/> to verify SAVE-byte writes
    /// actually persisted.
    /// </summary>
    public byte[]? GetFirmwareAnimationRaw()
    {
        byte[]? result = null;
        var ok = Exchange(
            Np50Protocol.BuildGetFirmwareAnimation(),
            expectedLength: 9,
            timeoutMs: 300,
            response => result = response.ToArray());
        return ok ? result : null;
    }

    public bool WriteLighting(int port, ReadOnlySpan<RgbColor> leds)
        => SendOnly(Np50Protocol.BuildLightingStream(port, leds));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    /// <summary>
    /// Run a request/response cycle. Disconnects on IO failure so the next
    /// heartbeat re-discovers; returns false on any error rather than throwing
    /// so the worker loop stays tick-clean.
    /// </summary>
    private bool Exchange(byte[] request, int expectedLength, int timeoutMs, Action<ReadOnlySpan<byte>> parse)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            // Drain stale bytes from any prior short read so the response we're
            // about to issue is parsed off the right offset. The hub sometimes
            // sends slightly more than the documented payload (firmware-side
            // FW-animation block in newer FW), and a leftover byte misaligns
            // the next 12-byte-slot parse to nonsense.
            transport.DiscardInput();
            transport.Write(request);
            var buf = new byte[expectedLength];
            var n = transport.Read(buf, timeoutMs);
            if (n < 2)
            {
                Console.Error.WriteLine($"[np50] short read ({n} bytes); dropping connection");
                Disconnect();
                return false;
            }
            parse(buf.AsSpan(0, n));
            State.LastPollMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[np50] exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private bool SendOnly(byte[] request)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Port was torn down by Dispose/Disconnect from another thread.
            // Don't double-disconnect; just report the write failed.
            return false;
        }
        catch (Exception ex)
        {
            // A single transient write hiccup (USB scheduling jitter, brief
            // buffer pressure) used to call Disconnect() — which then
            // dropped the entire transport for ~2 s until the next heartbeat
            // rediscovered, surfacing as a visible RGB stutter on every
            // strip. HYTE's reference (SmartHubCommandBase.Write) just logs
            // and lets the next frame retry; only escalate to a real
            // Disconnect after a sustained burst of failures.
            var n = Interlocked.Increment(ref _consecutiveWriteFailures);
            Console.Error.WriteLine($"[np50] write failed (#{n}): {ex.GetType().Name}: {ex.Message}");
            if (n >= ConsecutiveWriteFailureThreshold)
            {
                Console.Error.WriteLine($"[np50] {n} consecutive write failures — dropping transport so next tick rediscovers");
                _consecutiveWriteFailures = 0;
                Disconnect();
            }
            return false;
        }
    }

    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;
}
