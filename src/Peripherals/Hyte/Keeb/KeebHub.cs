using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Singleton coordinator for the HYTE Keeb TKL. Owns the open vendor-HID
/// interface and the high-level read/write operations the lighting provider,
/// REST routes, and connection worker call into. Mirrors <see cref="Np50Hub"/>,
/// swapping the serial transport for the raw-HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>): feature reports
/// arm a stream/settings write and 65-byte output reports carry the pages.
///
/// We drive the keyboard directly instead of via OpenRGB - the bundled
/// openrgb-headless has its "HYTE Keeb TKL" detector disabled
/// (<see cref="Rgb.OpenRgbProcessManager"/>) so nothing else holds the
/// interface. Hot-plug is self-healing: each <see cref="EnsureConnected"/>
/// re-enumerates.
///
/// All device IO is serialised on <c>_io</c>: the 30 Hz RGB stream and the
/// (rare, HTTP-thread) settings write must never interleave their multi-report
/// bursts on the shared HID handle, and a teardown must not dispose the handle
/// mid-burst.
/// </summary>
public sealed class KeebHub : IDisposable
{
    /// <summary>User-facing product label, shared by every page that surfaces the keeb.</summary>
    public const string ProductName = "HYTE Keeb TKL";

    private readonly IHidEnumerator _hid;
    private readonly object _io = new();
    private IHidDevice? _device;
    private bool _disposed;

    // Reusable wire buffers (avoid per-frame allocation at 30 Hz). Guarded by _io.
    private readonly RgbColor[] _keyWire = new RgbColor[KeebLayout.KeyWireSlots];
    private readonly RgbColor[] _surroundWire = new RgbColor[KeebLayout.SurroundWireSlots];

    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;

    /// <summary>Per-side settle around a 0x06 settings write (HYTE reference: "fw will crash otherwise").</summary>
    private const int SettingsWriteSettleMs = 20;

    /// <summary>
    /// Settle after arming a multi-page write/read (vendor KeebTKLCommand waits
    /// 20 ms after every 0xF2/0xF3 header with the note "fw will crash otherwise").
    /// </summary>
    private const int PageBurstSettleMs = 20;

    /// <summary>Vendor writes macro (0xF3) pages with a 20 ms pause after each page; layer (0xF2) pages need none.</summary>
    private const int MacroInterPageDelayMs = 20;

    /// <summary>
    /// Settle after a full layer/macro page write before anything re-arms the
    /// interface: the vendor driver sleeps 50 ms between a table write and its
    /// read-back (SuoaiKeebTKLController ChangeKey/SetMacro) while the
    /// firmware commits to flash; a read armed inside that window is swallowed.
    /// </summary>
    private const int OnboardCommitSettleMs = 50;

    /// <summary>Read-burst pacing per the vendor driver: 20 ms after arming, 5 ms between pages.</summary>
    private const int ReadArmSettleMs = 20;
    private const int ReadInterPageDelayMs = 5;

    public KeebHub(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public KeebState State { get; } = new();

    public bool IsConnected => _device is not null;

    /// <summary>"keeb:&lt;serial&gt;" id, or empty when never connected.</summary>
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"keeb:{State.Serial}";

    /// <summary>Open the keeb's vendor HID interface if not already open.</summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (_device is not null) return true;
        lock (_io) return EnsureConnectedLocked();
    }

    // Caller must hold _io.
    private bool EnsureConnectedLocked()
    {
        if (_disposed) return false;
        if (_device is not null) return true;

        var chosen = FindVendorInterface(_hid);
        if (chosen is null) return false;

        // Overlapped so Read honors its timeout: the firmware silently swallows
        // a read armed while it is busy (e.g. committing a just-written layer
        // table to flash), and a synchronous read would then block forever
        // INSIDE _io, wedging every keeb operation including the RGB stream.
        // With a real timeout the read fails cleanly and the handle recycles.
        var dev = _hid.Open(chosen.Path, forInput: true);
        if (dev is null)
        {
            ServiceLog.Error($"[keeb] open failed for {chosen.Path}");
            return false;
        }
        _device = dev;
        State.Serial = !string.IsNullOrWhiteSpace(chosen.Serial)
            ? chosen.Serial!
            : StableIdFromPath(chosen.Path);
        _consecutiveWriteFailures = 0;
        ServiceLog.Info($"[keeb] connected (serial={State.Serial}, usage={chosen.UsagePage:X4}/{chosen.Usage:X2})");
        return true;
    }

