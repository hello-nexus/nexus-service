using System;

namespace Nexus.Service.Helper;

public static class ScreenTimeProcessName
{
    /// <summary>
    /// The name System.Diagnostics.Process.ProcessName yields on Windows for
    /// an image path: everything after the last backslash, minus a trailing
    /// ".exe" (case-insensitive). Other extensions stay, unlike
    /// Path.GetFileNameWithoutExtension. Empty when the path has no file part.
    /// </summary>
    public static string ProcessNameFromPath(string exePath)
    {
        var name = exePath.AsSpan();
        name = name.Slice(name.LastIndexOf('\\') + 1);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Slice(0, name.Length - 4);
        }

        return name.ToString();
    }
}
