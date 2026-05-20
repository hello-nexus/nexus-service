using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Models.Common;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Persistence;

namespace Qos.Service.Peripherals.Keeb;

/// <summary>
/// Persistence-only Keeb provider: every setter writes through to <see cref="IConfigStore"/>.
/// Reads use the persisted snapshot. Connectivity is reported via <see cref="DeviceManager"/>
/// so the UI can render the offline state correctly even before the HID driver port lands.
///
/// When the real driver is wired in later, it replaces this class — the route surface and
/// DTO shapes are stable.
/// </summary>
public sealed class StubKeebProvider : IKeebProvider, IInputterProvider
{
    private readonly IConfigStore _store;
    private readonly DeviceManager _devices;

    public StubKeebProvider(IConfigStore store, DeviceManager devices)
    {
        _store = store;
        _devices = devices;
    }

    private bool IsConnected() =>
        _devices.GetAll().Any(d => d.Id == "keeb" && d.Connected);

    public KeyboardState GetState(int layer = 0) => new()
    {
        IsConnected = IsConnected(),
        Profile = 0,
        Layout = "ANSI",
        Layer = layer,
        Keys = GetLayer(layer),
    };

    public List<List<KeebKey>> GetLayer(int layer)
    {
        var s = _store.Load();
        if (s.Keeb.Layers.TryGetValue(layer, out var snapshot))
        {
            return snapshot.Keys
                .Select(row => row.Select(k => new KeebKey { Mode = k.Mode, Function = k.Function, Input = k.Input }).ToList())
                .ToList();
        }
        return new List<List<KeebKey>>();
    }

    public bool SetLayerKey(int layer, SetLayerKeyBody body)
    {
        // No firmware to write to yet. Cache in the snapshot so the UI round-trips
        // the change locally; once the driver lands, this becomes a fallback.
        _store.Update(s =>
        {
            if (!s.Keeb.Layers.TryGetValue(layer, out var snap))
            {
                snap = new KeebLayerSnapshot();
                s.Keeb.Layers[layer] = snap;
            }
            while (snap.Keys.Count <= body.X) snap.Keys.Add(new List<KeebLayerKey>());
            while (snap.Keys[body.X].Count <= body.Y) snap.Keys[body.X].Add(new KeebLayerKey());
            snap.Keys[body.X][body.Y] = new KeebLayerKey
            {
                Function = body.Func,
                Mode = body.Mode,
                Input = body.Input,
            };
        });
        return IsConnected();
    }

    public bool ResetLayer(int layer)
    {
        _store.Update(s => s.Keeb.Layers.Remove(layer));
        return IsConnected();
    }

    public GetKeebSettingsResponse GetSettings()
    {
        var k = _store.Load().Keeb;
        return new()
        {
            AltF4Disabled = k.GameMode.AltF4,
            AltTabDisabled = k.GameMode.AltTab,
            ShiftKeyDisabled = k.GameMode.ShiftTab,
            WindowsKeyDisabled = k.GameMode.WindowsKey,
            AnimationMode = k.FirmwareLighting.AnimationMode,
            Speed = k.FirmwareLighting.Speed,
            Direction = k.FirmwareLighting.Direction,
            Brightness = k.FirmwareLighting.Brightness,
            KeyIndicator = k.FirmwareLighting.KeyIndicator,
            KeyReactive = k.PassiveLighting.KeyReactive,
            KeyReactiveMask = k.PassiveLighting.KeyReactiveMask,
            KeyReactiveMode = k.PassiveLighting.KeyReactiveMode,
            KeyReactiveColor = new RGBA
            {
                R = k.PassiveLighting.KeyReactiveColor.R,
                G = k.PassiveLighting.KeyReactiveColor.G,
                B = k.PassiveLighting.KeyReactiveColor.B,
                A = k.PassiveLighting.KeyReactiveColor.A,
            },
        };
    }

    public string[] GetRotaryFunctions() => new[]
    {
        "VolumeAdjustment", "BrightnessAdjustment", "Scale", "AltTab", "CtrlTab",
        "ScrollX", "ScrollY", "WaveAdjustment", "ScrubAdobeTimeline", "ScrollAdobeTimeline",
        "AdobeBrushSize", "ScrollAdobeToolList", "UndoOrRedo", "MediaForwardsOrBackwards",
        "Q60PageControl",
    };

    public void SetRotary(SetRotaryWheelsBody body) => _store.Update(s =>
    {
        s.Keeb.RotaryLeft = body.Left;
        s.Keeb.RotaryRight = body.Right;
        s.Keeb.RotaryApps.Clear();
        foreach (var a in body.Apps)
        {
            s.Keeb.RotaryApps.Add(new KeebRotaryAppOverride { TargetId = a.TargetId, Left = a.Left, Right = a.Right });
        }
    });

    public void SetRotarySensitivity(string sensitivity) =>
        _store.Update(s => s.Keeb.RotarySensitivity = sensitivity);

    public void SetPassiveLighting(SetPassiveLightingBody body) => _store.Update(s =>
    {
        s.Keeb.PassiveLighting.KeyReactive = body.KeyReactive;
        s.Keeb.PassiveLighting.KeyReactiveMask = body.KeyReactiveMask;
        s.Keeb.PassiveLighting.KeyReactiveMode = body.KeyReactiveMode;
        s.Keeb.PassiveLighting.KeyReactiveColor = new RgbaColor
        {
            R = body.KeyReactiveColor.R,
            G = body.KeyReactiveColor.G,
            B = body.KeyReactiveColor.B,
            A = body.KeyReactiveColor.A,
        };
    });

    public void SetFirmwareLighting(SetFirmwareLightingBody body) => _store.Update(s =>
    {
        s.Keeb.FirmwareLighting.AnimationMode = body.AnimationMode;
        s.Keeb.FirmwareLighting.Speed = body.Speed;
        s.Keeb.FirmwareLighting.Direction = body.Direction;
        s.Keeb.FirmwareLighting.Brightness = body.Brightness;
        s.Keeb.FirmwareLighting.KeyIndicator = body.KeyIndicator;
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
        if (s.Keeb.Macros.TryGetValue(index, out var doc))
        {
            return Convert(doc);
        }
        return new() { Index = index };
    }

    public KeebMacro SetMacro(int index, SetMacroBody body)
    {
        _store.Update(s =>
        {
            var doc = new KeebMacroDocument { Index = index };
            foreach (var k in body.Keys)
            {
                doc.Keys.Add(new Persistence.KeebMacroKey
                {
                    Key = k.Key,
                    Duration = k.Duration,
                    Type = k.Type,
                    Category = k.Category,
                    Meta = k.Meta,
                    Ctrl = k.Ctrl,
                    Alt = k.Alt,
                    Shift = k.Shift,
                });
            }
            s.Keeb.Macros[index] = doc;
        });
        return GetMacro(index);
    }

    public void Send(InputterBody body)
    {
    }

    private static KeebMacro Convert(KeebMacroDocument doc)
    {
        var m = new KeebMacro { Index = doc.Index };
        foreach (var k in doc.Keys)
        {
            m.Keys.Add(new MacroKey
            {
                Key = k.Key,
                Duration = k.Duration,
                Type = k.Type,
                Category = k.Category,
                Meta = k.Meta,
                Ctrl = k.Ctrl,
                Alt = k.Alt,
                Shift = k.Shift,
            });
        }
        return m;
    }
}
