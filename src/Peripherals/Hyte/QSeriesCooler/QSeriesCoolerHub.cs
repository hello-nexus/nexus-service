using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;
// Q-series shares the MiniHub RGB triple; alias to avoid the Np50.RgbColor clash.
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Singleton coordinator for a HYTE Q-series (Q60 / Q80) cooler controller.
/// Mirrors <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub"/>:
/// opens the COM port lazily, polls the firmware version, exposes a state
/// snapshot, streams LED frames, and carries the "drop into DFU" handshake.
/// Reuses the product-agnostic <see cref="Np50SerialTransport"/>.
/// </summary>
public sealed class QSeriesCoolerHub : IDisposable, IDfuFlashTarget
{
    private readonly IQSeriesCoolerPortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;
    // Set once we've put the cooler into software RGB control; cleared on
    // disconnect so the next connection re-asserts it before streaming.
    private bool _rgbInSwControl;
    // The COM port currently held (empty when disconnected). Lets the composite
    // strip OpenRGB's zombie entry for the same port without fragile name matching.
    private string _portName = "";
    // Starts at 0 (the silent default) so a device absent from boot never logs
    // "discovery returned 0"; only a real change (0->N found, or N->0 disconnect) logs.
    // -1 so the first attempt logs even when it finds nothing: a silent zero is
    // indistinguishable from the worker never running.
    private int _lastDiscoveredPortCount = -1;
    // Last software-commanded pump duty, echoed when toggling turbo or switching
    // to software so the pump isn't reset by an unrelated fan-channel write.
    private int _lastPumpDuty = 50;
    // Last channel topology the INF log line reported, so a poll that finds no
    // model/LED-count change stays silent. Null until the first successful parse,
    // so an initially empty channel still logs "(none)" once.
    private IReadOnlyList<QSeriesLinkDevice>? _lastLoggedChannel1;
    private IReadOnlyList<QSeriesLinkDevice>? _lastLoggedChannel2;
    // The cooling-policy "pinned" mode (null until the user picks one). The
    // cooling provider reads it to decide whether to swallow engine duty writes
    // so they don't flip the shared pump+fan hub back to software. Reset to null
    // on process start, so a restored profile drives normally. Mirrors the NP50
    // hub's DesiredCoolingMode.
    private byte? _desiredControlMode;

