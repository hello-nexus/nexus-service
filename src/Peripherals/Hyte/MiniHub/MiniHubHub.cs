using System;
using System.Threading;
using Qos.Service.Peripherals.Hyte.Np50;

namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Singleton coordinator for a HYTE IBP MiniHub. Mirrors <see cref="Np50Hub"/>
/// for the MiniHub product: opens the COM port lazily, exposes a state
/// snapshot, and lets capability classes push LED frames. v1 ships
/// lighting-only — fan poll/control is a follow-up.
/// </summary>
public sealed class MiniHubHub : IDisposable
{
    /// <summary>
    /// Single source of truth for this device's user-facing product label.
    /// Both the lighting and cooling providers reference this so the panel
    /// shows the SAME name on every page that surfaces this device.
    ///
    /// The MiniHub firmware does not self-report a name; the canonical
    /// value comes from HYTE's own product taxonomy. HYTE's reference
    /// <c>UniversalHardwareInfo</c> entry for this VID/PID
    /// (VID_3402&amp;PID_0900) is <c>"iBUYPOWER Mini Hub"</c>; we use the
    /// no-space spelling <c>"iBUYPOWER MiniHub"</c> per the user-facing
    /// branding chosen for the qos panel.
    /// </summary>
    public const string ProductName = "iBUYPOWER MiniHub";

    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public MiniHubHub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public MiniHubState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"minihub:{State.Serial}";

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            // One-line trace so we can tell "no device" apart from "device
            // found but port open failed" — both manifest as the hub
            // silently staying disconnected. Logged at most every heartbeat
            // tick which is fine.
            Console.Error.WriteLine($"[minihub] discovery returned {ports.Count} port(s)");
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    Console.Error.WriteLine($"[minihub] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[minihub] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.DiscardInput();
            transport.Write(MiniHubProtocol.BuildGetFirmwareVersion());
            var buf = new byte[7];
            var n = transport.Read(buf, 300);
            if (n < 7) { Disconnect(); return false; }
            var v = MiniHubProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minihub] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public bool SetRgbControlMode(byte mode) => SendOnly(MiniHubProtocol.BuildSetRgbControlMode(mode));

    public bool WriteLighting(int channel, ReadOnlySpan<RgbColor> leds)
        => SendOnly(MiniHubProtocol.BuildLightingStream(channel, leds));

    public bool SetFanControlMode(byte mode) => SendOnly(MiniHubProtocol.BuildSetFanControlMode(mode));

    /// <summary>
    /// Push a single port-pair PWM update to the hub. Both ports are written
    /// in one frame (the protocol has no per-port command); pass the
    /// previously-applied value for whichever port the caller doesn't want
    /// to change. Stores the commanded values on <see cref="State"/> so the
    /// cooling provider can read them back without a round-trip.
    /// </summary>
    public bool WriteFanSpeed(int port1Percent, int port2Percent)
    {
        var ok = SendOnly(MiniHubProtocol.BuildSetFanSpeed(port1Percent, port2Percent));
        if (ok)
        {
            State.Port1Duty = Math.Clamp(port1Percent, MiniHubProtocol.FanMinDutyPercent, MiniHubProtocol.FanMaxDutyPercent);
            State.Port2Duty = Math.Clamp(port2Percent, MiniHubProtocol.FanMinDutyPercent, MiniHubProtocol.FanMaxDutyPercent);
        }
        return ok;
    }

    /// <summary>
    /// Query both port tach readings and stash them on <see cref="State"/>.
    /// Returns false on transport hiccup or malformed reply so the heartbeat
    /// can drop the transport and re-discover the port. The 300 ms read
    /// budget matches <see cref="PollFirmwareVersion"/>.
    /// </summary>
    public bool PollFanSpeeds()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.DiscardInput();
            transport.Write(MiniHubProtocol.BuildGetFanSpeed());
            var buf = new byte[MiniHubProtocol.GetFanSpeedResponseLength];
            var n = transport.Read(buf, 300);
            if (n < MiniHubProtocol.GetFanSpeedResponseLength) { Disconnect(); return false; }
            if (!MiniHubProtocol.TryParseFanSpeeds(buf.AsSpan(0, n), out var rpm1, out var rpm2)) return false;
            State.Port1Rpm = rpm1;
            State.Port2Rpm = rpm2;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minihub] get-fan-speed exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
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
            return false;
        }
        catch (Exception ex)
        {
            // See Np50Hub.SendOnly for the rationale: a single transient
            // write hiccup at 30 Hz used to tear down the shared transport
            // (which also affects the other hub on the same USB bus) and
            // surface as visible flicker. Soften to "log and retry" until
            // a sustained burst makes it clear the port is actually dead.
            var n = Interlocked.Increment(ref _consecutiveWriteFailures);
            Console.Error.WriteLine($"[minihub] write failed (#{n}): {ex.GetType().Name}: {ex.Message}");
            if (n >= ConsecutiveWriteFailureThreshold)
            {
                Console.Error.WriteLine($"[minihub] {n} consecutive write failures — dropping transport so next tick rediscovers");
                _consecutiveWriteFailures = 0;
                Disconnect();
            }
            return false;
        }
    }

    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;
}
