namespace Nexus.Service.Activity;

/// <summary>
/// Opt-in diagnostic log line for the Windows helper's EnumWindows callback
/// (WindowSetPoller), gated by the NEXUS_WINDOW_DIAG env var so it stays off
/// by default. Kept platform-neutral (no P/Invoke) and testable, mirroring
/// WindowClassification's split between Win32 extraction (in the poller) and
/// pure decision/formatting logic (here). Does not change IsCountableWindow
/// or any classification behavior - it only reports what the poller measured.
/// </summary>
public static class WindowDiagnostics
{
    public const string EnvVarName = "NEXUS_WINDOW_DIAG";

    /// <summary>Argv flag UserHelperBootstrapper forwards to the spawned
    /// helper process in place of EnvVarName - a machine environment
    /// variable change set on the LocalSystem service side does not reach a
    /// process schtasks spawns in another session, but an argv value does.</summary>
    public const string HelperArgName = "--window-diag";

    /// <summary>True only for the literal value "1" - null, empty, or any other value keeps diagnostics off.</summary>
    public static bool IsEnabled(string? envValue) => envValue == "1";

    /// <summary>
    /// One [window-diag] line: process identity, the IsCountableWindow
    /// inputs, the raw title-read outcome, and the classification result.
    /// titleLength/titleReadError distinguish a legitimately empty title
    /// (no stored caption, and the WM_GETTEXTLENGTH fallback returned 0 with
    /// error 0) from a blocked read (e.g. UIPI denying a lower-integrity
    /// caller access to an elevated window, which fails the fallback with a
    /// nonzero Win32 error). A window with a stored caption never reaches the
    /// fallback, so its titleReadError is 0.
    /// </summary>
    public static string FormatLine(
        int pid,
        string processName,
        bool isVisible,
        bool hasOwner,
        bool isToolWindow,
        bool isCloaked,
        bool hasTitle,
        int titleLength,
        int titleReadError,
        bool hasOnScreenBounds,
        bool coversMonitor,
        bool isForegroundWindow,
        bool isCountable)
    {
        return "[window-diag] " +
            $"pid={pid} proc=\"{processName}\" " +
            $"isVisible={isVisible} hasOwner={hasOwner} isToolWindow={isToolWindow} isCloaked={isCloaked} " +
            $"hasTitle={hasTitle} titleLength={titleLength} titleReadError={titleReadError} " +
            $"hasOnScreenBounds={hasOnScreenBounds} coversMonitor={coversMonitor} isForegroundWindow={isForegroundWindow} " +
            $"isCountable={isCountable} class={(isCountable ? "App" : "Background")}";
    }
}
