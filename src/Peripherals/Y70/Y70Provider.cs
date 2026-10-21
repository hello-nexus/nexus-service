using System;
using System.Threading;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Peripherals.Y70;

/// <summary>
/// Real <see cref="IY70Provider"/>: drives Y70 brightness + screen power on the
/// panel. Three transports, picked per variant (<see cref="Y70DisplayHub.Variant"/>):
///   • Serial (Infinite, and any connected-but-unidentified panel): the STM32
///     controller's <c>FF CC 01</c> frame over the CDC COM port managed by
///     <see cref="Y70DisplayHub"/>.
///   • Hybrid (original Touch, 0x0C00): screen power over serial with the
///     backlight PWM pinned to 100% (PWM dimming makes that panel hum), and
///     brightness through the monitor's DDC/CI RGB video-gain registers -
///     reference Y70TouchDevice. The gain registers need a one-time
///     per-connection prep (<see cref="EnsureTouchPrepared"/>).
///   • DDC/CI (Truly / GW): VCP 0x10 (brightness) + 0xD6 (power) via the
///     platform display-brightness provider, mirroring the reference
///     Y70TouchDdcciDeviceBase: brightness and power are independent writes,
///     and brightness is raw 0-100 (the serial minimum-brightness floor is a
///     PWM humming fix that does not apply to DDC). Truly also exposes a CDC
///     port (fw-version/DFU), so a live serial connection alone must not
///     select the serial transport.
/// Orientation goes through <see cref="IDisplayOrientationProvider"/> (Windows
/// rotation).
///
/// The config store is the source of truth the UI reads; every setter writes
/// the hardware first (best-effort) and then persists, so GET reflects the last
/// requested value even when the panel is briefly detached.
///
/// UNVERIFIED: the DDC/CI branch (Truly / GW) and the Touch RGB-gain branch
/// have NOT been tested on hardware; only the plain serial path (Infinite,
/// RTK0004) was on the bench. VCP codes and write semantics are ported from
/// the reference Y70DDCCIHelper / Y70TouchDevice; confirm on real panels
/// before trusting those paths. The reference also persists a 100% default
/// PWM to the controller's EEPROM at init (SetDefaultBrightness); that write
/// is deliberately not ported - blind EEPROM writes on unverified hardware
/// are the failure mode the HYTE EEPROM failure-log entry warns about - so a
/// power-cycled Touch may hum until the next brightness/toggle apply re-pins
/// the PWM.
/// </summary>
public sealed class Y70Provider : IY70Provider, IDisposable
{
    private readonly Y70DisplayHub _hub;
    private readonly IConfigStore _store;
    private readonly IDisplayOrientationProvider _orientation;
    private readonly IDisplayBrightnessProvider _ddc;
    private readonly HardwarePresence? _presence;

    // A DDC lookup enumerates every display (i2c transactions on Linux), so the heartbeat reuses the last result for a bounded time. A failed write drops a cached hit.
    private const long DdcLookupTtlMs = 60_000;
    private readonly object _ddcIdLock = new();
    private string? _ddcId;
    private long _ddcIdCheckedAt;
    private bool _ddcIdChecked;

    /// <summary>Linux only: gate IsConnected on USB presence and cache DDC lookups.</summary>
    internal bool LimitDdcScans { get; init; } = OperatingSystem.IsLinux();

    public Y70Provider(
        Y70DisplayHub hub,
        IConfigStore store,
        IDisplayOrientationProvider orientation,
        IDisplayBrightnessProvider ddc,
        HardwarePresence? presence = null)
    {
        _presence = presence;
        _hub = hub;
        _store = store;
        _orientation = orientation;
        _ddc = ddc;
    }

    public bool IsConnected()
    {
        if (_hub.IsConnected)
            return true;
        // The serial controller's USB id is proof of a Y70, so no display scan is needed.
        if (LimitDdcScans && _presence is not null && _presence.UsbPresent(
                Y70DisplayProtocol.VendorId,
                Y70DisplayProtocol.Y70TouchProductId,
                Y70DisplayProtocol.Y70InfiniteProductId,
                Y70DisplayProtocol.Y70TrulyProductId))
        {
            return true;
        }
        return DdcDisplayId(reuseMiss: true) is not null;
    }