    public QSeriesCoolerHub(IQSeriesCoolerPortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public QSeriesCoolerState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"q60" / "q80" once connected, else empty. Used as the firmware-catalog key.</summary>
    public string Variant => State.Variant;

    /// <summary>User-facing product name for the cooling / lighting pages ("HYTE Q60" / "HYTE Q80").</summary>
    public string ProductName => Variant == QSeriesCoolerProtocol.VariantQ80 ? "HYTE Q80" : "HYTE Q60";

    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"qseries:{State.Serial}";

    /// <summary>COM port currently held (e.g. "COM4"), empty when disconnected.</summary>
    public string PortName => _portName;

    /// <summary>True when the connected cooler's firmware supports the editable 5-point temperature curve.</summary>
    public bool SupportsFirmwareCurve =>
        IsConnected && QSeriesCoolerProtocol.SupportsFirmwareCurve(Variant, State.FirmwareVersion);

    /// <summary>True when the connected cooler's firmware supports the firmware-driven LED animation.</summary>
    public bool SupportsFirmwareAnimation =>
        IsConnected && QSeriesCoolerProtocol.SupportsFirmwareAnimation(Variant, State.FirmwareVersion);

    /// <summary>True when the connected cooler's firmware supports the firmware-animation brightness field.</summary>
    public bool SupportsFirmwareAnimationBrightness =>
        IsConnected && QSeriesCoolerProtocol.SupportsFirmwareAnimationBrightness(Variant, State.FirmwareVersion);

    /// <summary>
    /// LEDs the card addresses: the backlight panel followed by the logo diamond,
    /// so index 0..<see cref="QSeriesCoolerProtocol.BacklightLedCount"/>-1 is the
    /// panel and the remainder is the logo. Each half streams to its own port in
    /// <see cref="WriteLighting"/>.
    /// </summary>
    public const int LedCount = QSeriesCoolerProtocol.BacklightLedCount + QSeriesCoolerProtocol.LogoLedCount;

    /// <summary>
    /// Q-series needs no settings-first handshake (unlike CNVS), so streaming is gated only on
    /// the port being open. Software RGB control is asserted lazily in <see cref="WriteLighting"/>
    /// on the first frame after each (re)connect.
    /// </summary>
    public bool IsReadyForStreaming => IsConnected;

    /// <summary>
    /// Stream one frame of LED colors to the cooler. Mirrors the legacy
    /// PQSeriesDeviceBase.SendToHardware loop: assert software RGB control once per connection,
    /// then write all <see cref="QSeriesCoolerProtocol.LedPortCount"/> port frames. Ports carry
    /// what legacy HubRGBChannels puts on them - the backlight panel on
    /// <see cref="QSeriesCoolerProtocol.BacklightPort"/>, the logo on
    /// <see cref="QSeriesCoolerProtocol.LogoPort"/>, nothing on 1 and 2 - so a port that owns no
    /// LEDs gets a header-only padded frame rather than a copy of the panel. Colors are RGB here
    /// in wire order; <see cref="QSeriesCoolerProtocol.BuildLightingStream"/> emits GRB.
    /// </summary>
    public void WriteLighting(ReadOnlySpan<RgbColor> leds)
    {
        // Serialize the write sequence under _lock (re-entrant): the 30 Hz
        // frame writer and the 3 s heartbeat both touch the transport, and Disconnect
        // disposes it under the same lock. Without this a heartbeat-triggered Disconnect
        // could tear the port down mid-frame, and _rgbInSwControl could be read stale
        // across a reconnect. Mirrors CnvsHub's _writeLock discipline.
        lock (_lock)
        {
            if (!EnsureConnected()) return;
            var t = _transport;
            if (t is null) return;
            try
            {
                AssertSoftwareRgbControlLocked(t);
                var backlight = leds.Length >= QSeriesCoolerProtocol.BacklightLedCount
                    ? leds[..QSeriesCoolerProtocol.BacklightLedCount]
                    : leds;
                var logoAll = leds.Length > QSeriesCoolerProtocol.BacklightLedCount
                    ? leds[QSeriesCoolerProtocol.BacklightLedCount..]
                    : default;
                var logo = logoAll.Length > QSeriesCoolerProtocol.LogoLedCount
                    ? logoAll[..QSeriesCoolerProtocol.LogoLedCount]
                    : logoAll;
                // Only the panel/logo ports: 1 and 2 are Nexus Link channels now driven by
                // WriteLinkLighting, and re-writing them here with an empty slice every tick
                // would blank whatever that call just streamed.
                t.Write(QSeriesCoolerProtocol.BuildLightingStream(QSeriesCoolerProtocol.BacklightPort, backlight));
                t.Write(QSeriesCoolerProtocol.BuildLightingStream(QSeriesCoolerProtocol.LogoPort, logo));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] lighting write failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
            }
        }
    }

