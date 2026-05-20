using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Models.Common;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Peripherals.Keeb.Hid;
using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.KeyAssignment;
using Qos.Service.Peripherals.Keeb.Hid.Macros;
using Qos.Service.Persistence;

namespace Qos.Service.Peripherals.Keeb;

/// <summary>
/// Real <see cref="IKeebProvider"/> implementation backed by an open
/// <see cref="KeebSession"/>. Falls through to the persistence layer when the
/// keeb isn't attached so the UI can still render its offline state with
/// whatever was last persisted.
///
/// When a keeb IS attached, every write hits both the firmware (via
/// <see cref="KeebTkl"/>) and the persisted snapshot (via <see cref="IConfigStore"/>)
/// so a fresh boot can resync.
/// </summary>
public sealed class HidKeebProvider : IKeebProvider
{
    private const int PROFILE = 0; // qos pins firmware to profile 0; see KeebTkl

    private readonly KeebSession _session;
    private readonly IConfigStore _store;
    private readonly DeviceManager _devices;
    private readonly StubKeebProvider _fallback;

    public HidKeebProvider(KeebSession session, IConfigStore store, DeviceManager devices, StubKeebProvider fallback)
    {
        _session = session;
        _store = store;
        _devices = devices;
        _fallback = fallback;
    }

    private KeebTkl? Keeb => _session.Keeb;

    private bool IsConnected() => _devices.GetAll().Any(d => d.Id == "keeb" && d.Connected);

    public KeyboardState GetState(int layer = 0)
    {
        var keeb = Keeb;
        if (keeb is null)
        {
            return _fallback.GetState(layer);
        }
        return new KeyboardState
        {
            IsConnected = true,
            Profile = 0,
            Layout = keeb.Layout.ToString(),
            Layer = layer,
            Keys = GetLayer(layer),
        };
    }

    public List<List<KeebKey>> GetLayer(int layer)
    {
        var keeb = Keeb;
        if (keeb is null) return _fallback.GetLayer(layer);

        var mapping = keeb.GetLayerMapping(PROFILE, layer);
        return mapping
            .Select(row => row.Select(k => new KeebKey
            {
                Function = k.KeyFunction.ToString(),
                Mode = k.Mode.ToString(),
                Input = ExtractInput(k),
            }).ToList())
            .ToList();
    }

    public bool SetLayerKey(int layer, SetLayerKeyBody body)
    {
        // Always cache the change in the persisted snapshot — that's our
        // offline-render source of truth.
        _fallback.SetLayerKey(layer, body);

        var keeb = Keeb;
        if (keeb is null) return false;

        if (!System.Enum.TryParse<KeyFunction>(body.Func, out var func)) return false;
        if (!System.Enum.TryParse<KeyAssignmentMode>(body.Mode, out var mode)) return false;

        byte[]? inputs = null;
        var model = KeyFunctionLibrary.Library[mode].Models[func];
        if (model.DataTypes != null && model.DataTypes.Length > 0)
        {
            inputs = new byte[model.DataTypes.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = (byte)(body.Input ?? 0);
            }
        }

        keeb.ChangeKey(PROFILE, layer, body.X, body.Y, new Key(func, inputs, PROFILE));
        return true;
    }

    public bool ResetLayer(int layer)
    {
        _fallback.ResetLayer(layer);

        var keeb = Keeb;
        if (keeb is null) return false;

        keeb.SetLayerToDefault(PROFILE, layer);
        return true;
    }

    public GetKeebSettingsResponse GetSettings()
    {
        var keeb = Keeb;
        if (keeb is null) return _fallback.GetSettings();

        keeb.ReadSettings();
        var s = keeb.Settings;
        // Passive lighting stays on persistence — it isn't part of the firmware
        // settings page; the legacy app writes it through a separate code path
        // (key-reactive animation) which lives in the streaming engine.
        var persisted = _fallback.GetSettings();
        return new GetKeebSettingsResponse
        {
            AltF4Disabled = s.KeebGameMode.IsAltF4KeyOff,
            AltTabDisabled = s.KeebGameMode.IsAltTabKeyOff,
            ShiftKeyDisabled = s.KeebGameMode.IsShiftTabKeyOff,
            WindowsKeyDisabled = s.KeebGameMode.IsWindowsKeyOff,
            AnimationMode = s.CurrentFwAnimationSetting.AnimationMode.ToString(),
            Speed = s.CurrentFwAnimationSetting.LedSpeed.ToString(),
            Direction = s.CurrentFwAnimationSetting.LedDirection.ToString(),
            Brightness = s.LedBrightnessPercentage,
            KeyIndicator = persisted.KeyIndicator,
            KeyReactive = persisted.KeyReactive,
            KeyReactiveMask = persisted.KeyReactiveMask,
            KeyReactiveMode = persisted.KeyReactiveMode,
            KeyReactiveColor = persisted.KeyReactiveColor,
        };
    }