    public string GetOrientation() => _store.Load().Y70.Orientation;

    public void SetOrientation(string orientation)
    {
        _store.Update(s => s.Y70.Orientation = orientation);
    }

    public bool GetForceOrientation() => _store.Load().Y70.ForceOrientation;

    public void SetForceOrientation(bool forceOrientation)
    {
        _store.Update(s => s.Y70.ForceOrientation = forceOrientation);
    }

    /// <summary>Pushes the current effective orientation (PortraitFlipped when
    /// ForceOrientation is set, else the stored preference) to hardware.</summary>
    public void ApplyEffectiveOrientation()
    {
        var y70 = _store.Load().Y70;
        var effective = y70.ForceOrientation ? "PortraitFlipped" : y70.Orientation;
        var (ok, detail) = _orientation.SetY70Orientation(effective);
        // Log every apply (detail distinguishes an actual rotation, "applied X
        // from=Y", from a no-op, "already X") so a re-apply loop is visible.
        ServiceLog.Info($"[y70-orient] effective='{effective}' force={y70.ForceOrientation} ok={ok} detail='{detail}'");
    }

    public int GetBrightness() => _store.Load().Y70.Brightness;

    public void SetBrightness(int brightness)
    {
        var pct = Math.Clamp(brightness, 0, 100);
        // Touch and Truly both dim over DDC/CI (gain registers vs VCP 0x10);
        // WritePendingDdcBrightness picks the register set by variant.
        if (SerialTransportSelected)
        {
            ApplySerial(!_store.Load().Y70.ScreenOff, pct);
        }
        else
        {
            DdcSetBrightnessCoalesced(pct);
        }
        _store.Update(s => s.Y70.Brightness = pct);
    }

    public bool GetToggle() => _store.Load().Y70.ScreenOff;

    public void SetToggle(bool screenOff)
    {
        DriveScreenPower(!screenOff);
        _store.Update(s => s.Y70.ScreenOff = screenOff);
    }

    /// <summary>Drives screen power without persisting; screenOff=false restores the stored preference, so a panel the user switched off by hand stays off.</summary>
    public void SetGameModeScreenOff(bool screenOff)
    {
        DriveScreenPower(!screenOff && !_store.Load().Y70.ScreenOff);
    }

    private void DriveScreenPower(bool screenOn)
    {
        if (TouchVariantKnown)
        {
            // Reference Y70TouchDevice.TurnScreenBrightnessOff: power rides
            // the serial frame with the PWM pinned (full when on, zero when
            // off); on screen-on the perceived brightness is restored through
            // the gain registers, floored to the reference minimum.
            // SetBrightnessPower runs its own EnsureConnected, so this branch
            // keys on the identified variant; DDC power is the fallback only
            // while the serial link is down.
            var ok = _hub.SetBrightnessPower(screenOn, screenOn ? 100 : 0);
            LogApply($"transport=touch-serial on={screenOn} ok={ok}");
            if (!screenOn && !ok)
            {
                DdcSetPower(false);
                _touchDdcStandbyFallback = true;
            }
            if (screenOn)
            {
                // Serial power-on does not wake the monitor from a DDC
                // standby set while the link was down; clear it explicitly.
                if (!ok || _touchDdcStandbyFallback)
                {
                    DdcSetPower(true);
                    _touchDdcStandbyFallback = false;
                }
                DdcSetBrightnessCoalesced(Math.Max(_store.Load().Y70.Brightness, Y70DisplayProtocol.MinBrightnessOnPercent));
            }
        }
        else if (SerialTransportSelected)
        {
            ApplySerial(screenOn, _store.Load().Y70.Brightness);
        }
        else
        {
            DdcSetPower(screenOn);
        }
    }

