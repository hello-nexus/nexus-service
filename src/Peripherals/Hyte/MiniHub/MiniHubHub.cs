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
    // Serializes request/response polls and write-only frames so lighting
    // streams never collide with tach/firmware polls on the shared COM port.
    private readonly object _ioLock = new();
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

    /// <summary>
    /// Synchronization lock for multi-frame operations (such as a 4-channel
    /// lighting cycle) that must not be interleaved with tach or firmware polls.
    /// </summary>
    internal object IoLock => _ioLock;

    /// <summary>
    /// Monotonic counter incremented whenever a new serial transport is opened
    /// or RGB control mode is re-asserted. Frame writers watch this to
    /// invalidate frame caches across reconnects.
    /// </summary>
    public int ConnectEpoch { get; private set; }

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
                    ConnectEpoch++;
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
        _lastFanControlMode = null;
        _port1Tach.Reset();
        _port2Tach.Reset();
        State.Port1Duty = 0;
        State.Port2Duty = 0;
        State.Port1RpmValid = false;
        State.Port1Rpm = 0;
        State.Port2RpmValid = false;
        State.Port2Rpm = 0;
    }

    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        lock (_ioLock)
        {
            var transport = _transport;
            if (transport is null) return false;
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
    }

    public bool SetRgbControlMode(byte mode)
    {
        var ok = SendOnly(MiniHubProtocol.BuildSetRgbControlMode(mode));
        if (ok && mode == MiniHubProtocol.RgbModeSoftware)
        {
            ScrubLightingSramIfSerial();
            ConnectEpoch++;
        }
        return ok;
    }

    /// <summary>
    /// Scrubs <c>port3_buf</c>, <c>port4_buf</c>, and the 60-halfword WS2812 reset
    /// preamble <c>pwm_buf[0..59]</c> (<c>0x20000644..0x200006BB</c>) in MiniHub SRAM.
    /// Firmware 1.0.1.1's <c>build_pwm</c> starts writing at <c>pwm_idx = 60</c> and
    /// never zeroes <c>pwm_buf[0..59]</c> after power-on; if a prior unpadded 51-byte
    /// Channel 4 tail overflowed <c>port4_buf[299]</c> into <c>0x20000644</c>, those
    /// non-zero halfwords corrupt the reset preamble until scrubbed.
    /// </summary>
    private void ScrubLightingSramIfSerial()
    {
        lock (_ioLock)
        {
            var transport = _transport;
            if (transport is not Np50SerialTransport) return;
            try
            {
                var header = new byte[64];
                header[0] = 0xFF;
                header[1] = 0xEE;
                header[2] = 0x03;
                header[3] = 0x03;
                header[4] = 0x01;
                header[5] = 0x68;
                header[6] = 0x00;
                WriteRawChunkIfSerial(transport, header, InterChannelHoldoffMs);
                var zero64 = new byte[64];
                for (var i = 0; i < 10; i++)
                {
                    WriteRawChunkIfSerial(transport, zero64, i == 9 ? PostLatchHoldoffMs : InterChannelHoldoffMs);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[minihub] SRAM scrub failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public bool WriteLighting(int channel, ReadOnlySpan<RgbColor> leds)
        => SendOnly(MiniHubProtocol.BuildLightingStream(channel, leds));

    public bool SetFanControlMode(byte mode)
    {
        if (IsConnected && _lastFanControlMode == mode) return true;
        var ok = SendOnly(MiniHubProtocol.BuildSetFanControlMode(mode));
        if (ok) _lastFanControlMode = mode;
        return ok;
    }

    /// <summary>
    /// Push a single port-pair PWM update to the hub. Both ports are written
    /// in one frame (the protocol has no per-port command); pass the
    /// previously-applied value for whichever port the caller doesn't want
    /// to change. Stores the commanded values on <see cref="State"/> so the
    /// cooling provider can read them back without a round-trip.
    /// </summary>
    public bool WriteFanSpeed(int port1Percent, int port2Percent)
    {
        var p1 = Math.Clamp(port1Percent, MiniHubProtocol.FanMinDutyPercent, MiniHubProtocol.FanMaxDutyPercent);
        var p2 = Math.Clamp(port2Percent, MiniHubProtocol.FanMinDutyPercent, MiniHubProtocol.FanMaxDutyPercent);
        if (IsConnected && State.Port1Duty == p1 && State.Port2Duty == p2) return true;
        var ok = SendOnly(MiniHubProtocol.BuildSetFanSpeed(p1, p2));
        if (ok)
        {
            State.Port1Duty = p1;
            State.Port2Duty = p2;
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
        lock (_ioLock)
        {
            var transport = _transport;
            if (transport is null) return false;
            try
            {
                if (!SendRequest(transport, "fan-speed", MiniHubProtocol.BuildGetFanSpeed())) return false;
                var buf = new byte[MiniHubProtocol.GetFanSpeedResponseLength];
                var n = transport.Read(buf, 300);
                if (n < MiniHubProtocol.GetFanSpeedResponseLength)
                {
                    if (!SendRequest(transport, "fan-speed", MiniHubProtocol.BuildGetFanSpeed())) return false;
                    n = transport.Read(buf, 300);
                    if (n < MiniHubProtocol.GetFanSpeedResponseLength) return FailPoll("fan-speed", $"short read ({n} bytes)");
                }
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
        lock (_ioLock)
        {
            var transport = _transport;
            if (transport is null) return false;
            try
            {
                WritePacketized(transport, request);
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
            WaitForPreviousWriteIfSerial(transport);
            transport.DiscardInput();
            WritePacketized(transport, request);
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

    // Firmware 1.0.1.1's CDC_Receive_FS only inspects rx_buf[0..3] == FF EE 03 <ch>
    // at index 0 of each 64-byte USB Bulk OUT packet. Writing channels 1..4
    // back-to-back with 0 ms spacing causes usbser.sys to coalesce the 29-byte
    // tail of channel N (157 = 64+64+29) with the 7-byte header of channel N+1
    // into a single 64-byte packet, overflowing the 152-byte (0x98) SRAM stride
    // of portN_buf into port(N+1)_buf[0..4] and shifting its GRB data by 5 bytes
    // (2 color components). Enforce a 5 ms inter-channel gap so each channel's
    // short packet completes on the 12 Mbps Full-Speed bus before the next header
    // is sent, and a 25 ms post-latch holdoff after channel 4's 307-byte frame
    // while main() runs build_pwm + TIM1 DMA + HAL_Delay(2+2+2+4) (~15 ms).
    private const int InterChannelHoldoffMs = 5;
    private const int PostLatchHoldoffMs = 25;

    private void WritePacketized(INp50Transport transport, byte[] request)
    {
        if (transport is not Np50SerialTransport)
        {
            transport.Write(request);
            return;
        }

        // Lighting stream frames (FF EE 03 <ch> 01 68 00 ...) are sent in
        // 64-byte zero-padded USB Bulk OUT chunks. Firmware 1.0.1.1's
        // CDC_Receive_FS copies Buf[0..*Len-1] into the 64-byte rx_buf without
        // clearing rx_buf[*Len..63], then copies all 64 bytes of rx_buf into
        // portN_buf on every continuation packet. Zero-padding the final chunk
        // of each channel to 64 bytes guarantees rx_buf[29..63] (channels 1-3)
        // and rx_buf[51..63] (channel 4, which overflows port4_buf[299] into
        // pwm_buf[0..6] at 0x20000644) are always 0x00.
        if (request.Length >= MiniHubProtocol.OtherPortPaddedLength
            && request[0] == 0xFF
            && request[1] == 0xEE
            && request[2] == 0x03)
        {
            var chunk = new byte[64];
            for (var offset = 0; offset < request.Length; offset += 64)
            {
                Array.Clear(chunk, 0, 64);
                var count = Math.Min(64, request.Length - offset);
                Buffer.BlockCopy(request, offset, chunk, 0, count);
                var isLastChunk = offset + 64 >= request.Length;
                var chunkHoldoff = isLastChunk && request.Length >= MiniHubProtocol.Port4PaddedLength
                    ? PostLatchHoldoffMs
                    : InterChannelHoldoffMs;
                WriteRawChunkIfSerial(transport, chunk, chunkHoldoff);
            }
            return;
        }

        WriteRawChunkIfSerial(transport, request, InterChannelHoldoffMs);
    }

    private void WriteRawChunkIfSerial(INp50Transport transport, byte[] chunk, int holdoffMs)
    {
        WaitForPreviousWriteIfSerial(transport);
        transport.Write(chunk);
        _nextSerialWriteAllowedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            + (holdoffMs * System.Diagnostics.Stopwatch.Frequency) / 1000;
    }

    private void WaitForPreviousWriteIfSerial(INp50Transport transport)
    {
        if (transport is not Np50SerialTransport) return;
        var remainingTicks = _nextSerialWriteAllowedTimestamp - System.Diagnostics.Stopwatch.GetTimestamp();
        if (remainingTicks > 0)
        {
            var waitMs = (int)((remainingTicks * 1000 + System.Diagnostics.Stopwatch.Frequency - 1)
                / System.Diagnostics.Stopwatch.Frequency);
            if (waitMs > 0)
            {
                Thread.Sleep(waitMs);
            }
        }
    }

    private long _nextSerialWriteAllowedTimestamp;
    private byte? _lastFanControlMode;

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
