namespace Nexus.Service.Helper;

/// <summary>How a request to open a folder in the user session ended.</summary>
public enum OpenFolderOutcome
{
    Opened,
    NoHelper,
    /// <summary>The helper never acked in time. Explorer may still appear, so this is not a failure.</summary>
    TimedOut,
    Failed,
}

public static class OpenFolderOutcomes
{
    public static OpenFolderOutcome FromResult(bool ok, string? error) =>
        ok ? OpenFolderOutcome.Opened
        : error == "timeout" ? OpenFolderOutcome.TimedOut // HelperConnection.SendCommandAsync's ack-timeout error
        : OpenFolderOutcome.Failed;

    /// <summary>True when the caller should report the folder as not opened.</summary>
    public static bool IsFailure(OpenFolderOutcome outcome) =>
        outcome is OpenFolderOutcome.NoHelper or OpenFolderOutcome.Failed;
}
