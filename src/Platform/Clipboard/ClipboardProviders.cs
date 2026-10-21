using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

/// <summary>Windows clipboard through the Win32 clipboard API (CF_UNICODETEXT).</summary>
public sealed partial class WindowsClipboardProvider : IClipboardProvider
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x2;
    private const int OpenAttempts = 10;
    private const int OpenRetryMs = 20;
    private static readonly IntPtr HwndMessage = new(-3);

    public bool SetText(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var bytes = Encoding.Unicode.GetBytes((text ?? "") + "\0");
        // EmptyClipboard on a NULL-owner open makes SetClipboardData fail, so own the clipboard with a message-only window.
        var owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero)
        {
            ServiceLog.Warn($"[clipboard-win] owner window failed: {Marshal.GetLastPInvokeError()}");
            return false;
        }

        try
        {
            return SetUnicodeText(owner, bytes);
        }
        finally { DestroyWindow(owner); }
    }

    private static bool SetUnicodeText(IntPtr owner, byte[] bytes)
    {
        // Another process can hold the clipboard open briefly, so a busy open is retried.
        var opened = false;
        for (var i = 0; i < OpenAttempts && !(opened = OpenClipboard(owner)); i++)
        {
            Thread.Sleep(OpenRetryMs);
        }
        if (!opened)
        {
            ServiceLog.Warn($"[clipboard-win] OpenClipboard failed: {Marshal.GetLastPInvokeError()}");
            return false;
        }

        try
        {
            if (!EmptyClipboard()) return false;
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero) return false;
            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            Marshal.Copy(bytes, 0, locked, bytes.Length);
            GlobalUnlock(handle);
            // On success the clipboard owns the memory; only a failed set leaves it ours to free.
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                ServiceLog.Warn($"[clipboard-win] SetClipboardData failed: {Marshal.GetLastPInvokeError()}");
                return false;
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr memory);
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
