using System;

namespace Nexus.Service.Lifecycle;

/// <summary>What the service does about a console session when it wants to start something in it.</summary>
internal enum ConsoleLaunchPlan
{
    /// <summary>A user is signed in: the existing schtasks launch for that user.</summary>
    ConsoleUser,
    /// <summary>Nobody is signed in and OOBE is not running: wait for a logon, as before.</summary>
    WaitForLogon,
    /// <summary>Nobody is signed in and Windows is in OOBE: start directly in the console session.</summary>
    OobeNoUser,
}

/// <summary>
/// The decisions behind showing the panel kiosk during first-boot setup. Pure so
/// the normal path (a signed-in user, or OOBE finished) is pinned by tests.
/// </summary>
internal static class OobePanelPolicy
{
    /// <summary>
    /// <paramref name="inOobe"/> is only evaluated when no user is signed in, so a
    /// signed-in session, locked or not, never reaches it.
    /// </summary>
    internal static ConsoleLaunchPlan Decide(string consoleUser, bool consoleSessionExists, Func<bool> inOobe)
    {
        if (!string.IsNullOrEmpty(consoleUser))
        {
            return ConsoleLaunchPlan.ConsoleUser;
        }
        return consoleSessionExists && inOobe() ? ConsoleLaunchPlan.OobeNoUser : ConsoleLaunchPlan.WaitForLogon;
    }

    /// <summary>OOBE-launched processes are reaped only once OOBE is over; nothing tracked means nothing to evaluate.</summary>
    internal static bool ShouldReap(int trackedCount, Func<bool> inOobe) => trackedCount > 0 && !inOobe();
}