    // Set when a Touch screen-off fell back to DDC standby (serial link
    // down). Toggles are user-paced; a racing read at worst costs one extra
    // or one deferred DDC power-on, self-healing on the next toggle. Lost on
    // service restart - a monitor left in fallback standby then needs an
    // off/on cycle after the serial link returns.
    private bool _touchDdcStandbyFallback;

    public bool IsRotated()
    {
        var o = GetOrientation();
        return o == "Portrait" || o == "PortraitFlipped";
    }

    /// <summary>
    /// True when the plain serial FF CC transport drives this panel. A Truly
    /// enumerates the same CDC port as the serial models (the shared FF DD
    /// version query answers, so the hub connects) but its firmware takes
    /// display control only via DDC/CI - the serial FF CC frame is a silent
    /// no-op there, so a connected hub alone must not select serial. The
    /// original Touch takes the hybrid path instead.
    /// </summary>
    private bool SerialTransportSelected =>
        _hub.IsConnected
        && _hub.Variant != Y70DisplayProtocol.VariantTruly
        && _hub.Variant != Y70DisplayProtocol.VariantTouch;

    private bool TouchTransportSelected =>
        _hub.IsConnected && _hub.Variant == Y70DisplayProtocol.VariantTouch;

    /// <summary>
    /// True once a Touch has ever identified over serial (Variant survives a
    /// Disconnect). The DDC register-set choice keys on this, not on
    /// IsConnected: a Touch with its serial link transiently down must still
    /// dim through the gain registers - a raw VCP 0x10 write lowers the
    /// scaler luminance underneath the gain path and nothing restores it.
    /// </summary>
    private bool TouchVariantKnown => _hub.Variant == Y70DisplayProtocol.VariantTouch;

    private void ApplySerial(bool screenOn, int pct)
    {
        // Firmware acts on the percentage (backlight) byte: screen-off must
        // send 0; a nonzero percentage keeps the panel lit even with the off
        // flag set. Screen-on floors to the firmware's minimum. Matches legacy
        // Y70TouchInfiniteController: off => SetCurrentBrightness(false, 0).
        var effective = screenOn ? Math.Max(pct, Y70DisplayProtocol.MinBrightnessOnPercent) : 0;
        var ok = _hub.SetBrightnessPower(screenOn, effective);
        LogApply($"transport=serial ok={ok}");
    }

    // Reference Y70DDCCIHelper gates DDC writes to >=100ms intervals
    // (SETTING_DEBOUNCE_TIME); the brightness slider posts per drag tick and a
    // DDC/CI write is slow, so in-window writes collapse to one trailing write
    // of the latest value. Power toggles are rare and write immediately. The
    // Touch RGB-gain apply is three VCP writes, so the reference triples the
    // interval for it (SETTING_DEBOUNCE_TIME * 3).
    private const int DdcWriteIntervalMs = 100;
    private const int DdcRgbWriteIntervalMs = 300;
    // Guards the pending/last/timer fields; never held across the pipe RPC.
    private readonly object _ddcStateLock = new();
    // Serializes the actual RPC writes so overlapped route threads cannot
    // land out of order; each writer sends the latest pending value, so a
    // stale thread at worst duplicates the newest write.
    private readonly object _ddcRpcLock = new();
    // TickCount64 is >= 0, so -interval keeps the first write immediate
    // (long.MinValue would wrap the subtraction below negative).
    private long _lastDdcBrightnessWriteAt = -DdcWriteIntervalMs;
    private int _pendingDdcBrightness;
    private Timer? _ddcBrightnessFlush;
    private bool _disposed;

