#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Brings windows the helper spawns (native file dialogs, Explorer folder
/// windows) in front of the Nexus app window. A background process can't
/// simply SetForegroundWindow (foreground lock), so this attaches to the
/// current foreground thread's input queue first - the standard escape hatch
/// for "the user just asked for this window via another process's UI".
/// </summary>
[SupportedOSPlatform("windows")]
public static class ForegroundNudge
{
    /// <summary>The elevated <c>--focus-window</c> one-shot: focuses <paramref name="hwnd"/> only while <paramref name="expectedForeground"/> still has focus.</summary>
    public static bool FocusWindow(IntPtr hwnd, IntPtr expectedForeground)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || GetForegroundWindow() != expectedForeground)
        {
            return false;
        }

        TryForeground(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    public static void TryForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            var fg = GetForegroundWindow();
            uint fgThread = 0;
            if (fg != IntPtr.Zero)
            {
                fgThread = GetWindowThreadProcessId(fg, out _);
            }

            var cur = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(cur, fgThread, false);
                }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Open <paramref name="dir"/> in Explorer and bring the new window to the
    /// front (Explorer windows spawned by a background process open behind the
    /// app otherwise). If Explorer reuses an already-open window for the
    /// folder, no new window appears and the nudge quietly gives up.
    /// </summary>
    public static void OpenFolderOverApp(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var before = SnapshotExplorerWindows();
            LaunchFocus.Arm();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });

            // The window is created asynchronously by the (foreign) Explorer
            // process - there is no completion signal to wait on, so a bounded
            // poll finds the freshly-created folder window.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var fresh = SnapshotExplorerWindows().FirstOrDefault(h => !before.Contains(h));
                    if (fresh != IntPtr.Zero)
                    {
                        TryForeground(fresh);
                        return;
                    }

                    Thread.Sleep(100);
                }
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Open the folder containing <paramref name="filePath"/> in Explorer with
    /// the file itself selected, and bring the new window to the front. Same
    /// shape as <see cref="OpenFolderOverApp"/>, but a directory open would
    /// leave the user hunting for one file among many.
    /// </summary>
    public static void OpenFolderAndSelectOverApp(string filePath)
    {
        try
        {
            var before = SnapshotExplorerWindows();
            LaunchFocus.Arm();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var fresh = SnapshotExplorerWindows().FirstOrDefault(h => !before.Contains(h));
                    if (fresh != IntPtr.Zero)
                    {
                        TryForeground(fresh);
                        return;
                    }

                    Thread.Sleep(100);
                }
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Watch for the first visible window created on <paramref name="threadId"/>
    /// (the file dialog on its STA thread) and bring it to the front. Runs on
    /// the thread pool; the dialog thread itself is blocked inside Show().
    /// </summary>
    public static void ForegroundThreadWindowWhenShown(uint threadId)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var i = 0; i < 40; i++)
            {
                var found = IntPtr.Zero;
                EnumThreadWindows(threadId, (h, _) =>
                {
                    if (IsWindowVisible(h))
                    {
                        found = h;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    TryForeground(found);
                    return;
                }

                Thread.Sleep(100);
            }
        });
    }

    /// <summary>
    /// Launch ms-settings: in the user session (the helper is already in session 1)
    /// and bring the resulting ApplicationFrameWindow to the foreground. Settings is
    /// a UWP app; its top-level HWND class is ApplicationFrameWindow, not an Explorer class.
    /// </summary>
    public static void OpenSettingsOverApp()
    {
        try
        {
            var before = SnapshotWindowsByClass("ApplicationFrameWindow");
            LaunchFocus.Arm();
            Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var fresh = SnapshotWindowsByClass("ApplicationFrameWindow")
                        .FirstOrDefault(h => !before.Contains(h) && IsWindowVisible(h));
                    if (fresh != IntPtr.Zero)
                    {
                        TryForeground(fresh);
                        return;
                    }

                    Thread.Sleep(100);
                }
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Open a URL in the user session's default browser and bring the new window to
    /// the foreground. The helper runs in session 1 so Process.Start already lands in
    /// the user session; we poll for a new visible top-level window on the spawned
    /// process's threads.
    /// </summary>
    public static void OpenUrlOverApp(string url)
    {
        try
        {
            LaunchFocus.Arm();
            var proc = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (proc is null)
            {
                return;
            }

            PollProcessWindowAndForeground(proc);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Open a file with the OS default handler in the user session and bring the
    /// resulting window to the foreground.
    /// </summary>
    public static void OpenFileOverApp(string path)
    {
        try
        {
            LaunchFocus.Arm();
            var proc = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (proc is null)
            {
                return;
            }

            PollProcessWindowAndForeground(proc);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Poll for a visible top-level window owned by any thread in <paramref name="proc"/>
    /// and bring it to the foreground. Many handlers (browsers, Explorer) are multi-process
    /// or reuse an existing window without spawning a new process; the poll caps at 2 s so
    /// it does not spin forever when the handler reuses an existing window.
    /// </summary>
    private static void PollProcessWindowAndForeground(Process proc)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    proc.Refresh();
                    var main = proc.MainWindowHandle;
                    if (main != IntPtr.Zero && IsWindowVisible(main))
                    {
                        TryForeground(main);
                        return;
                    }

                    Thread.Sleep(100);
                }
            }
            catch { /* best-effort */ }
        });
    }

    private static HashSet<IntPtr> SnapshotExplorerWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            if (GetClassNameW(h, sb, 64) > 0)
            {
                var cls = sb.ToString();
                if (cls is "CabinetWClass" or "ExploreWClass")
                {
                    set.Add(h);
                }
            }

            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static HashSet<IntPtr> SnapshotWindowsByClass(string className)
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            if (GetClassNameW(h, sb, 64) > 0 && sb.ToString() == className)
            {
                set.Add(h);
            }

            return true;
        }, IntPtr.Zero);
        return set;
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attached);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();
}
#endif
