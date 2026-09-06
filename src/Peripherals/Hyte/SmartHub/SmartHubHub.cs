using System;
using System.Threading;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Singleton coordinator for a HYTE SmartHub. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub"/>: opens the
/// COM port lazily, exposes a <see cref="SmartHubState"/> snapshot, and lets
/// the lighting + cooling capability classes push LED frames / fan speeds.
///
/// It reuses the product-agnostic NP50 serial transport + port-discovery
/// abstraction (<see cref="INp50Transport"/> / <see cref="INp50PortDiscovery"/>)
/// - the SmartHub is just another HYTE serial-over-USB hub, so there's no
/// reason to duplicate the serial plumbing.
/// </summary>
public sealed class SmartHubHub : IDisposable, IDfuFlashTarget
{
    /// <summary>
    /// Single source of truth for this device's user-facing product label.
    /// Both the lighting and cooling providers reference this so the panel
    /// shows the SAME name everywhere. (HYTE's legacy taxonomy for
    /// VID_3402&amp;PID_0904 spells it "HYTE Smart Hub"; we brand it one word,
    /// matching "HYTE NP50".)
    /// </summary>
    public const string ProductName = "HYTE SmartHub";

    /// <summary>Device id + firmware-catalog key (the bundled <c>smarthub/</c> image directory).</summary>
    public const string DeviceType = "smarthub";

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

    // Serializes request/response exchanges. The heartbeat used to be the
    // only reader; the fw-setting REST route added a second exchanging thread,
    // and an interleaved DiscardInput/Write/Read pair would eat or misalign
    // the other's reply. Write-only frames (duty, LEDs) stay outside it.
    private readonly object _exchangeLock = new();

    private readonly PollFailureTracker _pollFailures = new("smarthub");
    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;

    public SmartHubHub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public SmartHubState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"smarthub:{State.Serial}";

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? DeviceType : "";
    bool IDfuFlashTarget.CanFlash(string firmwareType) => firmwareType == DeviceType;

