using System;
using System.Threading;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Singleton coordinator for a HYTE Y70 Touch display controller. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub"/>:
/// opens the COM port lazily, polls the firmware version, exposes a state
/// snapshot, and carries the "drop into DFU" handshake. Reuses the
/// product-agnostic <see cref="Np50SerialTransport"/>.
/// </summary>
public sealed class Y70DisplayHub : IDisposable, IDfuFlashTarget
{
    private readonly IY70DisplayPortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    // GT backlight port held beside a non-GT primary (touch enumerates first and wins _transport).
    private INp50Transport? _gtTransport;
    private string _gtSerial = "";
    private string _gtPortName = "";
    private bool _disposed;
    // Starts at 0 (the silent default) so a device absent from boot never logs
    // "discovery returned 0"; only a real change (0->N found, or N->0 disconnect) logs.
    // -1 so the first attempt logs even when it finds nothing: a silent zero is
    // indistinguishable from the worker never running.
    private int _lastDiscoveredPortCount = -1;

    public Y70DisplayHub(IY70DisplayPortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public Y70DisplayState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>
    /// Increments on every fresh port open. Consumers holding one-time
    /// per-connection state (the Touch RGB-gain prep) compare against it so a
    /// replugged panel - whose STM32 and monitor state reset - is re-prepped.
    /// Written under _lock, read from other threads without it.
    /// </summary>
    public int ConnectionEpoch => Volatile.Read(ref _connectionEpoch);
    private int _connectionEpoch;

    /// <summary>"y70-touch" / "y70-infinite" / "y70-truly" / "y70-gt" once connected, else empty. Firmware-catalog key.</summary>
    public string Variant => State.Variant;

    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"y70:{State.Serial}";

    private bool PrimaryIsGt => IsConnected && Variant == Y70DisplayProtocol.VariantGt;

    private INp50Transport? BacklightTransport =>
        PrimaryIsGt ? _transport : _gtTransport is { IsOpen: true } ? _gtTransport : null;

    /// <summary>Lighting id of the GT backlight, whichever port carries it; empty when none is open.</summary>
    public string BacklightDeviceId =>
        PrimaryIsGt ? DeviceId : BacklightTransport is not null && !string.IsNullOrEmpty(_gtSerial) ? $"y70:{_gtSerial}" : "";

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? Variant : "";
    bool IDfuFlashTarget.CanFlash(string firmwareType) =>
        !string.IsNullOrEmpty(firmwareType) && firmwareType.StartsWith("y70", StringComparison.Ordinal);

    /// <summary>Drop the Y70 display into DFU: write the OTA key + magic over the serial port, then release it.</summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        var t = _transport;
        var pid = Y70DisplayProtocol.ProductIdForVariant(Variant);
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
                ServiceLog.Info($"[y70-display] discovery returned {ports.Count} port(s)");
            }
            foreach (var port in ports)
            {
                // The GT port held beside the primary is already open; reopening it fails on Windows.
                if (_gtTransport is { IsOpen: true } && port.PortName == _gtPortName) continue;
                try
                {
                    var t = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    State.Serial = port.Serial;
                    State.Variant = port.Variant;
                    _transport = t;
                    Interlocked.Increment(ref _connectionEpoch);
                    ServiceLog.Info($"[y70-display] connected to {port.PortName} (variant={port.Variant} serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[y70-display] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>Opens the GT port alongside a non-GT primary so its backlight stays drivable. No-op when the primary is the GT or the GT is already held.</summary>
    public bool EnsureBacklightConnected()
    {
        if (_disposed) return false;
        lock (_lock)
        {
            if (PrimaryIsGt || BacklightTransport is not null) return true;
            try { _gtTransport?.Dispose(); } catch { /* best effort */ }
            _gtTransport = null;
            foreach (var port in _discovery.Discover())
            {
                if (port.Variant != Y70DisplayProtocol.VariantGt) continue;
                try
                {
                    _gtTransport = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    _gtSerial = port.Serial;
                    _gtPortName = port.PortName;
                    ServiceLog.Info($"[y70-display] backlight connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[y70-display] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            DisconnectBacklightLocked();
        }
    }

    private void DisconnectBacklightLocked()
    {
        try { _gtTransport?.Dispose(); } catch { /* best effort */ }
        _gtTransport = null;
    }

    public bool PollFirmwareVersion()
    {
        // Hold _lock across the whole exchange: the heartbeat worker and the
        // brightness/power calls below share this one serial port, and two
        // interleaved request/response pairs would parse each other's bytes.
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport!;
            try
            {
                transport.DiscardInput();
                transport.Write(Y70DisplayProtocol.BuildGetFirmwareVersion());
                var buf = new byte[Y70DisplayProtocol.FirmwareVersionResponseLength];
                var n = transport.Read(buf, 400);
                if (n < Y70DisplayProtocol.FirmwareVersionResponseLength)
                {
                    ServiceLog.Warn($"[y70-display] fw-version short read ({n} bytes) - dropping transport");
                    Disconnect();
                    return false;
                }
                var v = Y70DisplayProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
                if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[y70-display] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Set brightness (0-100 percent) and screen power in one FF CC 01 frame on
    /// the serial models (Touch / Infinite). Returns false if the port can't be
    /// opened or the write throws. The command has no reply, so a true result
    /// means the bytes left the port - not that the firmware applied them.
    /// </summary>
    public bool SetBrightnessPower(bool screenOn, int percent)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport!;
            try
            {
                transport.DiscardInput();
                transport.Write(Y70DisplayProtocol.BuildSetBrightnessPower(screenOn, percent));
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[y70-display] set brightness/power failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>True when the connected panel is the Y70 GT, the only variant with a backlight to stream.</summary>
    public bool HasBacklight => BacklightTransport is not null;

    /// <summary>
    /// Stream one backlight frame (RGB triples in wire order). Shares the port
    /// lock with the heartbeat's version query so frames and replies never
    /// interleave. No reply is returned.
    /// </summary>
    public bool WriteBacklight(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            var transport = BacklightTransport;
            if (transport is null) return false;
            try
            {
                transport.Write(Y70DisplayProtocol.BuildBacklightStream(rgb));
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[y70-display] backlight write failed: {ex.GetType().Name}: {ex.Message}");
                if (PrimaryIsGt) Disconnect(); else DisconnectBacklightLocked();
                return false;
            }
        }
    }

    /// <summary>Read the controller's current screen-on flag + brightness (FF CC 02).</summary>
    public bool TryReadScreenInfo(out bool screenOn, out int brightness)
    {
        screenOn = false;
        brightness = 0;
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport!;
            try
            {
                transport.DiscardInput();
                transport.Write(Y70DisplayProtocol.BuildGetScreenInfo());
                var buf = new byte[13];
                var n = transport.Read(buf, 400);
                var info = Y70DisplayProtocol.ParseScreenInfo(buf.AsSpan(0, n));
                if (info is null) return false;
                screenOn = info.Value.ScreenOn;
                brightness = info.Value.Brightness;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[y70-display] read screen info failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
