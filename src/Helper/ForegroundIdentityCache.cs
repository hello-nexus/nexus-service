using System;

namespace Nexus.Service.Helper;

/// <summary>
/// Identity of the foreground window's process (display name and image path),
/// remembered per (window handle, pid) so the ScreenTime poller reads them
/// once per window instead of once per pass. Geometry is not cached: a window
/// can move or resize while it stays foreground. Not thread-safe; the poller
/// is the only caller and its timer never overlaps passes.
/// </summary>
public sealed class ForegroundIdentityCache
{
    private IntPtr _hwnd;
    private int _pid;
    private string _app = "";
    private string? _exePath;
    private bool _valid;

    public bool TryGet(IntPtr hwnd, int pid, out string app, out string? exePath)
    {
        if (_valid && _hwnd == hwnd && _pid == pid)
        {
            app = _app;
            exePath = _exePath;
            return true;
        }

        app = "";
        exePath = null;
        return false;
    }

    public void Store(IntPtr hwnd, int pid, string app, string? exePath)
    {
        _hwnd = hwnd;
        _pid = pid;
        _app = app;
        _exePath = exePath;
        _valid = true;
    }

    public void Clear()
    {
        _valid = false;
        _app = "";
        _exePath = null;
    }

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