    public void Disconnect()
    {
        lock (_io)
        {
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
    }

    /// <summary>
    /// Pick the keeb's vendor protocol collection (usage page 0xFF11 / usage
    /// 0xF0) from the enumerator, falling back to a collection that can carry
    /// the protocol's reports. Shared by the write hub and the input reader so
    /// both target the same HID interface. Returns null when not found.
    /// </summary>
    internal static HidDeviceInfo? FindVendorInterface(IHidEnumerator hid)
    {
        HidDeviceInfo? chosen = null;
        foreach (var info in hid.Find(KeebProtocol.VendorId, KeebProtocol.ProductId))
        {
            if (info.UsagePage == KeebProtocol.VendorUsagePage && info.Usage == KeebProtocol.VendorUsage)
            {
                return info;
            }
            if (chosen is null
                && info.FeatureReportByteLength >= KeebProtocol.FeatureReportSize
                && info.OutputReportByteLength >= KeebLayout.PageSize)
            {
                chosen = info;
            }
        }
        return chosen;
    }

    // ── RGB writes ──

    /// <summary>
    /// Stream a keyboard-zone frame. <paramref name="keysInLedOrder"/> carries
    /// one color per physical key in <see cref="KeebLayout.KeyWireValues"/>
    /// order; we scatter them to wire slots, arm the stream, and push 6 pages.
    /// </summary>
    public bool WriteKeyboard(ReadOnlySpan<RgbColor> keysInLedOrder)
    {
        lock (_io)
        {
            KeebLayout.MapKeysToWire(keysInLedOrder, _keyWire);
            return StreamZoneLocked(KeebProtocol.KeyboardStreamFeature, _keyWire, KeebLayout.KeyPageCount);
        }
    }

    /// <summary>Stream an underglow-zone frame (63 LEDs, 3 pages).</summary>
    public bool WriteSurround(ReadOnlySpan<RgbColor> ledsInLedOrder)
    {
        lock (_io)
        {
            Array.Clear(_surroundWire);
            var n = Math.Min(ledsInLedOrder.Length, _surroundWire.Length);
            for (var i = 0; i < n; i++) _surroundWire[i] = ledsInLedOrder[i];
            return StreamZoneLocked(KeebProtocol.SurroundStreamFeature, _surroundWire, KeebLayout.SurroundPageCount);
        }
    }

    /// <summary>
    /// Write the 0x06 settings page (game mode + firmware animation + rotary).
    /// <paramref name="page"/> is the full 65-byte report (byte 0 = report id).
    /// </summary>
    public bool WriteSettings(ReadOnlySpan<byte> page)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                // Settle between arming the settings write and sending the
                // page, and again after. HYTE's reference driver
                // (KeebTKLCommand.SetSettings) waits ~20 ms each side with the
                // note "Need delay otherwise fw will crash". Without it the
                // page is stored but the running animation doesn't re-apply it
                // (e.g. a brightness-only change doesn't dim). Settings writes
                // are rare (user actions).
                if (!dev.SetFeature(KeebProtocol.SettingsWriteFeature)) return RecordWriteFailureLocked("settings-feature");
                System.Threading.Thread.Sleep(SettingsWriteSettleMs);
                if (!dev.Write(page)) return RecordWriteFailureLocked("settings-page");
                System.Threading.Thread.Sleep(SettingsWriteSettleMs);
                _consecutiveWriteFailures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] settings write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordWriteFailureLocked(ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Read the 0x06 settings page back from the device (SetFeature 0x84 0x06,
    /// then read the input report). Returns the raw response, or null. Used to
    /// inspect what the firmware actually stores (e.g. whether the rotary knob
    /// moves the brightness byte) and to sync the panel to live device state.
    /// </summary>
    public byte[]? ReadSettings()
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return null;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.SettingsReadFeature))
                {
                    RecordWriteFailureLocked("settings-read-feature");
                    return null;
                }
                var buf = new byte[KeebLayout.PageSize];
                var n = dev.Read(buf, 250);
                if (n <= 0)
                {
                    // A late response could still land on this handle and be
                    // consumed by the next read as stale data; drop the handle
                    // (same guard as ReadLayerRaw). Also counts toward the
                    // failure threshold so an unplugged keeb that only ever
                    // fails reads still tears down.
                    ServiceLog.Error("[keeb] settings read timed out; dropping interface");
                    DropDeviceLocked();
                    return null;
                }
                _consecutiveWriteFailures = 0;
                return buf.AsSpan(0, n).ToArray();
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] settings read failed: {ex.GetType().Name}: {ex.Message}");
                RecordWriteFailureLocked(ex.GetType().Name);
                return null;
            }
        }
    }

    /// <summary>
    /// Read a layer's raw key-assignment pages (0x84 F2) for the active
    /// profile. Diagnostic only: arms the read and pulls the layer's
    /// output-report pages back. Returns null when disconnected or any page
    /// read times out. Used to reverse-engineer the firmware's slot order on
    /// the bench without ever writing the table.
    /// </summary>
    public byte[]? ReadLayerRaw(int profile, int layer)
    {
        lock (_io)
        {
            return ReadPagesLocked(
                KeebProtocol.LayerFeature(KeebProtocol.Read, profile, layer),
                KeebProtocol.LayerPageCount, "layer");
        }
    }

    /// <summary>
    /// Read a macro's raw 4 pages (0x84 F3) for a global firmware slot
    /// (profile*16 + index). Used to verify an onboard write took.
    /// </summary>
    public byte[]? ReadMacroRaw(int slot)
    {
        lock (_io)
        {
            return ReadPagesLocked(
                KeebProtocol.MacroFeature(KeebProtocol.Read, slot),
                KeebMacroCodec.PageCount, "macro");
        }
    }

    // Arm a read feature and pull the burst of output-report pages back.
    // Caller must hold _io. Any page timeout drops the handle: late pages
    // could still land on it and a later ReadSettings would consume one as
    // stale data.
    private byte[]? ReadPagesLocked(byte[] feature, int pageCount, string what)
    {
        if (!EnsureConnectedLocked()) return null;
        var dev = _device!;
        try
        {
            if (!dev.SetFeature(feature))
            {
                RecordWriteFailureLocked($"{what}-read-feature");
                return null;
            }
            System.Threading.Thread.Sleep(ReadArmSettleMs);
            var pages = new byte[pageCount * KeebLayout.PageSize];
            for (var p = 0; p < pageCount; p++)
            {
                if (p > 0) System.Threading.Thread.Sleep(ReadInterPageDelayMs);
                var buf = new byte[KeebLayout.PageSize];
                var n = dev.Read(buf, 250);
                if (n <= 0)
                {
                    ServiceLog.Error($"[keeb] {what} read timed out at page {p}; dropping interface");
                    DropDeviceLocked();
                    return null;
                }
                if (n < KeebLayout.PageSize)
                    ServiceLog.Info($"[keeb] {what} page {p} short read ({n} bytes)");
                System.Array.Copy(buf, 0, pages, p * KeebLayout.PageSize, System.Math.Min(n, KeebLayout.PageSize));
            }
            _consecutiveWriteFailures = 0;
            return pages;
        }
        catch (ObjectDisposedException) { return null; }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] {what} read failed: {ex.GetType().Name}: {ex.Message}");
            RecordWriteFailureLocked(ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Write a macro's 4 pages (0xF3) for global firmware <paramref name="slot"/> (profile*16 + index, 0..31).</summary>
    public bool WriteMacro(int slot, byte[] pages)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var ok = WritePagesLocked(KeebProtocol.MacroFeature(KeebProtocol.Write, slot), pages, KeebMacroCodec.PageCount, MacroInterPageDelayMs);
            if (ok) System.Threading.Thread.Sleep(OnboardCommitSettleMs);
            return ok;
        }
    }

    /// <summary>Write a layer's 8 key-assignment pages (0xF2) for (profile, layer).</summary>
    public bool WriteLayer(int profile, int layer, byte[] pages)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var ok = WritePagesLocked(KeebProtocol.LayerFeature(KeebProtocol.Write, profile, layer), pages, KeebProtocol.LayerPageCount, 0);
            if (ok) System.Threading.Thread.Sleep(OnboardCommitSettleMs);
            return ok;
        }
    }

    /// <summary>Select the active firmware profile (0 or 1) via 0x02.</summary>
    public bool SetProfile(int profile)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.ProfileSetFeature(profile))) return RecordWriteFailureLocked("profile");
                State.Profile = profile;
                _consecutiveWriteFailures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] profile write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordWriteFailureLocked(ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Best-effort read of the device-info block (0x05) to populate firmware
    /// version + layout. Non-fatal: the keeb's input-report read path is
    /// firmware-dependent, so a failed read just leaves the defaults.
    /// </summary>
    public bool ReadDeviceInfo()
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.DeviceInfoRequest))
                {
                    RecordWriteFailureLocked("device-info-feature");
                    return false;
                }
                var buf = new byte[KeebLayout.PageSize];
                var n = dev.Read(buf, 200);
                if (n <= 0)
                {
                    // Same stale-response hazard as the settings read: a late
                    // device-info block would be consumed by the next settings
                    // read and decoded as garbage. Drop the handle.
                    ServiceLog.Error("[keeb] device-info read timed out; dropping interface");
                    DropDeviceLocked();
                    return false;
                }
                if (KeebProtocol.ParseDeviceInfo(buf.AsSpan(0, n)) is { } di)
                {
                    State.FirmwareVersion = di.FirmwareVersion;
                    State.Layout = di.Layout;
                    _consecutiveWriteFailures = 0;
                    return true;
                }
                return false;
            }
            catch { return false; }
        }
    }

    // Arm a feature, settle, then push each 65-byte page as one output report
    // (vendor KeebTKLCommand: 20 ms after the 0xF2/0xF3 header or the firmware
    // crashes; macro pages additionally need 20 ms each). Caller holds _io.
    private bool WritePagesLocked(byte[] feature, byte[] pages, int pageCount, int interPageDelayMs)
    {
        var dev = _device!;
        try
        {
            if (!dev.SetFeature(feature)) return RecordWriteFailureLocked("feature");
            System.Threading.Thread.Sleep(PageBurstSettleMs);
            for (var p = 0; p < pageCount; p++)
            {
                if (!dev.Write(pages.AsSpan(p * KeebLayout.PageSize, KeebLayout.PageSize)))
                    return RecordWriteFailureLocked("page");
                if (interPageDelayMs > 0) System.Threading.Thread.Sleep(interPageDelayMs);
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] page write failed: {ex.GetType().Name}: {ex.Message}");
            return RecordWriteFailureLocked(ex.GetType().Name);
        }
    }

    // Caller must hold _io.
    private bool StreamZoneLocked(byte[] feature, RgbColor[] wire, int pageCount)
    {
        if (!EnsureConnectedLocked()) return false;
        var dev = _device!;
        try
        {
            // Arm the stream, then push each 65-byte page as one output report
            // (mirrors OpenRGB's hid_send_feature_report + N×hid_write).
            if (!dev.SetFeature(feature)) return RecordWriteFailureLocked("feature");
            var pages = KeebProtocol.BuildStreamPages(wire, pageCount);
            for (var p = 0; p < pageCount; p++)
            {
                if (!dev.Write(pages.AsSpan(p * KeebLayout.PageSize, KeebLayout.PageSize)))
                    return RecordWriteFailureLocked("page");
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] stream failed: {ex.GetType().Name}: {ex.Message}");
            return RecordWriteFailureLocked(ex.GetType().Name);
        }
    }

    // A single dropped report is transient (USB jitter); only tear down the
    // interface after a sustained burst so the next tick re-enumerates.
    // Caller must hold _io; disposes the handle inline (not via Disconnect) so
    // it never needs to re-acquire the lock.
    private bool RecordWriteFailureLocked(string where)
    {
        var n = ++_consecutiveWriteFailures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            ServiceLog.Error($"[keeb] {n} consecutive IO failures ({where}) - dropping interface");
            DropDeviceLocked();
        }
        return false;
    }

    // Caller must hold _io; disposes the handle inline (not via Disconnect) so
    // it never needs to re-acquire the lock.
    private void DropDeviceLocked()
    {
        _consecutiveWriteFailures = 0;
        try { _device?.Dispose(); } catch { /* best effort */ }
        _device = null;
    }

    private static string StableIdFromPath(string path)
    {
        // No serial reported - derive a stable short id from the device path
        // so the device id survives reconnects of the same physical port.
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"tkl-{h:x8}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
