using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// HID-backed <see cref="IKeebProvider"/>. Every setter persists the desired
/// state to settings.json (the single source of truth) and then pushes the
/// COMPLETE state to the keyboard via <see cref="KeebSettingsApplier"/>: one
/// 0x06 settings write covering game mode + firmware animation + rotary, so a
/// partial change never clobbers another field.
///
/// Key assignments and macros write the keyboard's onboard storage and are
/// VERIFIED by reading the pages back byte-exact before the change is
/// persisted - a true() from the HID write only means the bytes left the
/// port, not that the firmware accepted them.
///
/// Layer writes always start from the device's own pristine table, captured
/// once per (layout, profile, layer) before Nexus's first write - see
/// <see cref="KeebLayerCodec"/>.
/// </summary>
public sealed class RealKeebProvider : IKeebProvider
{
    private readonly IConfigStore _store;
    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly LightingEngine _engine;
    // Serializes layer/macro store+device transactions so two concurrent saves
    // can't leave the device holding one write and the store another.
    private readonly object _writeLock = new();

    public RealKeebProvider(IConfigStore store, KeebHub hub, KeebSettingsApplier applier, LightingEngine engine)
    {
        _store = store;
        _hub = hub;
        _applier = applier;
        _engine = engine;
    }

    private string CurrentLayout()
        => string.Equals(_hub.State.Layout, "ISO", StringComparison.OrdinalIgnoreCase) ? "ISO" : "ANSI";

    public KeyboardState GetState(int layer)
    {
        var layout = CurrentLayout();
        var profile = _hub.State.Profile;
        var rows = KeebLayerMap.Rows(layout);
        var keys = new List<List<KeebKey>>(rows.Count);
        foreach (var row in rows)
        {
            var cells = new List<KeebKey>(row.Length);
            for (var i = 0; i < row.Length; i++) cells.Add(new KeebKey());
            keys.Add(cells);
        }
        foreach (var o in _store.Load().Keeb.KeyOverrides)
        {
            if (o.Profile != profile || o.Layer != layer) continue;
            if (o.X < 0 || o.X >= keys.Count || o.Y < 0 || o.Y >= keys[o.X].Count) continue;
            keys[o.X][o.Y] = new KeebKey { Mode = o.Mode, Function = o.Function, Input = o.Input };
        }
        return new KeyboardState
        {
            IsConnected = _hub.IsConnected,
            Profile = profile,
            Layer = layer,
            Layout = layout,
            Keys = keys,
        };
    }

    // ── Key assignment ──

    public SetLayerKeyResponse SetLayerKey(int layer, SetLayerKeyBody body)
    {
        if (layer is < 0 or >= KeebLayerMap.LayerCount)
            return LayerFail("layer out of range");
        var layout = CurrentLayout();
        if (!KeebLayerMap.TryGetCell(layout, body.X, body.Y, out _))
            return LayerFail($"no key at ({body.X},{body.Y}) on {layout}");
        var isNone = body.Mode == "StandardKey" && body.Func == "None";
        if (!isNone && !KeebKeyCodes.TryMatrixCode(body.Mode, body.Func, body.Input, out _))
            return LayerFail($"unknown function {body.Mode}/{body.Func}");

        lock (_writeLock)
        {
            var profile = _hub.State.Profile;
            if (!TryGetPristine(layout, profile, layer, out var pristine, out var why))
                return LayerFail(why);

            // Candidate overlay set = persisted overrides with this cell replaced.
            var overrides = _store.Load().Keeb.KeyOverrides
                .Where(o => !(o.Profile == profile && o.Layer == layer && o.X == body.X && o.Y == body.Y))
                .ToList();
            if (!isNone)
            {
                overrides.Add(new KeebKeyOverride
                {
                    Profile = profile, Layer = layer, X = body.X, Y = body.Y,
                    Mode = body.Mode, Function = body.Func, Input = body.Input,
                });
            }

            var wrote = false;
            if (_hub.IsConnected)
            {
                if (!WriteAndVerifyLayer(layout, profile, layer, pristine, overrides, out var err))
                    return LayerFail(err);
                wrote = true;
            }

            _store.Update(s =>
            {
                s.Keeb.KeyOverrides.RemoveAll(o => o.Profile == profile && o.Layer == layer && o.X == body.X && o.Y == body.Y);
                if (!isNone)
                {
                    s.Keeb.KeyOverrides.Add(new KeebKeyOverride
                    {
                        Profile = profile, Layer = layer, X = body.X, Y = body.Y,
                        Mode = body.Mode, Function = body.Func, Input = body.Input,
                    });
                }
            });
            return new SetLayerKeyResponse { State = GetState(layer), WroteDevice = wrote };
        }
    }

