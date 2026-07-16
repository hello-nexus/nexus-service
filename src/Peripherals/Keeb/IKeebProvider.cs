using Nexus.Service.Models.Peripherals.Keeb;

namespace Nexus.Service.Peripherals.Keeb;

public interface IKeebProvider
{
    KeyboardState GetState(int layer);
    GetKeebSettingsResponse GetSettings();
    string[] GetRotaryFunctions();
    void SetRotary(SetRotaryWheelsBody body);
    void SetFirmwareLighting(SetFirmwareLightingBody body);
    void SetPassiveLighting(SetPassiveLightingBody body);
    void SetGameMode(SetGameModeBody body);
    KeebMacro GetMacro(int index);
    SetMacroResponse SetMacro(int index, SetMacroBody body);
    SetLayerKeyResponse SetLayerKey(int layer, SetLayerKeyBody body);
    SetLayerKeyResponse ResetLayer(int layer);
    /// <summary>Push persisted key overrides + macros to the device on (re)connect. No-op when no device.</summary>
    void ApplyPersistedAssignments();
}

public interface IInputterProvider
{
    void Send(InputterBody body);
}