    private void DdcSetBrightnessCoalesced(int pct)
    {
        var interval = TouchVariantKnown ? DdcRgbWriteIntervalMs : DdcWriteIntervalMs;
        lock (_ddcStateLock)
        {
            if (_disposed) return;
            _pendingDdcBrightness = pct;
            var now = Environment.TickCount64;
            if (now - _lastDdcBrightnessWriteAt < interval)
            {
                _ddcBrightnessFlush ??= new Timer(_ => WritePendingDdcBrightness(), null, Timeout.Infinite, Timeout.Infinite);
                _ddcBrightnessFlush.Change(interval, Timeout.Infinite);
                return;
            }
            // Pending is written below; a still-armed trailing flush would
            // only duplicate it inside the write interval.
            _ddcBrightnessFlush?.Change(Timeout.Infinite, Timeout.Infinite);
            _lastDdcBrightnessWriteAt = now;
        }
        WritePendingDdcBrightness();
    }

    /// <summary>
    /// Immediate path and timer flush both land here. Never throws: the
    /// helper pipe RPC surfaces IOException / ObjectDisposedException across
    /// a helper restart, and an unhandled exception in a Timer callback is
    /// process-fatal.
    /// </summary>
    private void WritePendingDdcBrightness()
    {
        try
        {
            lock (_ddcRpcLock)
            {
                int pct;
                lock (_ddcStateLock)
                {
                    pct = _pendingDdcBrightness;
                    _lastDdcBrightnessWriteAt = Environment.TickCount64;
                }
                if (TouchVariantKnown) DdcWriteRgbBrightness(pct);
                else DdcWriteBrightness(pct);
            }
        }
        catch (Exception ex)
        {
            LogApply($"transport=ddc error {ex.GetType().Name}: {ex.Message}");
        }
    }

    // DDC/CI path (Truly / GW). UNVERIFIED - see class remarks. Brightness is
    // written raw (no MinBrightnessOnPercent floor) and independently of the
    // power state, matching the reference Y70TouchDdcciDeviceBase.
    private void DdcWriteBrightness(int pct)
    {
        var id = DdcDisplayId();
        if (id is null)
        {
            LogApply("transport=none (no reachable display; store-only)");
            return;
        }
        var ok = _ddc.SetVcp(id, Y70DisplayProtocol.VcpBrightness, pct);
        if (!ok) InvalidateDdcId(id);
        LogApply($"transport=ddc id={id} vcp=brightness ok={ok}");
    }

    // Touch RGB-gain path. UNVERIFIED - see class remarks. The same mapped
    // value goes to all three channels; & (not &&) so every register is
    // written even after one failure.
    private void DdcWriteRgbBrightness(int pct)
    {
        var id = DdcDisplayId();
        if (id is null)
        {
            LogApply("transport=none (no reachable display; store-only)");
            return;
        }
        EnsureTouchPrepared(id);
        var gain = Y70DisplayProtocol.TouchRgbGainForPercent(pct);
        var ok = _ddc.SetVcp(id, Y70DisplayProtocol.VcpVideoGainRed, gain)
               & _ddc.SetVcp(id, Y70DisplayProtocol.VcpVideoGainGreen, gain)
               & _ddc.SetVcp(id, Y70DisplayProtocol.VcpVideoGainBlue, gain);
        if (!ok) InvalidateDdcId(id);
        LogApply($"transport=ddc-rgb id={id} gain={gain} ok={ok}");
    }

    // Prep state, all mutated only under _ddcRpcLock (EnsureTouchPrepared's
    // callers hold it). Preset and PWM pin latch independently and only on a
    // successful write/read: SetVcp reports failure by returning false (the
    // helper proxy maps no-connection/timeout/NACK to false, it does not
    // throw), and the pin's serial read can fail transiently - latching
    // either on failure would leave the gains unresponsive or the panel
    // humming for the whole connection. A failed attempt costs up to the RPC
    // or serial timeout, so retries per connection are bounded; a reconnect
    // starts a new epoch and a fresh set.
    private int _touchPresetEpoch = -1;
    private int _touchPresetAttemptEpoch = -1;
    private int _touchPresetAttempts;
    private int _touchPinEpoch = -1;
    private int _touchPinAttemptEpoch = -1;
    private int _touchPinAttempts;
    private const int TouchPrepMaxAttempts = 3;

