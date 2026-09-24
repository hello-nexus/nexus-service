using System;
using System.Threading;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Singleton coordinator for a HYTE IBP MiniHub. Mirrors <see cref="Np50Hub"/>
/// for the MiniHub product: opens the COM port lazily, exposes a state
/// snapshot, and lets capability classes push LED frames + fan writes.
/// </summary>
public sealed class MiniHubHub : IDisposable, IDfuFlashTarget
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
    /// branding chosen for the nexus panel.
    /// </summary>
    public const string ProductName = "iBUYPOWER MiniHub";

    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;
    // Starts at 0 (the silent default) so a device absent from boot never logs
    // "discovery returned 0"; only a real change (0->N found, or N->0 disconnect) logs.
    // -1 so the first attempt logs even when it finds nothing: a silent zero is
    // indistinguishable from the worker never running.
    private int _lastDiscoveredPortCount = -1;

    public MiniHubHub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public MiniHubState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"minihub:{State.Serial}";

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? "fan-hub" : "";
    bool IDfuFlashTarget.CanFlash(string firmwareType) => firmwareType == "fan-hub";

    /// <summary>Drop the MiniHub into DFU: write the OTA key + magic over the serial port, then release it for dfu-util.</summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        var t = _transport;
        if (t is null) return false;
        var verify = OtaDfuEntry.SupportsPidCheck("fan-hub", State.FirmwareVersion);
        bool ok;
        try { ok = OtaDfuEntry.Enter(t, OtaProductKey.ForProductId(MiniHubProtocol.ProductId), verify); }
        catch { ok = true; /* port drops as the device reboots into DFU */ }
        Disconnect();
        return ok;
    }

    /// <summary>
    /// User-pinned fan-control mode. Null = unpinned (cooling provider
    /// owns it). When set to <see cref="MiniHubProtocol.FanModeMotherboard"/>,
    /// the cooling provider must NOT re-assert Software on subsequent fan
    /// writes - otherwise the next curve tick clobbers the user's BIOS
    /// pick before the hub has even reported the mode change back.
    /// </summary>
    public byte? DesiredFanControlMode { get; private set; }

    /// <summary>
    /// Pin a fan-control mode and immediately push it to the hub. Pass null
    /// to clear the pin (lets the provider go back to managing mode per
    /// fan write).
    /// </summary>
    public void SetDesiredFanControlMode(byte? mode)
    {
        DesiredFanControlMode = mode;
        if (mode is byte m) SetFanControlMode(m);
    }

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            // Trace to tell "no device" apart from "device found but port open
            // failed"; both manifest as the hub staying disconnected. Logged only
            // when the count changes so a present-but-unopenable port can't flood.
            if (ports.Count != _lastDiscoveredPortCount)
            {
                _lastDiscoveredPortCount = ports.Count;
                ServiceLog.Info($"[minihub] discovery returned {ports.Count} port(s)");
            }
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    ServiceLog.Info($"[minihub] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[minihub] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
        _port1Tach.Reset();
        _port2Tach.Reset();
        State.Port1RpmValid = false;
        State.Port1Rpm = 0;
        State.Port2RpmValid = false;
        State.Port2Rpm = 0;
    }

    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            if (!SendRequest(transport, "fw-version", MiniHubProtocol.BuildGetFirmwareVersion())) return false;
            var buf = new byte[7];
            var n = transport.Read(buf, 300);
            if (n < 7) return FailPoll("fw-version", $"short read ({n} bytes)");
            var v = MiniHubProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            _pollFailures.Reset();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            return FailPoll("fw-version", $"{ex.GetType().Name}: {ex.Message}");
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
    /// Query both port tach readings, feed them through
    /// <see cref="MiniHubTachConsensus"/> and stash the agreed values on
    /// <see cref="State"/> (see <see cref="MiniHubProtocol.TryParseFanSpeeds"/>
    /// for why a single poll cannot be trusted).
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
            if (!SendRequest(transport, "fan-speed", MiniHubProtocol.BuildGetFanSpeed())) return false;
            var buf = new byte[MiniHubProtocol.GetFanSpeedResponseLength];
            var n = transport.Read(buf, 300);
            if (n < MiniHubProtocol.GetFanSpeedResponseLength) return FailPoll("fan-speed", $"short read ({n} bytes)");
            if (!MiniHubProtocol.TryParseFanSpeeds(buf.AsSpan(0, n), out var rpm1, out var rpm2)) return false;
            State.Port1RawRpm = rpm1;
            State.Port2RawRpm = rpm2;
            _port1Tach.Add(rpm1);
            _port2Tach.Add(rpm2);
            // Valid drops before Rpm moves and rises after, so a concurrent
            // GetFanChannels never pairs a stale flag with a fresh value.
            var agreed1 = _port1Tach.Evaluate();
            var agreed2 = _port2Tach.Evaluate();
            State.Port1RpmValid = false;
            State.Port1Rpm = agreed1 ?? 0;
            State.Port1RpmValid = agreed1 is not null;
            State.Port2RpmValid = false;
            State.Port2Rpm = agreed2 ?? 0;
            State.Port2RpmValid = agreed2 is not null;
            _pollFailures.Reset();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            return FailPoll("fan-speed", $"{ex.GetType().Name}: {ex.Message}");
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
            // See Np50Hub.SendOnly. Log and retry on a single write hiccup;
            // only drop the transport after a sustained burst. Tearing it down
            // on one hiccup at 30 Hz also kills the other hub on the same USB
            // bus and surfaces as visible flicker.
            var n = Interlocked.Increment(ref _consecutiveWriteFailures);
            Console.Error.WriteLine($"[minihub] write failed (#{n}): {ex.GetType().Name}: {ex.Message}");
            if (n >= ConsecutiveWriteFailureThreshold)
            {
                Console.Error.WriteLine($"[minihub] {n} consecutive write failures - dropping transport so next tick rediscovers");
                _consecutiveWriteFailures = 0;
                Disconnect();
            }
            return false;
        }
    }


    /// <summary>
    /// Issue a poll request. A write that throws never reached the hub, so a
    /// retry would spend the software-control budget on a port that is not
    /// carrying our frames; drop it now.
    /// </summary>
    private bool SendRequest(INp50Transport transport, string operation, byte[] request)
    {
        try
        {
            transport.DiscardInput();
            transport.Write(request);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[minihub] {operation} request failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private bool FailPoll(string operation, string detail)
    {
        if (_pollFailures.ShouldDisconnect(operation, detail))
        {
            Disconnect();
        }
        return false;
    }

    private readonly PollFailureTracker _pollFailures = new("minihub");
    private readonly MiniHubTachConsensus _port1Tach = new();
    private readonly MiniHubTachConsensus _port2Tach = new();
    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;
}
