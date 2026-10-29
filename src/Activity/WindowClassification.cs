using System;

namespace Nexus.Service.Activity;

/// <summary>
/// The "does this window count as a real app window" predicate shared by
/// the Windows user-session helper's EnumWindows callback (WindowSetPoller)
/// and its tests. Kept platform-neutral (no P/Invoke) - the poller extracts
/// each attribute via Win32 and this only decides. Matches Task Manager's
/// Apps definition, not .NET's own (more permissive) MainWindowHandle
/// heuristic: IsWindowVisible alone still passes DWM-cloaked windows (hidden
/// UWP/background windows, and the common case behind service processes
/// like svchost owning technically-visible windows nobody sees), so cloak
/// state is required, not optional - unless the window covers a whole
/// monitor, which only a fullscreen app does. Likewise a missing title is
/// excluded unless the read itself was blocked (UIPI across an elevation
/// boundary), which a truly titleless background window never trips. The
/// cloak/title/tool-window heuristics all exist to reject windows that are
/// hidden from the user - the current OS foreground window is never one of
/// those by definition, so it bypasses all of them and only the ownership
/// and on-screen-bounds sanity checks still apply.
/// </summary>
public static class WindowClassification
{
    public static bool IsCountableWindow(
        bool isVisible,
        IntPtr owner,
        bool isToolWindow,
        bool isCloaked,
        bool hasTitle,
        bool hasOnScreenBounds,
        bool coversMonitor,
        bool titleBlockedByUipi,
        bool isForegroundWindow)
    {
        if (owner != IntPtr.Zero || !hasOnScreenBounds)
        {
            return false;
        }
        if (isForegroundWindow)
        {
            return true;
        }
        return isVisible
            && !isToolWindow
            && (!isCloaked || coversMonitor)
            && (hasTitle || titleBlockedByUipi);
    }

    /// <summary>The per-window attribute reads IsCountableWindow's first terms
    /// need. Each method is one Win32 call in the poller; an implementation
    /// may cache, and PreClassify calls each at most once.</summary>
    public interface IWindowReads
    {
        bool IsVisible();
        IntPtr Owner();
        bool IsToolWindow();
        bool HasOnScreenBounds();
    }

    public enum PreResult
    {
        Rejected,
        Accepted,
        /// <summary>Survived the cheap terms; the cloak and title terms decide.</summary>
        NeedsCloakAndTitle,
    }

    /// <summary>
    /// Same outcome as IsCountableWindow for every input, reading attributes
    /// in the cheapest-rejecting order and skipping any read whose value cannot
    /// change the result: an invisible non-foreground window is rejected on
    /// IsWindowVisible alone, and the foreground window never reads visibility
    /// or style. Rejected and Accepted are final; NeedsCloakAndTitle means
    /// IsCountableWindow must still be evaluated with the cloak and title terms.
    /// </summary>
    public static PreResult PreClassify<T>(ref T reads, bool isForegroundWindow) where T : struct, IWindowReads
    {
        if (!isForegroundWindow && !reads.IsVisible())
        {
            return PreResult.Rejected;
        }
        if (reads.Owner() != IntPtr.Zero)
        {
            return PreResult.Rejected;
        }
        if (!isForegroundWindow && reads.IsToolWindow())
        {
            return PreResult.Rejected;
        }
        if (!reads.HasOnScreenBounds())
        {
            return PreResult.Rejected;
        }
        return isForegroundWindow ? PreResult.Accepted : PreResult.NeedsCloakAndTitle;
    }

    /// <summary>True when the window rect has positive area and overlaps the
    /// virtual screen (the bounding box of every monitor) - excludes
    /// zero-size and fully off-screen windows.</summary>
    public static bool HasOnScreenBounds(
        int left, int top, int right, int bottom,
        int virtualLeft, int virtualTop, int virtualRight, int virtualBottom)
    {
        if (right <= left || bottom <= top)
        {
            return false;
        }
        return left < virtualRight && right > virtualLeft && top < virtualBottom && bottom > virtualTop;
    }

    /// <summary>True when the window rect has positive area and fully
    /// contains the given monitor's bounds - an exclusive-fullscreen window
    /// renders at exactly its monitor's rect (or larger, for any overscan),
    /// even though DWM cloaks it like a hidden background window.</summary>
    public static bool CoversMonitor(
        int left, int top, int right, int bottom,
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom)
    {
        if (right <= left || bottom <= top)
        {
            return false;
        }
        return left <= monitorLeft && top <= monitorTop && right >= monitorRight && bottom >= monitorBottom;
    }

    /// <summary>True when className identifies desktop/taskbar chrome
    /// (Progman/WorkerW own the desktop, Shell_TrayWnd/Shell_SecondaryTrayWnd
    /// own the taskbar on each monitor) or a Task View / Alt-Tab switcher
    /// surface (MultitaskingViewFrame is Task View; XamlExplorerHostIslandWindow
    /// is the modern Alt-Tab/Win-Tab switcher, TaskSwitcherWnd the older one) -
    /// none of these are an app the user is running, even though any can
    /// briefly take OS foreground focus.</summary>
    public static bool IsShellChromeClassName(string className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow" or "TaskSwitcherWnd";
}
