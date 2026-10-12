using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Owns the CNVS COM port for the lifetime of the service. Mirrors
/// <see cref="Np50.Np50Hub"/>'s "open at startup, hold forever" model so
/// OpenRGB-headless (which also tries to claim CNVS) finds the port busy
/// and silently skips it. Whoever opens the COM port first wins under
/// Windows serial semantics; running our open BEFORE OpenRGB launches -
/// which <see cref="CnvsConnectionWorker"/> guarantees via an early
/// background tick - makes us the de-facto owner.
///
/// Surfaces three wire commands:
/// - <see cref="WriteSettings"/>: EEPROM-persisted firmware bits
///   (`FF DC 07 suppressBoot ledsOnWhenOff`).
/// - <see cref="ReadSettings"/>: queries `FF DC 08`, reads 9 bytes.
/// - <see cref="WriteLighting"/>: the 157-byte LED stream frame
///   (`FF EE 02 01 00 32 00 + 50×3 GRB bytes`) lifted from
///   HYTE's HYTEMousematController.StreamingCommand in OpenRGB.
/// - <see cref="SetFirmwareAnimationOff"/>: `FF DC 05 00` - every
///   streaming frame must be preceded by this (HYTE's reference does
///   the same in CNVSBaseController.SendToHardware) so the firmware
///   stops overlaying its boot animation.
/// </summary>
public sealed class CnvsHub : IDisposable, IDfuFlashTarget
{
    /// <summary>Number of physical LEDs on the CNVS (per HYTE's OpenRGB driver).</summary>
    public const int LedCount = 50;

    /// <summary>
    /// Per-channel brightness ceiling HYTE's reference driver applies.
    /// Equivalent to `(72 * value) / 100` in HYTEMousematController.cpp;
    /// keeps a fully-saturated LED at ~72% of its raw drive current so the
    /// mat doesn't overheat or draw past the USB-port budget on long bursts.
    /// Applied centrally in <see cref="WriteLighting"/> so callers pass raw
    /// 0..255 colors and don't need to know about the cap.
    /// </summary>
    public const int MaxChannelBrightness = 72;

    private readonly ICnvsPortDiscovery _discovery;
    private readonly object _writeLock = new();
    private readonly object _readLock = new();
    private SerialPort? _port;
    private string _portName = "";
    private string _serial = "";
    private int _productId;
    private string _firmwareVersion = "";
    private bool _disposed;

    public CnvsHub(ICnvsPortDiscovery discovery)
    {
        _discovery = discovery;
    }

    /// <summary>True iff we currently hold an open CNVS port.</summary>
    public bool IsConnected => !_disposed && _port?.IsOpen == true;

    /// <summary>USB serial of the open device, or empty when never connected.</summary>
    public string Serial => _serial;

    /// <summary>"cnvs:&lt;serial&gt;" device id; empty until we open the port.</summary>
    public string DeviceId => string.IsNullOrEmpty(_serial) ? "" : $"cnvs:{_serial}";

    /// <summary>Operating USB PID of the open device (0B00/0B01/0B02/0BFF), 0 until connected.</summary>
    public int ProductId => _productId;

    /// <summary>Firmware-catalog variant key for the connected unit (cnvs-left/cnvs-v1/cnvs-white).</summary>
    public string Variant => CnvsProtocol.VariantForProductId(_productId);

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? Variant : "";

