namespace Nexus.Service.Helper;

/// <summary>How a request to open a folder in the user session ended.</summary>
public enum OpenFolderOutcome
{
    Opened,
    NoHelper,
    /// <summary>The helper never acked in time. Explorer may still appear, so this is not a failure.</summary>
    TimedOut,
    Failed,
    /// <summary>The caller went away after the command was sent; the folder may still open.</summary>
    Canceled,
}

public static class OpenFolderOutcomes
{
    public static OpenFolderOutcome FromResult(bool ok, string? error) =>
        ok ? OpenFolderOutcome.Opened
        : error == HelperErrors.Timeout ? OpenFolderOutcome.TimedOut
        : error == HelperErrors.Canceled ? OpenFolderOutcome.Canceled
        : OpenFolderOutcome.Failed;

    /// <summary>True when the caller should report the folder as not opened.</summary>
    public static bool IsFailure(OpenFolderOutcome outcome) =>
        outcome is OpenFolderOutcome.NoHelper or OpenFolderOutcome.Failed;
}
