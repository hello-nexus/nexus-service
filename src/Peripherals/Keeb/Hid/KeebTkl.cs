using System;
using System.Collections.Generic;
using System.Threading;
using Qos.Service.Peripherals.Hid;
using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.KeyAssignment;
using Qos.Service.Peripherals.Keeb.Hid.Layout;
using Qos.Service.Peripherals.Keeb.Hid.Macros;
using Qos.Service.Peripherals.Keeb.Hid.Settings;

namespace Qos.Service.Peripherals.Keeb.Hid;

/// <summary>
/// HYTE Keeb TKL device session. Owns the HID command channel, an in-memory
/// cache of the firmware key map (per profile, per layer) and macro slots, and
/// the firmware-settings snapshot. Hot-swap is handled at the layer above
/// (<c>KeebHotswapHost</c>); this class is constructed once per connected
/// device and disposed on disconnect.
///
/// Profile model: the firmware exposes two profiles (0, 1); qos pins firmware
/// to profile 0 on session start because qos's own profile system already
/// gives the user the equivalent. The second firmware profile is still
/// fetched on init so a future write to it would round-trip correctly, but
/// it's never selected during normal operation.
/// </summary>
public sealed class KeebTkl : IDisposable
{
    private const int PROFILE_COUNT = 2;
    private const int LAYER_COUNT = 4;
    private const int MACRO_COUNT_PER_PROFILE = 16;

    private readonly object _locker = new();
    private readonly KeebTklCommand _command;
    private readonly Profile[] _profiles;
    private readonly KeebSettings _settings = new();
    private bool _disposed;

    public Action<KeebLayoutModel>? KeyPressed { get; set; }
    public Action<ScrollWheelsMode, byte?, byte?>? ScrollWheelChanged { get; set; }
    public Action<byte>? SoftwareKeyClicked { get; set; }

    public KeebLayout Layout { get; }
    public int CurrentProfile { get; private set; }
    public string FirmwareVersion { get; }
    public KeebSettings Settings => _settings;
    public Profile[] Profiles => _profiles;

    public KeebTkl(IHidDevice featureInterface, IHidDevice inputInterface)
    {
        _command = new KeebTklCommand(featureInterface, inputInterface);
        _command.KeyPressInvoke = m => KeyPressed?.Invoke(m);
        _command.ScrollWheelInvoke = m => ScrollWheelChanged?.Invoke(m, null, null);
        _command.SoftwareKeyInvoke = b => SoftwareKeyClicked?.Invoke(b);

        // Read FW version + layout from the handshake.
        var info = _command.GetDeviceInfo();
        FirmwareVersion = $"0.{info[6]:X}.{info[5]:X2}.1";
        Layout = info[7] is (byte)KeebLayout.ANSI or (byte)KeebLayout.ISO
            ? (KeebLayout)info[7]
            : KeebLayout.ANSI;

        // Seed in-memory settings + profile/layer/macro caches.
        ReadSettings();
        _profiles = new Profile[PROFILE_COUNT] { new(0, Layout), new(1, Layout) };
        CurrentProfile = _command.GetCurrentProfileIndex();

        for (int profile = 0; profile < PROFILE_COUNT; profile++)
        {
            for (int layer = 0; layer < LAYER_COUNT; layer++)
            {
                ReadLayerFromFirmware(profile, layer);
            }
            for (int macroIdx = 0; macroIdx < MACRO_COUNT_PER_PROFILE; macroIdx++)
            {
                ReadMacroFromFirmware(profile, macroIdx);
            }
        }

        // Pin firmware to profile 0 — qos profiles supersede the firmware's two
        // profiles; we always drive profile 0 from here on.
        if (CurrentProfile != 0)
        {
            _command.SelectCurrentProfile(0);
            CurrentProfile = _command.GetCurrentProfileIndex();
        }
    }

    #region Settings