    /// <summary>
    /// Drop the SmartHub into DFU: write the OTA key + magic over the serial
    /// port, then release it for dfu-util. The key encodes
    /// <see cref="SmartHubProtocol.OtaProductId"/> (NP50's 0x0901 - the legacy
    /// ControlHub factory entry ships NP50's key, not PID 0x0904).
    /// </summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        var t = _transport;
        if (t is null) return false;
        var verify = OtaDfuEntry.SupportsPidCheck(DeviceType, State.FirmwareVersion);
        bool ok;
        try { ok = OtaDfuEntry.Enter(t, OtaProductKey.ForProductId(SmartHubProtocol.OtaProductId), verify); }
        catch { ok = true; /* port drops as the device reboots into DFU */ }
        Disconnect();
        return ok;
    }

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            if (ports.Count != _lastDiscoveredPortCount)
            {
                _lastDiscoveredPortCount = ports.Count;
                ServiceLog.Info($"[smarthub] discovery returned {ports.Count} port(s)");
            }
            foreach (var port in ports)
            {
                try
                {
                    _transport = _transportFactory(port);
                    State.Serial = port.Serial;
                    ServiceLog.Info($"[smarthub] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[smarthub] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            foreach (var fan in State.Fans) { fan.SeenFan = false; fan.HostDriven = false; }
        }
    }

    /// <summary>Read the firmware version once on connect. Returns false on transport hiccup so the heartbeat can re-discover.</summary>
    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            lock (_exchangeLock)
            {
                if (!SendRequest(transport, "fw-version", SmartHubProtocol.BuildGetFirmwareVersion())) return false;
                var buf = new byte[SmartHubProtocol.FirmwareVersionResponseLength];
                var n = transport.Read(buf, 300);
                if (n < SmartHubProtocol.FirmwareVersionResponseLength) return FailPoll("fw-version", $"short read ({n} bytes)");
                var v = SmartHubProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
                if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
                _pollFailures.Reset();
                return true;
            }
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

    /// <summary>
    /// Poll the 20-byte hub-info response and stash per-channel tach + enabled
    /// state on <see cref="State"/>. Returns false on transport hiccup or a
    /// malformed reply so the heartbeat can drop the transport and re-discover.
    /// </summary>
    public bool PollChannelInfo()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            lock (_exchangeLock)
            {
                if (!SendRequest(transport, "get-info", SmartHubProtocol.BuildGetInfo())) return false;
                var buf = new byte[SmartHubProtocol.GetInfoResponseLength];
                var n = transport.Read(buf, 300);
                if (n < SmartHubProtocol.GetInfoResponseLength) return FailPoll("get-info", $"short read ({n} bytes)");
                if (!SmartHubProtocol.TryParseChannelInfo(buf.AsSpan(0, n), out var channels) || channels is null)
                    return false;
                for (var i = 0; i < State.Fans.Length && i < channels.Length; i++)
                {
                    State.Fans[i].Rpm = channels[i].Rpm;
                    State.Fans[i].Enabled = channels[i].Enabled;
                    if (channels[i].Rpm > 0) State.Fans[i].SeenFan = true;
                }
                _pollFailures.Reset();
                return true;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            return FailPoll("get-info", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Read the flash-persisted standalone setting (LED animation + colour +
    /// brightness + the fan duty the watchdog holds when no host is driving).
    /// Returns false on transport hiccup or a malformed reply so the caller
    /// can surface a transient error and retry.
    /// </summary>
    public bool ReadMcuSetting(out SmartHubProtocol.SmartHubMcuSetting setting)
    {
        setting = default;
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            lock (_exchangeLock)
            {
                if (!SendRequest(transport, "fw-setting", SmartHubProtocol.BuildGetMcuSetting())) return false;
                var buf = new byte[SmartHubProtocol.McuSettingResponseLength];
                var n = transport.Read(buf, 300);
                if (n < SmartHubProtocol.McuSettingResponseLength) return FailPoll("fw-setting", $"short read ({n} bytes)");
                if (!SmartHubProtocol.TryParseMcuSetting(buf.AsSpan(0, n), out var parsed) || parsed is null)
                    return false;
                setting = parsed.Value;
                _pollFailures.Reset();
                return true;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            return FailPoll("fw-setting", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Persist the standalone setting (animation/colour/brightness/fan duty) to flash. See <see cref="SmartHubProtocol.BuildSetMcuSetting"/>.</summary>
    public bool WriteMcuSetting(int animation, byte r, byte g, byte b, int brightness, int fanPercent)
        => SendOnly(SmartHubProtocol.BuildSetMcuSetting(animation, r, g, b, brightness, fanPercent));

    /// <summary>Turn the hub's onboard LED animation on/off. Off ⇒ software streaming drives the ARGB ports.</summary>
    public bool SetFirmwareAnimation(bool on) => SendOnly(SmartHubProtocol.BuildSetFirmwareAnimation(on));

    /// <summary>Stream a rendered LED frame to one ARGB port (1..4).</summary>
    public bool WriteLighting(int port, ReadOnlySpan<RgbColor> leds)
        => SendOnly(SmartHubProtocol.BuildLightingStream(port, leds));

    /// <summary>
    /// Set one PWM-fan port's duty (0..100%). Records the commanded value on
    /// <see cref="State"/> so the cooling provider can read it back without a
    /// round-trip. <paramref name="enabled"/> gates the port output.
    /// </summary>
    public bool WriteFanSpeed(int channel, int dutyPercent, bool enabled = true)
    {
        if (channel < 0 || channel >= State.Fans.Length) return false;
        var ok = SendOnly(SmartHubProtocol.BuildSetFanSpeed(channel, dutyPercent, enabled));
        if (ok)
        {
            State.Fans[channel].Duty = Math.Clamp(dutyPercent, SmartHubProtocol.FanMinDutyPercent, SmartHubProtocol.FanMaxDutyPercent);
            State.Fans[channel].Enabled = enabled;
            State.Fans[channel].HostDriven = true;
        }
        return ok;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
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
            ServiceLog.Warn($"[smarthub] {operation} request failed: {ex.GetType().Name}: {ex.Message}");
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
            // Same rationale as MiniHubHub.SendOnly / Np50Hub.SendOnly: a
            // single transient write hiccup at 30 Hz shouldn't tear down the
            // shared transport (which could surface as visible flicker).
            // Soften to "log and retry" until a sustained burst makes it clear
            // the port is actually dead.
            var n = Interlocked.Increment(ref _consecutiveWriteFailures);
            Console.Error.WriteLine($"[smarthub] write failed (#{n}): {ex.GetType().Name}: {ex.Message}");
            if (n >= ConsecutiveWriteFailureThreshold)
            {
                Console.Error.WriteLine($"[smarthub] {n} consecutive write failures - dropping transport so next tick rediscovers");
                _consecutiveWriteFailures = 0;
                Disconnect();
            }
            return false;
        }
    }
}
