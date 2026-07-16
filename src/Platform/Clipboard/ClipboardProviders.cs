using System;
using System.Text;

namespace Nexus.Service.Platform.Clipboard;

/// <summary>
/// Sets the OS clipboard text. Used by the deck "type text" action, which sets
/// the clipboard then injects a paste keystroke - the only Unicode-reliable
/// cross-platform way to insert arbitrary text. Returns false when the platform
/// clipboard tool is unavailable.
/// </summary>
public interface IClipboardProvider
{
    bool SetText(string text);
}

/// <summary>macOS clipboard via <c>pbcopy</c>.</summary>
public sealed class MacClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            ShellExecutor.RunWithStdin("/usr/bin/pbcopy", text ?? "", 2000);
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-mac] pbcopy failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Linux clipboard via <c>wl-copy</c> (Wayland) then <c>xclip</c> (X11).</summary>
public sealed class LinuxClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var payload = text ?? "";
        // wl-copy reads stdin and forks a server; treat a clean exit as success.
        if (ShellExecutor.RunWithStdin("wl-copy", payload, 2000) is not null && WlCopyAvailable())
            return true;
        try
        {
            ShellExecutor.RunWithStdin("xclip", payload, 2000, "-selection", "clipboard");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-linux] no clipboard tool (wl-copy/xclip) available: {ex.Message}");
            return false;
        }
    }

    private static bool WlCopyAvailable()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

/// <summary>Windows clipboard via PowerShell <c>Set-Clipboard</c> (AOT-safe; no OLE/STA COM).</summary>
public sealed class WindowsClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Carry the text as base64 (UTF-8) inside a script passed via
        // -EncodedCommand (base64 UTF-16): both hops are ASCII on the command
        // line, so an emoji's surrogate pair survives. Piping through stdin
        // narrowed non-ANSI chars to "?" under the console's OEM codepage.
        var textB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        var script = $"Set-Clipboard -Value ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{textB64}')))";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var exitCode = ShellExecutor.RunWithStdinExit("powershell", "", 4000, out var stderr,
            "-NoProfile", "-EncodedCommand", encoded);
        if (exitCode != 0)
        {
            ServiceLog.Warn($"[clipboard-win] Set-Clipboard exited {exitCode}: {stderr.Trim()}");
            return false;
        }
        return true;
    }
}

public sealed class StubClipboardProvider : IClipboardProvider
{
    public bool SetText(string text) => false;
}

#if WINDOWS
/// <summary>
/// Clipboards are per-session: a set from the Session-0 service lands on an
/// invisible clipboard. Interactive runs set locally; service mode routes
/// through the user-session helper and reports failure when none is connected
/// (a local set would "succeed" without the user ever seeing the text).
/// </summary>
public sealed class HelperRoutedClipboardProvider : IClipboardProvider
{
    private readonly IServiceProvider _services;
    private readonly WindowsClipboardProvider _local = new();

    public HelperRoutedClipboardProvider(IServiceProvider services) => _services = services;

    public bool SetText(string text)
    {
        if (Environment.UserInteractive)
            return _local.SetText(text);
        if (_services.GetService(typeof(Nexus.Service.Helper.HelperRegistry)) is not Nexus.Service.Helper.HelperRegistry registry)
        {
            return false;
        }
        try
        {
            // The token must also bound the pipe WRITE: SendCommandAsync's
            // internal timeout only covers the reply wait, and this is a
            // sync-over-async seam parking a worker thread.
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            return Nexus.Service.Helper.Domains.ClipboardCommands
                .SetTextAsync(registry, text, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-win] helper route failed: {ex.Message}");
            return false;
        }
    }
}
#endif