    public SetLayerKeyResponse ResetLayer(int layer)
    {
        if (layer is < 0 or >= KeebLayerMap.LayerCount)
            return LayerFail("layer out of range");
        var layout = CurrentLayout();
        lock (_writeLock)
        {
            var profile = _hub.State.Profile;
            var s = _store.Load().Keeb;
            var hasOverrides = s.KeyOverrides.Any(o => o.Profile == profile && o.Layer == layer);
            var hasPristine = s.PristineLayers.ContainsKey(PristineKey(layout, profile, layer));
            if (!hasOverrides && !hasPristine)
                return new SetLayerKeyResponse { State = GetState(layer), WroteDevice = false };

            var wrote = false;
            if (hasPristine)
            {
                // The device holds remapped tables; restoring them needs the
                // device. Refuse offline rather than pretend.
                if (!_hub.IsConnected)
                    return LayerFail("keyboard is not connected");
                if (!TryGetPristine(layout, profile, layer, out var pristine, out var why))
                    return LayerFail(why);
                if (!WriteAndVerifyLayer(layout, profile, layer, pristine, new List<KeebKeyOverride>(), out var err))
                    return LayerFail(err);
                wrote = true;
            }
            _store.Update(st => st.Keeb.KeyOverrides.RemoveAll(o => o.Profile == profile && o.Layer == layer));
            return new SetLayerKeyResponse { State = GetState(layer), WroteDevice = wrote };
        }
    }

    /// <summary>
    /// Push every persisted layer override set and macro to the device
    /// (called on (re)connect so assignments made while unplugged take
    /// effect). Layers with no overrides are left untouched.
    /// </summary>
    public void ApplyPersistedAssignments()
    {
        if (!_hub.IsConnected) return;
        var layout = CurrentLayout();
        lock (_writeLock)
        {
            var s = _store.Load().Keeb;
            foreach (var group in s.KeyOverrides.GroupBy(o => (o.Profile, o.Layer)))
            {
                if (!TryGetPristine(layout, group.Key.Profile, group.Key.Layer, out var pristine, out var why))
                {
                    ServiceLog.Error($"[keeb] layer reapply skipped p{group.Key.Profile} l{group.Key.Layer}: {why}");
                    continue;
                }
                if (!WriteAndVerifyLayer(layout, group.Key.Profile, group.Key.Layer, pristine, group.ToList(), out var err))
                    ServiceLog.Error($"[keeb] layer reapply failed p{group.Key.Profile} l{group.Key.Layer}: {err}");
            }
            foreach (var (slot, doc) in s.Macros)
            {
                if (slot is < 0 or >= KeebLayerMap.ProfileCount * 16) continue;
                var built = KeebMacroCodec.Build(doc);
                if (!_hub.WriteMacro(slot, built.Pages))
                    ServiceLog.Error($"[keeb] macro reapply failed for slot {slot}");
            }
        }
    }

    // Compose pristine + overlays, write the 8 pages, read them back and
    // require a byte-exact echo before reporting success.
    private bool WriteAndVerifyLayer(string layout, int profile, int layer, byte[] pristine, List<KeebKeyOverride> overrides, out string error)
    {
        var overlays = new List<(int Slot, byte[] Code)>();
        foreach (var o in overrides)
        {
            if (o.Profile != profile || o.Layer != layer) continue;
            if (!KeebLayerMap.TryGetCell(layout, o.X, o.Y, out var cell)) continue;
            if (!KeebKeyCodes.TryMatrixCode(o.Mode, o.Function, o.Input, out var code)) continue;
            overlays.Add((cell.Slot, code));
        }
        var pages = KeebLayerCodec.ComposePages(pristine, overlays);
        if (!_hub.WriteLayer(profile, layer, pages))
        {
            error = "layer write failed";
            return false;
        }
        var readback = _hub.ReadLayerRaw(profile, layer);
        if (readback is null)
        {
            error = "layer verify read failed";
            return false;
        }
        if (!KeebLayerCodec.DataEquals(readback, pages, KeebLayerCodec.PageCount))
        {
            ServiceLog.Error($"[keeb] layer verify mismatch p{profile} l{layer}: {KeebLayerCodec.DescribeMismatch(pages, readback, KeebLayerCodec.PageCount)}");
            error = "layer verify mismatch - the keyboard did not accept the table";
            return false;
        }
        error = "";
        return true;
    }