    /// <summary>
    /// Drop the CNVS into DFU mode: write the OTA product key + the DFU magic
    /// over the serial port, then release the port so dfu-util can claim the
    /// re-enumerated bootloader (3402:0a00). CNVS firmware doesn't support the
    /// FF DC 07 key readback, so we don't verify (matches HYTE's OTAHelper for
    /// non-PID-check devices). Bench-confirmed working 2026-05-28.
    /// </summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                var key = OtaProductKey.ForProductId(_productId);
                port.DiscardInBuffer();
                port.Write(key, 0, key.Length);
                // 30 ms key-write→magic gap per HYTE OTAHelper.Update.
                Thread.Sleep(30);
                var magic = OtaDfuEntry.MagicBytes();
                port.Write(magic, 0, magic.Length);
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                // The port commonly drops mid-write as the device reboots into
                // DFU - that's the success signal, not a failure.
                Console.Error.WriteLine($"[cnvs] EnterDfuMode (port dropping as device reboots): {ex.GetType().Name}");
            }
            // Release the COM port so dfu-util can open the DFU device.
            Disconnect();
            return true;
        }
    }

    /// <summary>
    /// Last firmware version reported by the device, formatted "Major.Minor.Build.Hw"
    /// (e.g. "1.0.2.2"). Empty until <see cref="GetFirmwareVersion"/> succeeds;
    /// cleared on <see cref="Disconnect"/>.
    /// </summary>
    public string FirmwareVersion => _firmwareVersion;

    /// <summary>
    /// True when the connected firmware honors the <c>FF DC 07</c> /
    /// <c>FF DC 08</c> settings commands. Introduced in CNVS firmware
    /// v1.0.2.1 per <c>hyte-refs/hyte-documents/firmware-protocol/CNVS/stm32-commands.md</c>
    /// §3 - older firmware (e.g. 1.0.1.1 observed in the Y70 dev unit)
    /// silently accepts and ignores the FF DC 07 frame.
    /// </summary>
    public bool SettingsSupported =>
        CnvsFirmware.SupportsSettings(_firmwareVersion);

    /// <summary>
    /// Try to open a CNVS port if one is enumerable and we don't already
    /// hold one. Idempotent; safe to call from a background worker. Returns
    /// true iff the hub is connected after the call.
    /// </summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_writeLock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            foreach (var info in ports)
            {
                try
                {
                    var port = OpenPort(info.PortName);
                    _port = port;
                    _portName = info.PortName;
                    _serial = info.Serial ?? "";
                    _productId = info.ProductId;
                    ServiceLog.Info($"[cnvs] connected on {info.PortName} (serial={_serial}, pid=0x{_productId:X4}, variant={Variant})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[cnvs] open {info.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>Release the port. Used on shutdown and on transport errors.</summary>
    public void Disconnect()
    {
        lock (_writeLock)
        {
            try { if (_port?.IsOpen == true) _port.Close(); } catch { }
            try { _port?.Dispose(); } catch { }
            _port = null;
            _firmwareVersion = "";
            // Force the next connect to re-apply settings before the
            // lighting writer is allowed to stream - see WriteSettings
            // doc for the firmware invariant.
            IsReadyForStreaming = false;
        }
    }

    /// <summary>
    /// Persist both firmware settings to EEPROM. Read-back after to verify
    /// the firmware accepted them - returns true when the read-back matches,
    /// false on transport error or device disagreement.
    /// </summary>
    /// <summary>
    /// Write the 5-byte SetSettings command exactly as HYTE's
    /// <c>CNVSHelper.ChangeCnvsSetting</c> does - no bracket, no extra
    /// preamble.
    ///
    /// **Firmware version requirement.** Per
    /// <c>hyte-refs/hyte-documents/firmware-protocol/CNVS/stm32-commands.md</c>
    /// §3, the <c>FF DC 07</c> command (and its <c>FF DC 08</c> read-back
    /// in <see cref="ReadSettings"/>) were introduced in CNVS firmware
    /// <b>v1.0.2.1 / v1.0.2.2</b>. Units running older firmware
    /// (Y70 dev hardware in the lab observed at 1.0.1.1) silently accept
    /// the bytes - no error, no disconnect - and do nothing: the boot
    /// animation still plays, the PC-off behavior is unchanged, and
    /// the FF DC 08 read-back returns zero bytes. Returning true from
    /// this method therefore means "bytes left the port", NOT "firmware
    /// honored the setting." The UI side should gate the toggles on the
    /// firmware version reported by <see cref="GetFirmwareVersion"/>;
    /// see the matching TODO in nexus-web's CnvsDevicePage.tsx.
    /// </summary>
    public bool WriteSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        if (!EnsureConnected()) return false;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                port.DiscardInBuffer();
                var frame = CnvsProtocol.BuildSetSettings(suppressBootAnimation, keepLedsOnWhenPcOff);
                port.Write(frame, 0, frame.Length);
                ServiceLog.Info(
                    $"[cnvs] WriteSettings sent {frame.Length}B on {_portName} (serial={_serial}): boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}");
                Thread.Sleep(20);

                // Best-effort read-back; some firmware revs don't echo
                // FF DC 08 at all. Logged only.
                var readBack = ReadSettingsLocked(port);
                if (readBack is { } rb)
                    ServiceLog.Info($"[cnvs] WriteSettings read-back: {rb}");
                else
                    ServiceLog.Warn("[cnvs] WriteSettings: read-back returned no data");

                // Open the gate so the lighting writer can start streaming.
                IsReadyForStreaming = true;
                return true;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[cnvs] WriteSettings exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// True after <see cref="WriteSettings"/> has run on the current
    /// connection. Cleared on <see cref="Disconnect"/>. The lighting
    /// frame writer gates every tick on this so a fresh USB connect
    /// gets the FF DC 07 settings write BEFORE any FF DC 05 streaming
    /// command goes out - the firmware invariant that makes the
    /// settings actually persist.
    /// </summary>
    public bool IsReadyForStreaming { get; private set; }

    /// <summary>
    /// Query the firmware version (FF DD 02 → 7-byte response).
    /// Returns "Major.Minor.Build.Hw" on success, null on no-response / not
    /// connected. Used by <see cref="CnvsConnectionWorker"/> on first connect as
    /// a sanity probe: if this responds but FF DC 08 (settings read) doesn't,
    /// the firmware is talking but doesn't implement the settings command-pair
    /// on this revision.
    /// </summary>
    public string? GetFirmwareVersion()
    {
        if (!EnsureConnected()) return null;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return null;
            try
            {
                lock (_readLock)
                {
                    port.DiscardInBuffer();
                    var req = CnvsProtocol.BuildGetFirmwareVersion();
                    port.Write(req, 0, req.Length);
                    // 20 ms write→read gap per HYTE CNVSHelper.cs:93 - firmware
                    // takes that long to assemble the 7-byte version reply.
                    Thread.Sleep(20);
                    var buf = new byte[7];
                    var n = ReadExact(port, buf, 200);
                    if (n < 7) return null;
                    var s = CnvsProtocol.ParseFirmwareVersion(buf);
                    if (string.IsNullOrEmpty(s)) return null;
                    _firmwareVersion = s;
                    return s;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] GetFirmwareVersion exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
    }

    /// <summary>Read the EEPROM-persisted settings. Null on transport error / not connected.</summary>
    public CnvsProtocol.CnvsSettings? ReadSettings()
    {
        if (!EnsureConnected()) return null;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return null;
            try
            {
                return ReadSettingsLocked(port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] ReadSettings exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
    }

    /// <summary>
    /// Push one LED frame. <paramref name="leds"/> must be exactly
    /// <see cref="LedCount"/> entries; shorter spans are zero-padded.
    /// Bytes are GRB-ordered and pre-scaled by <see cref="MaxChannelBrightness"/>
    /// / 100 (the same cap HYTE's OpenRGB driver applies). The firmware's
    /// boot animation must be silenced first via
    /// <see cref="SetFirmwareAnimationOff"/> or it overlays the stream.
    /// </summary>
    public bool WriteLighting(ReadOnlySpan<RgbColor> leds)
    {
        if (!EnsureConnected()) return false;
        // 157-byte fixed frame: 7-byte header + 50 LEDs × 3 bytes.
        Span<byte> buf = stackalloc byte[157];
        buf.Clear();
        buf[0] = 0xFF; buf[1] = 0xEE; buf[2] = 0x02;
        buf[3] = 0x01; buf[4] = 0x00;
        buf[5] = (byte)LedCount; // 0x32
        buf[6] = 0x00;
        var count = Math.Min(leds.Length, LedCount);
        for (var i = 0; i < count; i++)
        {
            var off = 7 + (i * 3);
            buf[off + 0] = (byte)((MaxChannelBrightness * leds[i].G) / 100);
            buf[off + 1] = (byte)((MaxChannelBrightness * leds[i].R) / 100);
            buf[off + 2] = (byte)((MaxChannelBrightness * leds[i].B) / 100);
        }
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                var arr = buf.ToArray();
                port.Write(arr, 0, arr.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] WriteLighting exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Disable the firmware's boot animation so it stops overlaying the
    /// streamed colors. HYTE's CNVSBaseController.SendToHardware calls the
    /// equivalent <c>TurnFwAnimationOFF</c> on every frame that flips out of
    /// firmware-animation mode; the 4-byte write costs ~30 µs over the CDC
    /// link.
    /// </summary>
    public bool SetFirmwareAnimationOff()
    {
        if (!EnsureConnected()) return false;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                var frame = CnvsProtocol.BuildTurnAnimationOff();
                port.Write(frame, 0, frame.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] SetFirmwareAnimationOff exception: {ex.GetType().Name}: {ex.Message}");
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

    // ── Internals ──

    private static SerialPort OpenPort(string portName)
    {
        var port = new SerialPort(portName, baudRate: 115200, Parity.None, dataBits: 8, StopBits.One)
        {
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private CnvsProtocol.CnvsSettings? ReadSettingsLocked(SerialPort port)
    {
        lock (_readLock)
        {
            port.DiscardInBuffer();
            var get = CnvsProtocol.BuildGetSettings();
            port.Write(get, 0, get.Length);
            Thread.Sleep(20);
            var buf = new byte[9];
            var n = ReadExact(port, buf, 200);
            if (n < 5) return null;
            return CnvsProtocol.ParseGetSettings(buf.AsSpan(0, n));
        }
    }

    private static int ReadExact(SerialPort port, byte[] buf, int timeoutMs)
    {
        var deadline = Environment.TickCount + Math.Max(1, timeoutMs);
        var total = 0;
        while (total < buf.Length)
        {
            var remaining = deadline - Environment.TickCount;
            if (remaining <= 0) break;
            port.ReadTimeout = remaining;
            int n;
            try
            {
                n = port.Read(buf, total, buf.Length - total);
            }
            catch (TimeoutException) { break; }
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>One CNVS device as enumerated by the OS, before we open the port.</summary>
public sealed class CnvsPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    /// <summary>Operating USB PID (0B00/0B01/0B02/0BFF) the port matched - selects the firmware variant.</summary>
    public int ProductId { get; init; }
}

/// <summary>OS-level CNVS USB-CDC port discovery. Windows uses SetupAPI;
/// non-Windows returns empty.</summary>
public interface ICnvsPortDiscovery
{
    IReadOnlyList<CnvsPortInfo> Discover();
}

/// <summary>
/// 24-bit RGB color used by the CNVS lighting write. Mirrors the
/// <see cref="Np50.RgbColor"/> shape so callers can share buffers across
/// hubs without translation.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
