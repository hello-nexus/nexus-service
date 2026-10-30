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
    /// <summary>The temporary OOBE account is signed in and Windows is in OOBE: start in the console session with that account's token.</summary>
    OobeTempUser,
}

/// <summary>
/// The decisions behind showing the panel kiosk during first-boot setup. Pure so
/// the normal path (a signed-in user, or OOBE finished) is pinned by tests.
/// </summary>
internal static class OobePanelPolicy
{
    /// <summary>
    /// <paramref name="inOobe"/> and <paramref name="userIsLocal"/> are evaluated only for a
    /// console user named like the OOBE temporary account, or for no user at all, so a
    /// signed-in user, locked or not, never reaches them.
    /// </summary>
    internal static ConsoleLaunchPlan Decide(string consoleUser, bool consoleSessionExists, Func<bool> inOobe, Func<bool>? userIsLocal = null)
    {
        if (string.IsNullOrEmpty(consoleUser))
        {
            return consoleSessionExists && inOobe() ? ConsoleLaunchPlan.OobeNoUser : ConsoleLaunchPlan.WaitForLogon;
        }
        if (!IsOobeTempAccount(consoleUser))
        {
            return ConsoleLaunchPlan.ConsoleUser;
        }
        return (userIsLocal?.Invoke() ?? true) && inOobe() ? ConsoleLaunchPlan.OobeTempUser : ConsoleLaunchPlan.ConsoleUser;
    }

    /// <summary>The account Windows setup runs under: defaultuser0, or defaultuser followed by digits.</summary>
    internal static bool IsOobeTempAccount(string user)
    {
        const string prefix = "defaultuser";
        if (user.Length <= prefix.Length || !user.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        for (var i = prefix.Length; i < user.Length; i++)
        {
            if (user[i] < '0' || user[i] > '9') return false;
        }
        return true;
    }

    /// <summary>A console user other than the OOBE temporary account.</summary>
    internal static bool IsRealUser(string user) => !string.IsNullOrEmpty(user) && !IsOobeTempAccount(user);

    /// <summary>
    /// OOBE-launched processes are reaped once OOBE is over, or as soon as a real user is
    /// on the console (OOBEComplete can lag a real logon); the temporary OOBE account does not count. Nothing tracked means
    /// nothing is evaluated.
    /// </summary>
    internal static bool ShouldReap(int trackedCount, Func<bool> inOobe, Func<bool> consoleUserPresent)
        => trackedCount > 0 && (consoleUserPresent() || !inOobe());

    /// <summary>
    /// Whether <paramref name="imagePath"/> is one of the two binaries this service
    /// launches in an OOBE session: the overlay host or Nexus.exe, both under
    /// <paramref name="baseDirectory"/>. A pid file is writable by others, so no
    /// process is ended on its word alone.
    /// </summary>
    internal static bool IsOwnImage(string? imagePath, string baseDirectory)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }
        var full = System.IO.Path.GetFullPath(imagePath);
        return string.Equals(full, System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, "overlay", "nexus-overlay.exe")), StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, "Nexus.exe")), StringComparison.OrdinalIgnoreCase);
    }
}