    public void ReadSettings()
    {
        lock (_locker)
        {
            var bytes = _command.GetSettings();
            // Firmware returns a zeroed page if it has never had settings written;
            // in that case write defaults back so the next read is sane.
            if (bytes[3] == 0)
            {
                var defaults = _settings.GetCommands();
                _command.SetSettings(defaults);
                _settings.GetSettingFromCommands(defaults);
                return;
            }
            _settings.GetSettingFromCommands(bytes);
        }
    }

    private void WriteSettings()
    {
        lock (_locker)
        {
            _command.SetSettings(_settings.GetCommands());
        }
    }

    public void ChangeFwAnimationMode(FwAnimationMode mode)
    {
        _settings.CurrentFwAnimationSetting.ChangeFwAnimationMode(mode);
        _settings.SetLedOnOff(true);
        WriteSettings();
        ReadSettings();
    }

    public void ChangeFwAnimationSpeed(FwAnimationSpeed speed)
    {
        _settings.CurrentFwAnimationSetting.ChangeSpeed(speed);
        WriteSettings();
        ReadSettings();
    }

    public void ChangeFwAnimationDirection(FwAnimationDirection direction)
    {
        _settings.CurrentFwAnimationSetting.ChangeDirection(direction);
        WriteSettings();
        ReadSettings();
    }

    public void ChangeFwAnimationBrightness(int percentage)
    {
        _settings.SetLedBrightness(percentage);
        WriteSettings();
        ReadSettings();
    }

    public void ChangeGameMode(bool windowsKey, bool shiftTab, bool altF4, bool altTab)
    {
        _settings.KeebGameMode.SetGameMode(windowsKey, shiftTab, altF4, altTab);
        WriteSettings();
        ReadSettings();
    }

    public void ChangeScrollwheelSensitivity(int delayMs)
    {
        _command.ChangeScrollwheelSensitivity(delayMs);
    }

    #endregion

    #region Layer key assignment

    public void ChangeKey(int profile, int layer, int x, int y, Key key)
    {
        lock (_locker)
        {
            _profiles[profile].ChangeLayerKeyMap(layer, x, y, key);
            var commands = _profiles[profile].KeyLayers[layer].GetCurrentKeyMapCommands();
            _command.SetLayerKeyAssignment(profile, layer, commands);
            Thread.Sleep(50);
            ReadLayerFromFirmware(profile, layer);
        }
    }

    public void SetLayerToDefault(int profile, int layer)
    {
        lock (_locker)
        {
            var commands = _profiles[profile].KeyLayers[layer].GetDefaultKeyMapCommands(layer);
            _command.SetLayerKeyAssignment(profile, layer, commands);
            Thread.Sleep(50);
            ReadLayerFromFirmware(profile, layer);
        }
    }

    private void ReadLayerFromFirmware(int profile, int layer)
    {
        var bytes = _command.GetLayerKeyAssignment(profile, layer);
        _profiles[profile].GetLayerFromFw(layer, bytes);
    }

    public Key[][] GetLayerMapping(int profile, int layer)
    {
        return _profiles[profile].KeyLayers[layer].CurrentMapping;
    }

    #endregion

    #region Macros

    public Macro GetMacro(int profile, int macroIdx)
    {
        return _profiles[profile].Macros[macroIdx];
    }

    public void SetMacro(int profile, int macroIdx, Macro macro)
    {
        lock (_locker)
        {
            var slot = profile * MACRO_COUNT_PER_PROFILE + macroIdx;
            _command.SetMacro(slot, macro.Commands?.ToArray() ?? Array.Empty<byte>());
            Thread.Sleep(50);
            ReadMacroFromFirmware(profile, macroIdx);
        }
    }

    private void ReadMacroFromFirmware(int profile, int macroIdx)
    {
        var slot = profile * MACRO_COUNT_PER_PROFILE + macroIdx;
        var bytes = _command.GetMacro(slot);
        _profiles[profile].WriteMacroInProfile(macroIdx, new Macro(macroIdx, bytes));
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _command.Dispose();
    }
}