    private static string PristineKey(string layout, int profile, int layer) => $"{layout}|{profile}|{layer}";

    // Load the captured factory table for (layout, profile, layer), capturing
    // it from the device on first use. Caller holds _writeLock.
    private bool TryGetPristine(string layout, int profile, int layer, out byte[] pristine, out string error)
    {
        pristine = Array.Empty<byte>();
        var key = PristineKey(layout, profile, layer);
        var stored = _store.Load().Keeb.PristineLayers.TryGetValue(key, out var hex) ? hex : null;
        if (stored is not null)
        {
            try
            {
                var bytes = Convert.FromHexString(stored);
                if (bytes.Length == KeebLayerCodec.PagesBytes)
                {
                    pristine = bytes;
                    error = "";
                    return true;
                }
            }
            catch (FormatException) { /* recapture below */ }
            ServiceLog.Error($"[keeb] stored pristine layer {key} is corrupt; recapturing");
        }
        if (!_hub.IsConnected)
        {
            error = "keyboard must be connected for its first key assignment";
            return false;
        }
        var raw = _hub.ReadLayerRaw(profile, layer);
        if (raw is null || raw.Length != KeebLayerCodec.PagesBytes)
        {
            error = "could not read the keyboard's current layout";
            return false;
        }
        _store.Update(s => s.Keeb.PristineLayers[key] = Convert.ToHexString(raw));
        pristine = raw;
        error = "";
        return true;
    }

    private static SetLayerKeyResponse LayerFail(string msg)
        => new() { Error = true, Msg = msg, WroteDevice = false };

