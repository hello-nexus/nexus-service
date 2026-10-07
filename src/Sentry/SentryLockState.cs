namespace Nexus.Service.Sentry;

/// <summary>The session's real lock state, asked once at startup. Reads state only, never input.</summary>
internal static class SentryLockState
{
    /// <summary>True or false when readable, null when not (no console session, query failed, or no source on this OS).</summary>
    internal static bool? Read()
    {
#if WINDOWS
        return Nexus.Service.Platform.ConsoleSessionLock.IsLocked();
#elif MACOS
        return Nexus.Service.Platform.Mac.MacSessionLock.IsLocked();
#else
        return null;
#endif
    }
}