    /// <summary>
    /// Stream one frame to a Nexus Link port (1 or 2 - the same connectors channel-info
    /// addresses as channels 1/2). Shares the connection lock and software-control
    /// assertion with <see cref="WriteLighting"/> so the panel/logo and link ports never
    /// race for the RGB-mode handshake.
    /// </summary>
    public void WriteLinkLighting(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port != QSeriesCoolerProtocol.LinkChannel1 && port != QSeriesCoolerProtocol.FanChannel)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1 or 2.");
        lock (_lock)
        {
            if (!EnsureConnected()) return;
            var t = _transport;
            if (t is null) return;
            try
            {
                AssertSoftwareRgbControlLocked(t);
                t.Write(QSeriesCoolerProtocol.BuildLightingStream(port, leds));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] link lighting write failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
            }
        }
    }

    // Caller holds _lock. Asserts software RGB control once per connection; the LED
    // streams on every port are ignored by firmware still on the motherboard-ARGB default.
    private void AssertSoftwareRgbControlLocked(INp50Transport t)
    {
        if (_rgbInSwControl) return;
        t.Write(QSeriesCoolerProtocol.BuildSetRgbControlMode(QSeriesCoolerProtocol.RgbModeSoftware));
        _rgbInSwControl = true;
    }

    /// <summary>
    /// Restore the cooler's firmware settings to factory: turbo off, the default
    /// pump/fan curves, and the default LED animation. The curve and animation
    /// writes skip a value the device already holds; turbo has no such skip and
    /// re-persists to the MCU (FF CC 0A) on every call, so it is only written
    /// when Port-0 says it is on. Returns false if any attempted step failed;
    /// unsupported firmware skips that step rather than failing.
    /// </summary>
    public bool ResetFirmwareToDefaults()
    {
        if (!EnsureConnected()) return false;
        var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        var ok = !ReadPort0(port0) || !QSeriesCoolerProtocol.TurboOnOf(port0) || SetTurbo(false);
        if (SupportsFirmwareCurve)
            ok &= WriteFirmwareCurve(QSeriesCoolerProtocol.DefaultFirmwareCurve());
        if (SupportsFirmwareAnimation)
        {
            ok &= SetFirmwareAnimation(
                QSeriesCoolerProtocol.DefaultFwAnimation,
                QSeriesCoolerProtocol.DefaultFwR,
                QSeriesCoolerProtocol.DefaultFwG,
                QSeriesCoolerProtocol.DefaultFwB,
                QSeriesCoolerProtocol.DefaultFwBrightness);
        }
        return ok;
    }

    /// <summary>
    /// Release software RGB control, returning the LEDs to the power-on default
    /// the cooler's own firmware animation runs under. Software control is what
    /// suppresses that animation, so releasing it is the only way a frame-less state shows
    /// anything but the last pushed frame. Clears the asserted flag, so the next
    /// <see cref="WriteLighting"/> re-takes software control.
    /// </summary>
    public bool ReleaseRgbControlToFirmware()
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                t.Write(QSeriesCoolerProtocol.BuildSetRgbControlMode(QSeriesCoolerProtocol.RgbModeMotherboard));
                _rgbInSwControl = false;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] release rgb control failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? Variant : "";
    bool IDfuFlashTarget.CanFlash(string firmwareType) =>
        firmwareType == QSeriesCoolerProtocol.VariantQ60 || firmwareType == QSeriesCoolerProtocol.VariantQ80;

    /// <summary>Drop the Q-series cooler into DFU: write the OTA key + magic over the serial port, then release it.</summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        var t = _transport;
        var pid = QSeriesCoolerProtocol.ProductIdForVariant(Variant);
        if (t is null || pid < 0) return false;
        var verify = OtaDfuEntry.SupportsPidCheck(Variant, State.FirmwareVersion);
        bool ok;
        try { ok = OtaDfuEntry.Enter(t, OtaProductKey.ForProductId(pid), verify); }
        catch { ok = true; }
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
                ServiceLog.Info($"[qseries-cooler] discovery returned {ports.Count} port(s)");
            }
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    _transport = t;
                    State.Serial = port.Serial;
                    State.Variant = port.Variant;
                    _portName = port.PortName;
                    ServiceLog.Info($"[qseries-cooler] connected to {port.PortName} (variant={port.Variant} serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[qseries-cooler] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            _rgbInSwControl = false;
            _portName = "";
        }
    }

    public bool PollFirmwareVersion()
    {
        // Under _lock so the fw-version request/response can't interleave with the
        // 30 Hz lighting stream now that WriteLighting runs on a separate thread.
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport;
            if (transport is null) return false;
            try
            {
                transport.DiscardInput();
                transport.Write(QSeriesCoolerProtocol.BuildGetFirmwareVersion());
                var buf = new byte[QSeriesCoolerProtocol.FirmwareVersionResponseLength];
                var n = transport.Read(buf, 400);
                if (n < QSeriesCoolerProtocol.FirmwareVersionResponseLength)
                {
                    ServiceLog.Warn($"[qseries-cooler] fw-version short read ({n} bytes) - dropping transport");
                    Disconnect();
                    return false;
                }
                var v = QSeriesCoolerProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
                if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // Telemetry read deadline. The pump answers a Port-0 query in a few ms; this
    // is the silent-device ceiling. Kept well under the fw-version poll's 400 ms
    // because telemetry polls every heartbeat (3 s) and holds _lock against the
    // 30 Hz lighting writer - a longer deadline would stall the LED stream that
    // long on a marginal serial link.
    private const int TelemetryReadTimeoutMs = 150;

    // The 240-byte Type-M channel-info reply is larger than the pump status, so it
    // gets a longer read deadline. Still well under the fw-version poll's 400 ms.
    private const int FanReadTimeoutMs = 250;

    /// <summary>
    /// Poll pump telemetry (Port-0, plus the Q80 second pump) and both Nexus Link channels
    /// into <see cref="State"/>. Read-only on the wire - issues no control writes. Shares
    /// <c>_lock</c> with the 30 Hz lighting stream, so it can't interleave with a frame
    /// write. A short / mis-framed reply skips the update (leaving lighting streaming);
    /// only a thrown transport error tears the port down for the heartbeat to reconnect.
    /// </summary>
    public bool PollTelemetry()
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport;
            if (transport is null) return false;
            try
            {
                transport.DiscardInput();
                transport.Write(QSeriesCoolerProtocol.BuildGetPort0Info());
                var buf = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                var n = transport.Read(buf, TelemetryReadTimeoutMs);
                if (!QSeriesCoolerProtocol.TryParsePort0PumpRpm(buf.AsSpan(0, n), out var pumpRpm))
                    return false;
                State.PumpRpm = pumpRpm;
                State.ControlMode = QSeriesCoolerProtocol.ControlModeOf(buf);
                State.TurboOn = QSeriesCoolerProtocol.TurboOnOf(buf);
                (State.CoolantTempInC, State.CoolantTempOutC) = QSeriesCoolerProtocol.CoolantTempsOf(buf);

                // Q80 has a single pump, same as Q60 - the second-pump port is
                // not queried, so HasPump2 stays false and no Pump 2 is shown.

                PollChannelDevices(transport, QSeriesCoolerProtocol.LinkChannel1, d => State.Channel1Devices = d, ref _lastLoggedChannel1);
                PollChannelDevices(transport, QSeriesCoolerProtocol.FanChannel, d => State.Channel2Devices = d, ref _lastLoggedChannel2);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] telemetry poll failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // Caller holds _lock and a live transport. Best-effort: a short/mis-framed/"no data
    // yet" reply leaves the channel's published device list untouched. Publishes every
    // successful parse (RPM changes every tick) but only builds and logs the topology
    // string when the cheap structural compare finds a real change.
    private void PollChannelDevices(INp50Transport transport, byte channel, Action<IReadOnlyList<QSeriesLinkDevice>> setDevices, ref IReadOnlyList<QSeriesLinkDevice>? lastLogged)
    {
        transport.DiscardInput();
        transport.Write(QSeriesCoolerProtocol.BuildGetChannelInfo(channel));
        var buf = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        var n = transport.Read(buf, FanReadTimeoutMs);
        if (!QSeriesCoolerProtocol.TryParseChannelDevices(buf.AsSpan(0, n), out var devices)) return;
        setDevices(devices);

        if (lastLogged is not null && SameTopology(devices, lastLogged)) return;
        lastLogged = devices;
        var signature = string.Join(",", devices.Select(d => $"{d.Model}:{d.LedCount}"));
        ServiceLog.Info($"[qseries-cooler] channel {channel} devices: {(signature.Length == 0 ? "(none)" : signature)}");
    }

    private static bool SameTopology(IReadOnlyList<QSeriesLinkDevice> a, IReadOnlyList<QSeriesLinkDevice> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Model != b[i].Model || a[i].LedCount != b[i].LedCount) return false;
        }
        return true;
    }

    // Caller holds _lock. Reads the 20-byte Port-0 status into buf; false on a
    // short / mis-framed reply. A control write echoes buf's fw-animation bytes.
    private bool ReadPort0(byte[] buf)
    {
        var t = _transport;
        if (t is null) return false;
        t.DiscardInput();
        t.Write(QSeriesCoolerProtocol.BuildGetPort0Info());
        var n = t.Read(buf, TelemetryReadTimeoutMs);
        return n >= QSeriesCoolerProtocol.Port0ResponseLength
            && QSeriesCoolerProtocol.TryParsePort0PumpRpm(buf.AsSpan(0, n), out _);
    }

    /// <summary>
    /// Drive the pump at <paramref name="dutyPercent"/> (0-100) under software
    /// control. Reads Port-0 first to preserve turbo + fw-animation state, maps
    /// the duty to the firmware's voltage byte, then writes the control frame.
    /// </summary>
    public bool SetPumpSpeed(int dutyPercent)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var turboOn = QSeriesCoolerProtocol.TurboOnOf(port0);
                var wire = QSeriesCoolerProtocol.MapPumpDutyToWire(dutyPercent, turboOn);
                var turboByte = turboOn ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                // HYTE switches to software mode in one frame, then sends the speed
                // in a SEPARATE frame with the mode byte cleared. Re-asserting the
                // mode in the speed frame resets the pump, so only switch when the
                // hub isn't already in software control.
                if (QSeriesCoolerProtocol.ControlModeOf(port0) != QSeriesCoolerProtocol.ControlModeSoftware)
                    t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeSoftware, wire, turboByte, port0));
                t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeKeep, wire, turboByte, port0));
                _lastPumpDuty = Math.Clamp(dutyPercent, 0, 100);
                State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set pump speed failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// The user-pinned control mode (null until one is chosen, reset on process
    /// start). The cooling provider swallows engine duty writes when this is a
    /// non-software mode so they don't flip the shared pump+fan hub back to
    /// software.
    /// </summary>
    public byte? DesiredControlMode => _desiredControlMode;

    /// <summary>Record the pinned mode without re-issuing a control write (the duty setters assert software live).</summary>
    public void MarkDesiredControlMode(byte? mode)
    {
        lock (_lock) { _desiredControlMode = mode; }
    }

    /// <summary>
    /// Drive every fan on one Nexus Link channel (1 or 2) at the given per-slot duties
    /// under software control. Ensures the hub is in software mode first (preserving the
    /// pump's last duty), caps each duty for turbo the same way the pump does, then writes
    /// the full per-slot frame - callers resend all slots on every call since the wire
    /// frame has no "leave unchanged" option.
    /// </summary>
    public bool SetChannelFanSpeeds(byte channel, IReadOnlyList<QSeriesCoolerProtocol.QSeriesFanSlotDuty> slotDuties)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var turboOn = QSeriesCoolerProtocol.TurboOnOf(port0);
                var capped = new QSeriesCoolerProtocol.QSeriesFanSlotDuty[slotDuties.Count];
                for (var i = 0; i < slotDuties.Count; i++)
                {
                    var s = slotDuties[i];
                    capped[i] = new QSeriesCoolerProtocol.QSeriesFanSlotDuty(
                        QSeriesCoolerProtocol.CapFanDutyForTurbo(s.Fan1Percent, turboOn),
                        QSeriesCoolerProtocol.CapFanDutyForTurbo(s.Fan2Percent, turboOn),
                        QSeriesCoolerProtocol.CapFanDutyForTurbo(s.Fan3Percent, turboOn));
                }
                // The fan frame only takes effect in software mode; switch if
                // needed, preserving the pump's last commanded duty so we don't
                // stall it while bringing the fan channel under control.
                if (QSeriesCoolerProtocol.ControlModeOf(port0) != QSeriesCoolerProtocol.ControlModeSoftware)
                {
                    var pumpWire = QSeriesCoolerProtocol.MapPumpDutyToWire(_lastPumpDuty, turboOn);
                    var turboByte = turboOn ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                    t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeSoftware, pumpWire, turboByte, port0));
                }
                t.Write(QSeriesCoolerProtocol.BuildSetChannelFanSpeeds(channel, capped));
                State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set channel fan speeds failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Switch the hub control mode (Software / Motherboard / Firmware). Reads
    /// Port-0 first to preserve turbo + fw-animation state, and skips the control
    /// frame when the hub already reports <paramref name="mode"/>: re-asserting a
    /// mode resets the pump, so a hand-back to the mode the cooler powered up in
    /// must be silent. <paramref name="pin"/> records the mode as user-chosen so
    /// the engine's duty writes are swallowed; <see cref="HandBackControlMode"/>
    /// is the release path, which never pins.
    /// </summary>
    public bool SetControlMode(byte mode, bool pin = true)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                if (QSeriesCoolerProtocol.ControlModeOf(port0) != mode)
                {
                    t.Write(QSeriesCoolerProtocol.BuildSetControl(
                        mode, 0,
                        QSeriesCoolerProtocol.TurboOnOf(port0) ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte,
                        port0));
                }
                State.ControlMode = mode;
                if (pin) _desiredControlMode = mode; // pin: a user-chosen mode the engine must not override
                // The live control byte alone doesn't engage the onboard curve;
                // firmware mode also needs the EEPROM default flipped to Temperature
                // (and reset to Motherboard when handing back), preserving the
                // stored curve. Mirrors HYTE SwitchToTemperatureMode /
                // SetFirmwareToMotherboardMode. These extra read + write round-trips
                // run under _lock, so this rare user-initiated switch can delay the
                // 30 Hz lighting stream more than a plain control write. Best-effort:
                // the live mode already changed, so a curve read/write hiccup here
                // must not fail the whole switch (the next poll re-reads true mode).
                if (SupportsFirmwareCurve)
                {
                    try
                    {
                        if (mode == QSeriesCoolerProtocol.ControlModeFirmware)
                            SetFirmwareDefaultModeLocked(t, QSeriesCoolerProtocol.FwDefaultModeTemperature);
                        else if (mode == QSeriesCoolerProtocol.ControlModeMotherboard)
                            SetFirmwareDefaultModeLocked(t, QSeriesCoolerProtocol.FwDefaultModeMotherboard);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[qseries-cooler] firmware default-mode engage failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set control mode failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Hand the hub to <paramref name="mode"/> once nothing drives it. The
    /// <paramref name="nothingDriven"/> check runs under the hub lock, so an
    /// engine claim lands wholly before it (the hand-back yields) or wholly after
    /// (the claim's own duty write flips the hub back to software); no ordering
    /// leaves the hub handed back while a channel still counts as driven. The
    /// engine's Software latch is stale once nothing is driven and is dropped; a
    /// user pin (Motherboard / Firmware) survives unless <paramref name="dropUserPin"/>,
    /// since a bulk release ends the pick along with the channels it applied to.
    /// </summary>
    public bool HandBackControlMode(byte mode, Func<bool> nothingDriven, bool dropUserPin)
    {
        lock (_lock)
        {
            if (!nothingDriven()) return true;
            if (dropUserPin || _desiredControlMode == QSeriesCoolerProtocol.ControlModeSoftware)
            {
                _desiredControlMode = null;
            }
            return SetControlMode(mode, pin: false);
        }
    }

    /// <summary>
    /// Toggle turbo. Writes the control frame (preserving the current mode and
    /// last-commanded pump duty) then persists the turbo flag to the MCU (FF CC 0A).
    /// </summary>
    public bool SetTurbo(bool on)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var mode = QSeriesCoolerProtocol.ControlModeOf(port0);
                var wire = QSeriesCoolerProtocol.MapPumpDutyToWire(_lastPumpDuty, on);
                var turboByte = on ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                t.Write(QSeriesCoolerProtocol.BuildSetControl(mode, wire, turboByte, port0));
                t.Write(QSeriesCoolerProtocol.BuildSetTurboMcu(turboByte));
                State.TurboOn = on;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set turbo failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Read the current firmware-driven LED animation from a fresh Port-0 poll (bytes
    /// [15..19]). Null when disconnected or the reply is short / mis-framed.
    /// </summary>
    public QSeriesCoolerProtocol.QSeriesFwAnimation? TryReadFirmwareAnimation()
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return null;
            var t = _transport;
            if (t is null) return null;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return null;
                return QSeriesCoolerProtocol.TryParseFirmwareAnimation(port0, out var animation) ? animation : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] read firmware animation failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
    }

    /// <summary>
    /// Persist the firmware-driven LED animation (FF CC 0C: effect, RGB, brightness). False
    /// when disconnected or the connected firmware predates <see cref="SupportsFirmwareAnimation"/>
    /// (brightness is one field of the same frame, so it rides along even on firmware below
    /// <see cref="SupportsFirmwareAnimationBrightness"/> - the caller uses that flag to decide
    /// whether to surface a brightness control at all, not to gate this write). Reads Port-0
    /// first and skips the write entirely when the requested block already matches - ROM-write
    /// endurance, and at most one write per call. Re-reads Port-0 after writing and logs a
    /// fails the call when the readback disagrees: a successful serial write is not evidence
    /// the firmware accepted it, and reporting it as saved is what let the UI show a save the
    /// device never took.
    /// </summary>
    public bool SetFirmwareAnimation(byte animation, byte r, byte g, byte b, byte brightness)
    {
        lock (_lock)
        {
            if (!EnsureConnected() || !SupportsFirmwareAnimation) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                if (QSeriesCoolerProtocol.TryParseFirmwareAnimation(port0, out var current)
                    && current.Animation == animation && current.R == r && current.G == g
                    && current.B == b && current.Brightness == brightness)
                {
                    return true;
                }

                // The control frame applies the animation to the live hub; the 0x0C
                // write persists it to the MCU. The MCU write alone leaves Port-0
                // reporting the old animation, which is what made a save look
                // accepted and then revert (NEX-62). Order and pairing match
                // HYTE's SmartHubCommandBase animation entry points.
                var turboOn = QSeriesCoolerProtocol.TurboOnOf(port0);
                t.Write(QSeriesCoolerProtocol.BuildSetControlWithAnimation(
                    QSeriesCoolerProtocol.ControlModeOf(port0),
                    QSeriesCoolerProtocol.MapPumpDutyToWire(_lastPumpDuty, turboOn),
                    turboOn ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte,
                    animation, r, g, b, brightness));
                t.Write(QSeriesCoolerProtocol.BuildWriteFirmwareAnimation(animation, r, g, b, brightness));

                Thread.Sleep(FwAnimationVerifySettleMs);
                var verify = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(verify)
                    || !QSeriesCoolerProtocol.TryParseFirmwareAnimation(verify, out var applied))
                {
                    ServiceLog.Warn("[qseries-cooler] firmware animation write unverified: readback unavailable");
                    return true;
                }
                // Firmware without the brightness field always reads back 0 there,
                // so comparing it would fail every write on those revisions.
                var brightnessMismatch = SupportsFirmwareAnimationBrightness && applied.Brightness != brightness;
                if (applied.Animation != animation || applied.R != r || applied.G != g
                    || applied.B != b || brightnessMismatch)
                {
                    // Reporting a verified-failed write as success is what let the
                    // UI show "saved" over unchanged hardware.
                    ServiceLog.Warn(
                        $"[qseries-cooler] firmware animation readback mismatch: wanted " +
                        $"{animation:X2}/{r:X2}{g:X2}{b:X2}/{brightness}, read " +
                        $"{applied.Animation:X2}/{applied.R:X2}{applied.G:X2}{applied.B:X2}/{applied.Brightness}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set firmware animation failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // HYTE MCUs drop reads armed while flash-committing a just-written block and no
    // completion signal exists; settle matches the keeb bench window (failure log 2026-07-12).
    private const int FwAnimationVerifySettleMs = 50;

    // EEPROM curve read deadline. The default-mode response comes back in a few
    // ms; kept under the fw-version poll's 400 ms but above the 150 ms telemetry
    // ceiling since this is a user-initiated read, not the hot lighting path.
    private const int FirmwareReadTimeoutMs = 300;

    /// <summary>
    /// Read the stored 5-point firmware temperature curve (FF CC 04) into
    /// <paramref name="points"/>. False on disconnect, an unsupported firmware,
    /// or a short / mis-framed reply.
    /// </summary>
    public bool TryReadFirmwareCurve(out QSeriesFirmwareCurvePoint[] points)
    {
        points = Array.Empty<QSeriesFirmwareCurvePoint>();
        lock (_lock)
        {
            if (!EnsureConnected() || !SupportsFirmwareCurve) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                return TryReadFirmwareCurveLocked(t, out points, out _);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] read firmware curve failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Persist a new 5-point firmware temperature curve to EEPROM (FF CC 03),
    /// preserving the current default mode so saving the curve never changes which
    /// controller drives the pump. False on disconnect / unsupported firmware.
    /// </summary>
    public bool WriteFirmwareCurve(IReadOnlyList<QSeriesFirmwareCurvePoint> points)
    {
        if (points.Count != QSeriesCoolerProtocol.FirmwareCurvePointCount) return false;
        lock (_lock)
        {
            if (!EnsureConnected() || !SupportsFirmwareCurve) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                // Read back the current default mode + curve: preserves the mode, and
                // skips the EEPROM write entirely when the curve already matches (ROM endurance).
                var hasCurrent = TryReadFirmwareCurveLocked(t, out var current, out var currentMode);
                var arr = new QSeriesFirmwareCurvePoint[points.Count];
                for (var i = 0; i < points.Count; i++) arr[i] = points[i];
                if (hasCurrent && CurveEquals(current, arr)) return true;
                var mode = hasCurrent ? currentMode : QSeriesCoolerProtocol.FwDefaultModeMotherboard;
                t.Write(QSeriesCoolerProtocol.BuildSetFirmwareMode(mode, arr));
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] write firmware curve failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // Field-by-field curve compare (no derived equality on the mutable QSeriesFirmwareCurvePoint struct).
    private static bool CurveEquals(QSeriesFirmwareCurvePoint[] a, QSeriesFirmwareCurvePoint[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].PumpTempC != b[i].PumpTempC || a[i].PumpDutyPercent != b[i].PumpDutyPercent
                || a[i].FanTempC != b[i].FanTempC || a[i].FanDutyPercent != b[i].FanDutyPercent)
            {
                return false;
            }
        }
        return true;
    }

    // Caller holds _lock. Reads FF CC 04 into a parsed curve + the current default
    // mode byte. False on a short / mis-framed reply.
    private bool TryReadFirmwareCurveLocked(INp50Transport t, out QSeriesFirmwareCurvePoint[] points, out byte defaultMode)
    {
        points = Array.Empty<QSeriesFirmwareCurvePoint>();
        defaultMode = QSeriesCoolerProtocol.FwDefaultModeMotherboard;
        t.DiscardInput();
        t.Write(QSeriesCoolerProtocol.BuildGetFirmwareDefault());
        var buf = new byte[QSeriesCoolerProtocol.FirmwareDefaultResponseLength];
        var n = t.Read(buf, FirmwareReadTimeoutMs);
        if (!QSeriesCoolerProtocol.TryParseFirmwareCurve(buf.AsSpan(0, n), out points)) return false;
        defaultMode = QSeriesCoolerProtocol.FirmwareDefaultModeOf(buf.AsSpan(0, n));
        return true;
    }

    // Caller holds _lock. Flips the EEPROM default mode to newMode while preserving
    // the stored curve (read it back, re-send with the new mode). No-op when already
    // in newMode or when the read fails.
    private void SetFirmwareDefaultModeLocked(INp50Transport t, byte newMode)
    {
        if (!TryReadFirmwareCurveLocked(t, out var curve, out var current)) return;
        if (current == newMode || curve.Length != QSeriesCoolerProtocol.FirmwareCurvePointCount) return;
        t.Write(QSeriesCoolerProtocol.BuildSetFirmwareMode(newMode, curve));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
