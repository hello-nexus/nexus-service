using System;
using System.IO;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Last resort after a resume that left the SMBus mutex held and the OpenRGB daemon unable to settle: restarts the whole
/// service through the same detached finalizer as the restart route. The stamp survives the restart so a mutex that is
/// held again after the new process starts cannot loop the restarts.
/// </summary>
public sealed class ResumeSelfRestart
{
    internal static readonly TimeSpan LoopGuard = TimeSpan.FromMinutes(30);

    private readonly string _stampPath;
    private readonly string _startedMarkerPath;
    private readonly Func<bool> _restart;
    private readonly Func<DateTime> _utcNow;

    internal ResumeSelfRestart(string stampPath, Func<bool> restart, Func<DateTime>? utcNow = null)
    {
        _stampPath = stampPath;
        _startedMarkerPath = stampPath + ".started";
        _restart = restart;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Returns true when a restart was started; false inside the loop guard window or when the restart itself refused.</summary>
    internal bool TryRestart(string context)
    {
        var now = _utcNow();
        if (ReadStamp() is { } last && now - last < LoopGuard)
        {
            ServiceLog.Warn($"[resume] service self-restart skipped: one already ran {(now - last).TotalMinutes:F0} min ago ({context})");
            return false;
        }
        // Written before the restart so the new process sees it; with no stamp the loop guard cannot hold.
        if (!WriteFiles(now))
        {
            return false;
        }
        ServiceLog.Warn($"[resume] restarting the service to release the SMBus mutex ({context})");
        if (_restart())
        {
            return true;
        }
        ServiceLog.Warn("[resume] service self-restart refused (a firmware update or another restart is in progress)");
        DeleteFiles();
        return false;
    }

    /// <summary>Called once at startup: logs when this process is the product of a self-restart.</summary>
    internal void LogStartMarker()
    {
        try
        {
            if (!File.Exists(_startedMarkerPath))
            {
                return;
            }
            File.Delete(_startedMarkerPath);
            ServiceLog.Info("[resume] started after a resume self-restart");
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[resume] start marker unreadable: {ex.Message}");
        }
    }

    private DateTime? ReadStamp()
    {
        try
        {
            return long.TryParse(File.ReadAllText(_stampPath).Trim(), out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool WriteFiles(DateTime now)
    {
        try
        {
            File.WriteAllText(_stampPath, now.Ticks.ToString());
            File.WriteAllText(_startedMarkerPath, "");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[resume] service self-restart skipped, stamp not written: {ex.Message}");
            return false;
        }
    }

    private void DeleteFiles()
    {
        try
        {
            File.Delete(_stampPath);
            File.Delete(_startedMarkerPath);
        }
        catch (IOException)
        {
        }
    }
}
