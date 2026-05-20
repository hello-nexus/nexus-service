using System.Collections.Generic;
using Qos.Service.Models.Peripherals.Keeb;

namespace Qos.Service.Peripherals.Keeb;

public interface IKeebProvider
{
    KeyboardState GetState(int layer = 0);
    /// <summary>Keys for a single firmware layer (0..3). Falls back to the persisted snapshot when offline; empty list if there isn't one yet.</summary>
    List<List<KeebKey>> GetLayer(int layer);
    /// <summary>Writes one key on the given layer. Returns false when the keeb is offline (caller should surface that to the UI).</summary>
    bool SetLayerKey(int layer, SetLayerKeyBody body);
    /// <summary>Resets a layer to firmware defaults. Returns false when offline.</summary>
    bool ResetLayer(int layer);

    GetKeebSettingsResponse GetSettings();
    string[] GetRotaryFunctions();
    void SetRotary(SetRotaryWheelsBody body);
    void SetRotarySensitivity(string sensitivity);
    void SetPassiveLighting(SetPassiveLightingBody body);
    void SetFirmwareLighting(SetFirmwareLightingBody body);
    void SetGameMode(SetGameModeBody body);
    KeebMacro GetMacro(int index);
    KeebMacro SetMacro(int index, SetMacroBody body);
}

public interface IInputterProvider
{
    void Send(InputterBody body);
}