    /// <summary>
    /// One-time per-connection Touch prep, reference Y70TouchDevice init:
    /// switch the monitor to UserDefine3 so the gain registers respond, and
    /// pin the STM32 backlight PWM to 100% - but only when the screen is
    /// currently on below full, so an intentionally-off screen is not woken.
    /// Re-runs when the hub's connection epoch changes (replug resets the
    /// STM32 and monitor state).
    /// </summary>
    private void EnsureTouchPrepared(string ddcId)
    {
        var epoch = _hub.ConnectionEpoch;
        if (_touchPresetEpoch != epoch)
        {
            if (_touchPresetAttemptEpoch != epoch)
            {
                _touchPresetAttemptEpoch = epoch;
                _touchPresetAttempts = 0;
            }
            if (_touchPresetAttempts < TouchPrepMaxAttempts)
            {
                _touchPresetAttempts++;
                if (_ddc.SetVcp(ddcId, Y70DisplayProtocol.VcpColorPresetMode, Y70DisplayProtocol.VcpColorPresetUserDefine3))
                {
                    _touchPresetEpoch = epoch;
                }
            }
        }

        if (_touchPinEpoch == epoch) return;
        if (_touchPinAttemptEpoch != epoch)
        {
            _touchPinAttemptEpoch = epoch;
            _touchPinAttempts = 0;
        }
        if (_touchPinAttempts >= TouchPrepMaxAttempts) return;
        _touchPinAttempts++;
        if (!_hub.TryReadScreenInfo(out _, out var pwm)) return;
        if (pwm is > 0 and < 100)
        {
            _hub.SetBrightnessPower(true, 100);
        }
        _touchPinEpoch = epoch;
        ServiceLog.Info($"[y70-display] touch rgb prep done (epoch={epoch} pwm={pwm})");
    }

    private void DdcSetPower(bool screenOn)
    {
        try
        {
            lock (_ddcRpcLock)
            {
                var id = DdcDisplayId();
                if (id is null)
                {
                    LogApply("transport=none (no reachable display; store-only)");
                    return;
                }
                var ok = _ddc.SetVcp(id, Y70DisplayProtocol.VcpPower, screenOn ? Y70DisplayProtocol.VcpPowerOn : Y70DisplayProtocol.VcpPowerStandby);
                if (!ok) InvalidateDdcId(id);
                LogApply($"transport=ddc id={id} vcp=power on={screenOn} ok={ok}");
            }
        }
        catch (Exception ex)
        {
            LogApply($"transport=ddc error {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The brightness slider posts on every drag tick; log transitions only so
    // a drag emits one line while transport/result changes stay visible.
    private string? _lastApplyLog;

    private void LogApply(string detail)
    {
        if (detail == _lastApplyLog) return;
        _lastApplyLog = detail;
        ServiceLog.Info($"[y70-display] apply {detail}");
    }

    private void InvalidateDdcId(string id)
    {
        lock (_ddcIdLock)
        {
            if (_ddcId == id)
                _ddcIdChecked = false;
        }
    }

    private string? DdcDisplayId(bool reuseMiss = false)
    {
        if (!LimitDdcScans)
            return _ddc.FindDisplayIdByHardwareName(Y70DisplayProtocol.DdcPanelHardwareNames);
        lock (_ddcIdLock)
        {
            var now = Environment.TickCount64;
            // A cached miss serves only the heartbeat; a write retries so a freshly attached display is reachable at once.
            var reusable = _ddcIdChecked && now - _ddcIdCheckedAt < DdcLookupTtlMs && (_ddcId is not null || reuseMiss);
            if (!reusable)
            {
                _ddcId = _ddc.FindDisplayIdByHardwareName(Y70DisplayProtocol.DdcPanelHardwareNames);
                _ddcIdCheckedAt = now;
                _ddcIdChecked = true;
            }
            return _ddcId;
        }
    }

    public void Dispose()
    {
        lock (_ddcStateLock)
        {
            _disposed = true;
            _ddcBrightnessFlush?.Dispose();
            _ddcBrightnessFlush = null;
        }
    }
}
