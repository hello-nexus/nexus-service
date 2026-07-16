using System.Collections.Generic;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// No-hardware <see cref="IKeebProvider"/> (non-Windows platforms and the
/// <see cref="IInputterProvider"/> fallback). Settings and macros persist as
/// desired state; anything that requires the physical keyboard - key
/// assignment, onboard verification - reports honestly that no device is
/// connected instead of pretending.
/// </summary>
public sealed class StubKeebProvider : IKeebProvider, IInputterProvider
{
    private readonly IConfigStore _store;

    public StubKeebProvider(IConfigStore store) { _store = store; }

    public KeyboardState GetState(int layer) => new()
    {
        IsConnected = false,
        Profile = 0,
        Layer = layer,
        Layout = "ANSI",
        Keys = new List<List<KeebKey>>(),
    };

    public GetKeebSettingsResponse GetSettings()
    {
        var k = _store.Load().Keeb;
        return new()
        {
            AltF4Disabled = k.GameMode.AltF4,
            AltTabDisabled = k.GameMode.AltTab,
            ShiftKeyDisabled = k.GameMode.ShiftTab,
            WindowsKeyDisabled = k.GameMode.WindowsKey,
            RotaryLeft = k.RotaryLeft,
            RotaryRight = k.RotaryRight,
            AnimationMode = k.FirmwareLighting.AnimationMode,
            Speed = k.FirmwareLighting.Speed,
            Direction = k.FirmwareLighting.Direction,
            Brightness = k.FirmwareLighting.Brightness,
            KeyReactive = k.FirmwareLighting.KeyReactive,
            KeyReactiveMask = k.FirmwareLighting.KeyReactiveMask,
            KeyReactiveMode = k.FirmwareLighting.KeyReactiveMode,
            KeyReactiveColor = new RGBA
            {
                R = k.FirmwareLighting.KeyReactiveColor.R,
                G = k.FirmwareLighting.KeyReactiveColor.G,
                B = k.FirmwareLighting.KeyReactiveColor.B,
                A = k.FirmwareLighting.KeyReactiveColor.A,
            },
        };
    }

    public string[] GetRotaryFunctions() => KeebSettingsCodec.RotaryFunctions;

    public void SetRotary(SetRotaryWheelsBody body) => _store.Update(s =>
    {
        s.Keeb.RotaryLeft = body.Left;
        s.Keeb.RotaryRight = body.Right;
    });

    public void SetFirmwareLighting(SetFirmwareLightingBody body) => _store.Update(s =>
    {
        s.Keeb.FirmwareLighting.AnimationMode = body.AnimationMode;
        s.Keeb.FirmwareLighting.Speed = body.Speed;
        s.Keeb.FirmwareLighting.Direction = body.Direction;
        s.Keeb.FirmwareLighting.Brightness = body.Brightness;
    });

    public void SetPassiveLighting(SetPassiveLightingBody body) => _store.Update(s =>
    {
        s.Keeb.FirmwareLighting.KeyReactive = body.KeyReactive;
        s.Keeb.FirmwareLighting.KeyReactiveMask = body.KeyReactiveMask;
        s.Keeb.FirmwareLighting.KeyReactiveMode = body.KeyReactiveMode;
        s.Keeb.FirmwareLighting.KeyReactiveColor = new RgbaColor
        {
            R = body.KeyReactiveColor.R,
            G = body.KeyReactiveColor.G,
            B = body.KeyReactiveColor.B,
            A = body.KeyReactiveColor.A,
        };
    });

    public void SetGameMode(SetGameModeBody body) => _store.Update(s =>
    {
        s.Keeb.GameMode.AltF4 = body.AltF4;
        s.Keeb.GameMode.AltTab = body.AltTab;
        s.Keeb.GameMode.ShiftTab = body.ShiftTab;
        s.Keeb.GameMode.WindowsKey = body.WindowsKey;
    });

    public KeebMacro GetMacro(int index)
    {
        var s = _store.Load();
        if (s.Keeb.Macros.TryGetValue(index, out var doc)) return Convert(doc, index);
        return new() { Index = index };
    }

    public SetMacroResponse SetMacro(int index, SetMacroBody body)
    {
        var doc = new KeebMacroDocument { Index = index };
        foreach (var k in body.Keys ?? new List<MacroKey>())
        {
            doc.Keys.Add(new Persistence.KeebMacroKey
            {
                Key = k.Key,
                Duration = k.Duration,
                Type = k.Type,
            });
        }
        var built = KeebMacroCodec.Build(doc);
        _store.Update(s => s.Keeb.Macros[index] = doc);
        return new SetMacroResponse
        {
            Macro = GetMacro(index),
            Truncated = built.Truncated,
            DroppedKeys = built.DroppedKeys.ToArray(),
            WroteDevice = false,
        };
    }

    public SetLayerKeyResponse SetLayerKey(int layer, SetLayerKeyBody body)
        => new() { Error = true, Msg = "keyboard is not connected", WroteDevice = false };

    public SetLayerKeyResponse ResetLayer(int layer)
        => new() { Error = true, Msg = "keyboard is not connected", WroteDevice = false };

    public void ApplyPersistedAssignments()
    {
    }

    public void Send(InputterBody body)
    {
    }

    private static KeebMacro Convert(KeebMacroDocument doc, int index)
    {
        var m = new KeebMacro { Index = index };
        foreach (var k in doc.Keys)
        {
            m.Keys.Add(new MacroKey
            {
                Key = k.Key,
                Duration = k.Duration,
                Type = k.Type,
            });
        }
        return m;
    }
}
