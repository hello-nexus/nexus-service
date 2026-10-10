namespace Nexus.Service.Lighting.IdleDim;

/// <summary>The OS's own screen-off timeout, so the UI can label "when my screen turns off".</summary>
public static class OsScreenOffTimeout
{
    /// <summary>Seconds for the active power source; 0 is Never; null when unreadable or unsupported.</summary>
    public static int? ReadSeconds()
    {
        try
        {
#if WINDOWS
            return Nexus.Service.Platform.Windows.WindowsScreenOffTimeout.Read();
#elif MACOS
            return Nexus.Service.Platform.Mac.MacScreenOffTimeout.Read();
#else
            return null;
#endif
        }
        catch
        {
            return null;
        }
    }
}
