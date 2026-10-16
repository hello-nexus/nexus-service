using System;

namespace Nexus.Service.Lifecycle;

/// <summary>Argument contract for <c>NexusUpdater.exe --updater-window &lt;from&gt; &lt;to&gt;</c>.</summary>
internal static class UpdaterSplashArgs
{
    public const string Flag = "--updater-window";

    /// <summary>Window caption; the helper finds and closes the splash by it.</summary>
    public const string Title = "Nexus Updater";

    public static bool TryParse(string[] args, out string from, out string to)
    {
        from = to = "";
        if (args.Length == 0 || !string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase)) return false;
        if (args.Length > 1) from = args[1];
        if (args.Length > 2) to = args[2];
        return true;
    }

    /// <summary>"from → to", or just the target when the source is unknown.</summary>
    public static string VersionLine(string from, string to)
        => from.Length == 0 ? to : to.Length == 0 ? from : from + " → " + to;
}