    // ── Settings ──

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
            Speed = NormalizeSpeed(k.FirmwareLighting.Speed),
            Direction = NormalizeDirection(k.FirmwareLighting.Direction),
            Brightness = k.FirmwareLighting.Brightness,
            KeyReactive = k.FirmwareLighting.KeyReactive,
            KeyReactiveMask = k.FirmwareLighting.KeyReactiveMask,
            KeyReactiveMode = NormalizeReactiveMode(k.FirmwareLighting.KeyReactiveMode),
            KeyReactiveColor = ToRgba(k.FirmwareLighting.KeyReactiveColor),
        };
    }

    // The panel dropdowns only list canonical values; map legacy persisted ones
    // so the control isn't blank. "Medium" is the old synonym for "Standard"
    // (same speed byte); "Off" was a non-mode default (on/off is KeyReactive);
    // "Forward"/"Reverse" are the pre-panel direction names (same bytes as
    // LeftToRight/RightToLeft).
    private static string NormalizeSpeed(string s) =>
        string.Equals(s, "Medium", StringComparison.OrdinalIgnoreCase) ? "Standard" : s;

    private static string NormalizeReactiveMode(string m) =>
        string.Equals(m, "Off", StringComparison.OrdinalIgnoreCase) ? "SingleKey" : m;

    private static string NormalizeDirection(string d) => d.ToLowerInvariant() switch
    {
        "forward" => "LeftToRight",
        "reverse" => "RightToLeft",
        _ => d,
    };

    public string[] GetRotaryFunctions() => KeebSettingsCodec.RotaryFunctions;

    public void SetRotary(SetRotaryWheelsBody body)
    {
        _store.Update(s =>
        {
            s.Keeb.RotaryLeft = body.Left;
            s.Keeb.RotaryRight = body.Right;
        });
        _applier.Apply();
    }

    public void SetFirmwareLighting(SetFirmwareLightingBody body)
    {
        var brightness = Math.Clamp(body.Brightness, 0, 100);
        // While a software effect streams, the firmware animation is suppressed and
        // the frame writer caps the stream by the master brightness, so writing the
        // 0x06 page now would only flash the firmware animation through the live
        // stream (and a brightness drag flashes repeatedly). Update the master and
        // let the composer apply it; the frame writer writes the page when streaming
        // stops. With no effect, write it so the firmware animation reflects the
        // change immediately.
        var streaming = _engine.CurrentEffectName != "none";
        _applier.ApplyGated(s =>
        {
            s.Keeb.FirmwareLighting.AnimationMode = body.AnimationMode;
            s.Keeb.FirmwareLighting.Speed = body.Speed;
            s.Keeb.FirmwareLighting.Direction = body.Direction;
            s.Keeb.FirmwareLighting.Brightness = brightness;
        }, writeDevice: !streaming);
    }

    public void SetPassiveLighting(SetPassiveLightingBody body)
    {
        // Key-reactive is a software overlay driven by input callbacks; persist
        // it here and render it in the input-callback phase. No firmware bytes.
        _store.Update(s =>
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
    }

    public void SetGameMode(SetGameModeBody body)
    {
        _store.Update(s =>
        {
            s.Keeb.GameMode.AltF4 = body.AltF4;
            s.Keeb.GameMode.AltTab = body.AltTab;
            s.Keeb.GameMode.ShiftTab = body.ShiftTab;
            s.Keeb.GameMode.WindowsKey = body.WindowsKey;
        });
        _applier.Apply();
    }

    // ── Macros ──

    // Macros live in GLOBAL firmware slots: profile*16 + panel index (the
    // vendor's SetMacro(profile * 16 + macroIndex) convention), so profile 1
    // macros never clobber profile 0's. Persistence uses the same key.
    private int GlobalMacroSlot(int index) => _hub.State.Profile * 16 + index;

    public KeebMacro GetMacro(int index)
    {
        var s = _store.Load();
        var slot = GlobalMacroSlot(index);
        return s.Keeb.Macros.TryGetValue(slot, out var doc)
            ? ToDto(doc, index)
            : new KeebMacro { Index = index };
    }

    public SetMacroResponse SetMacro(int index, SetMacroBody body)
    {
        var keys = body.Keys ?? new List<MacroKey>();
        var doc = new KeebMacroDocument { Index = index };
        foreach (var k in keys)
        {
            doc.Keys.Add(new KeebMacroKey
            {
                Key = k.Key,
                Duration = Math.Clamp(k.Duration, 0, KeebMacroCodec.MaxDurationMs),
                Type = k.Type,
            });
        }

        var built = KeebMacroCodec.Build(doc);
        lock (_writeLock)
        {
            var slot = GlobalMacroSlot(index);
            var wrote = false;
            if (_hub.IsConnected)
            {
                if (!_hub.WriteMacro(slot, built.Pages))
                    return MacroFail("macro write failed", built);
                var readback = _hub.ReadMacroRaw(slot);
                if (readback is null || !KeebLayerCodec.DataEquals(readback, built.Pages, KeebMacroCodec.PageCount, KeebMacroCodec.ReservedTailBytes))
                {
                    if (readback is not null)
                        ServiceLog.Error($"[keeb] macro verify mismatch slot {slot}: {KeebLayerCodec.DescribeMismatch(built.Pages, readback, KeebMacroCodec.PageCount, KeebMacroCodec.ReservedTailBytes)}");
                    return MacroFail("macro verify mismatch - the keyboard did not accept it", built);
                }
                wrote = true;
            }
            _store.Update(s => s.Keeb.Macros[slot] = doc);
            return new SetMacroResponse
            {
                Macro = GetMacro(index),
                Truncated = built.Truncated,
                DroppedKeys = built.DroppedKeys.ToArray(),
                WroteDevice = wrote,
            };
        }
    }

    private static SetMacroResponse MacroFail(string msg, KeebMacroCodec.BuildResult built) => new()
    {
        Error = true,
        Msg = msg,
        Truncated = built.Truncated,
        DroppedKeys = built.DroppedKeys.ToArray(),
        WroteDevice = false,
    };

    private static RGBA ToRgba(RgbaColor c) => new() { R = c.R, G = c.G, B = c.B, A = c.A };

    private static KeebMacro ToDto(KeebMacroDocument doc, int index)
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