    public string[] GetRotaryFunctions() => _fallback.GetRotaryFunctions();

    public void SetRotary(SetRotaryWheelsBody body)
    {
        _fallback.SetRotary(body);
        // Wheel rotation handling lives in qos-side software actions, not in
        // the firmware. The firmware-level rotary mode is set on KeebSettings
        // when the session opens; the user-configured action mapping is
        // executed in the qos process when key events arrive.
    }

    public void SetRotarySensitivity(string sensitivity)
    {
        _fallback.SetRotarySensitivity(sensitivity);

        var keeb = Keeb;
        if (keeb is null) return;

        var ms = sensitivity switch
        {
            "Slow" => 200,
            "Steady" => 150,
            "Balanced" => 100,
            "Fast" => 50,
            "Turbo" => 25,
            _ => 100,
        };
        keeb.ChangeScrollwheelSensitivity(ms);
    }

    public void SetPassiveLighting(SetPassiveLightingBody body)
    {
        // Persistence only — the streaming engine (qos-rgb integration) reads
        // these on its next frame. Firmware doesn't have a separate "passive
        // reactive" register; it's a software effect we composite over the
        // streaming output.
        _fallback.SetPassiveLighting(body);
    }

    public void SetFirmwareLighting(SetFirmwareLightingBody body)
    {
        _fallback.SetFirmwareLighting(body);

        var keeb = Keeb;
        if (keeb is null) return;

        if (System.Enum.TryParse<FwAnimationMode>(body.AnimationMode, out var mode))
            keeb.ChangeFwAnimationMode(mode);
        if (System.Enum.TryParse<FwAnimationSpeed>(body.Speed, out var speed))
            keeb.ChangeFwAnimationSpeed(speed);
        if (System.Enum.TryParse<FwAnimationDirection>(body.Direction, out var dir))
            keeb.ChangeFwAnimationDirection(dir);
        keeb.ChangeFwAnimationBrightness(body.Brightness);
    }

    public void SetGameMode(SetGameModeBody body)
    {
        _fallback.SetGameMode(body);

        var keeb = Keeb;
        if (keeb is null) return;
        keeb.ChangeGameMode(body.WindowsKey, body.ShiftTab, body.AltF4, body.AltTab);
    }

    public KeebMacro GetMacro(int index)
    {
        var keeb = Keeb;
        if (keeb is null) return _fallback.GetMacro(index);

        var m = keeb.GetMacro(PROFILE, index);
        if (m is null) return new KeebMacro { Index = index };

        var dto = new KeebMacro { Index = index };
        foreach (var kc in m.Keys)
        {
            dto.Keys.Add(new MacroKey
            {
                Key = kc.ThisKey.ToString(),
                Type = kc.ThisKeyStatus.ToString(),
                Duration = kc.Duration * 10, // firmware stores in 10ms units; surface as wall ms
                Category = kc.Category.ToString(),
            });
        }
        return dto;
    }

    public KeebMacro SetMacro(int index, SetMacroBody body)
    {
        _fallback.SetMacro(index, body);

        var keeb = Keeb;
        if (keeb is null) return _fallback.GetMacro(index);

        var keyCodes = new List<KeyCode>(body.Keys.Count);
        foreach (var k in body.Keys)
        {
            if (!System.Enum.TryParse<KeyStatus>(k.Type, out var status)) continue;
            if (!System.Enum.TryParse<MacroKeyCode>(k.Key, out var code)) continue;
            keyCodes.Add(new KeyCode(status, k.Duration / 10, code));
        }
        keeb.SetMacro(PROFILE, index, new Macro(index, 1, keyCodes));
        return GetMacro(index);
    }

    private static int? ExtractInput(Key k)
    {
        var model = KeyFunctionLibrary.Library[k.Mode].Models[k.KeyFunction];
        if (model.DataTypes is null || model.DataTypes.Length == 0) return null;
        var commands = k.Commands;
        if (commands is null || model.ParameterByteIndexs is null || model.ParameterByteIndexs.Length == 0) return null;
        return commands[model.ParameterByteIndexs[0]];
    }
}
