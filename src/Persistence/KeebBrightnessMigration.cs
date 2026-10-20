namespace Nexus.Service.Persistence;

/// <summary>
/// The keeb firmware brightness default changed and the old one was always persisted, so a stored
/// old-default level moves to the new default once: in settings.json (schema v25) and in each local
/// profile file (<see cref="ProfileManager"/>). A level chosen afterwards is never touched.
/// </summary>
public static class KeebBrightnessMigration
{
    public const int OldDefault = 80;
    public const int NewDefault = 100;

    /// <summary>Moves an old-default level to the new default; true when it changed.</summary>
    public static bool Apply(KeebFirmwareLighting? firmware)
    {
        if (firmware is not { Brightness: OldDefault })
        {
            return false;
        }
        firmware.Brightness = NewDefault;
        return true;
    }
}
