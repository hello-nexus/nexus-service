using System.Linq;


namespace Nexus.Service.Diagnostics.Storage;

/// <summary>
/// How a drive whose SMART read did not finish in time is listed: a watch-level reason the health grid renders, instead of
/// a missing drive, a "no SMART source" note, or a stale Good.
/// </summary>
internal static class SmartNotResponding
{
    internal const string Code = "storage.notResponding";

    internal static SmartReason Reason(string name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "This drive" : name;
        return new SmartReason(
            Code,
            ReasonSeverity.Watch,
            $"{label} is not responding",
            "Windows is taking too long to read this drive, which usually means it is failing or its cable or port has problems. Its readings are paused until it responds.");
    }

    /// <summary>Identity only, for a drive never read successfully.</summary>
    internal static SmartDriveInfo Placeholder(string id, string name, string serial, string bus, ulong? sizeBytes) => new()
    {
        Id = id,
        Name = name,
        Serial = serial,
        Bus = bus,
        SizeBytes = sizeBytes,
        Status = "caution",
        StatusReasons = new[] { Code },
        DetailedReasons = new[] { Reason(name) },
    };

    /// <summary>The last full entry with the not-responding reason added; a good or unknown status is raised to caution so it cannot read as healthy.</summary>
    internal static SmartDriveInfo Annotate(SmartDriveInfo previous)
    {
        if (previous.StatusReasons.Contains(Code))
        {
            return previous;
        }
        return previous with
        {
            Status = previous.Status is "good" or "unknown" ? "caution" : previous.Status,
            StatusReasons = previous.StatusReasons.Append(Code).ToList(),
            DetailedReasons = previous.DetailedReasons.Append(Reason(previous.Name)).ToList(),
        };
    }
}
