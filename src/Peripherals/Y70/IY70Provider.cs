namespace Nexus.Service.Peripherals.Y70;

public interface IY70Provider
{
    bool IsConnected();
    string GetOrientation();
    /// <summary>Persists the orientation preference only; call
    /// <see cref="ApplyEffectiveOrientation"/> to push it to hardware.</summary>
    void SetOrientation(string orientation);
    bool GetForceOrientation();
    /// <summary>Persists the force-orientation flag only; call
    /// <see cref="ApplyEffectiveOrientation"/> to push it to hardware.</summary>
    void SetForceOrientation(bool forceOrientation);
    /// <summary>Pushes the current effective orientation (PortraitFlipped when
    /// ForceOrientation is set, else the stored preference) to hardware.</summary>
    void ApplyEffectiveOrientation();
    int GetBrightness();
    void SetBrightness(int brightness);
    /// <summary>Re-applies the stored slider through the brightness schedule multiplier without persisting anything.</summary>
    void ReapplyBrightness() { }
    bool GetToggle();
    void SetToggle(bool toggle);
    /// <summary>Drives screen power without persisting; false restores the stored preference.</summary>
    void SetGameModeScreenOff(bool screenOff);
    bool IsRotated();
}
