using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Runs a tool as a host process: resolve the binary, adopt an instance left by a
/// prior service run, else spawn one. Single-instance per <c>ToolId</c>, killed on
/// service shutdown. A <see cref="ToolSession.User"/> tool launches cross-session
/// via the scheduled-task helper (Windows) and retains no handle.
/// </summary>
public sealed class HostExeInstallStrategy : IToolInstallStrategy
{
    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public ToolTarget Target => ToolTarget.HostExe;

    public async Task LaunchAsync(ExternalToolSpec spec, IToolResolver resolver, CancellationToken ct)
    {
        await _launchGate.WaitAsync(ct);
        try
        {
            if (IsRunning(spec.ToolId)) return;

            var path = await resolver.ResolveAsync(spec, ct);
            if (path is null)
            {
                _failed[spec.ToolId] = 1;
                ServiceLog.Warn($"[tools] {spec.ToolId}: no binary resolved; not launched");
                return;
            }

            // Adopt an instance left by a prior service run - a crash / hard-kill
            // skips StopAsync, so the previous driver process can still be alive.
            // Adopting (instead of spawning a duplicate) keeps exactly one driver
            // across restarts, and the next graceful stop still terminates it.
            var existing = FindRunningByImage(path);
            if (existing is not null)
            {
                _failed.TryRemove(spec.ToolId, out _);
                _running[spec.ToolId] = existing;
                ServiceLog.Info($"[tools] {spec.ToolId}: adopted already-running {Path.GetFileName(path)} pid={existing.Id}");
                return;
            }

            var proc = StartProcess(spec, path);
            if (proc is null)
            {
                _failed[spec.ToolId] = 1;
            }
            else
            {
                _failed.TryRemove(spec.ToolId, out _);
                _running[spec.ToolId] = proc;
            }
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public ToolStatus GetStatus(string toolId)
    {
        if (IsRunning(toolId)) return ToolStatus.Running;
        if (_failed.ContainsKey(toolId)) return ToolStatus.Failed;
        return ToolStatus.NotRunning;
    }

    public bool Terminate(string toolId)
    {
        if (!_running.TryRemove(toolId, out var proc)) return false;
        try
        {
            if (proc.HasExited)
            {
                return true;
            }
            proc.Kill(entireProcessTree: true);
            // The exit wait is the whole return value: callers that touch the
            // device afterwards need to know the process really let go of it, and
            // a kill can be refused or outlast the wait.
            if (!proc.WaitForExit(3000))
            {
                ServiceLog.Warn($"[tools] terminate {toolId}: still alive after kill");
                return false;
            }
            ServiceLog.Info($"[tools] terminated {toolId}");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tools] terminate {toolId}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    public void TerminateAll()
    {
        foreach (var id in _running.Keys.ToArray())
            Terminate(id);
    }

    /// <summary>
    /// Find a process already running the resolved binary (by image name), to adopt
    /// across a service restart rather than double-spawn. Returns the first match;
    /// extra handles are disposed (the processes are left alone).
    /// </summary>
    private static Process? FindRunningByImage(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var matches = Process.GetProcessesByName(name);
            if (matches.Length == 0) return null;
            for (var i = 1; i < matches.Length; i++) matches[i].Dispose();
            return matches[0];
        }
        catch
        {
            return null;
        }
    }

    private bool IsRunning(string toolId)
    {
        if (_running.TryGetValue(toolId, out var proc))
        {
            try { if (!proc.HasExited) return true; }
            catch { /* fall through to cleanup */ }
            if (_running.TryRemove(toolId, out var dead))
            {
                try { dead.Dispose(); } catch { /* ignore */ }
            }
        }
        return false;
    }

    private Process? StartProcess(ExternalToolSpec spec, string path)
    {
        try
        {
            if (spec.Launch.Session == ToolSession.User)
                return StartInUserSession(spec, path);

            // System session: launch directly in the service's own (LocalSystem,
            // Session 0) context so the tool runs pre-login. No "runas" verb -
            // LocalSystem is already maximally privileged.
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = spec.Launch.Hidden,
                WindowStyle = spec.Launch.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            var proc = Process.Start(psi);
            ServiceLog.Info($"[tools] launched {spec.ToolId} ({Path.GetFileName(path)}) pid={proc?.Id}");
            return proc;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[tools] launch {spec.ToolId} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private Process? StartInUserSession(ExternalToolSpec spec, string path)
    {
#if WINDOWS
        // Detached cross-session launch via the scheduled-task helper. No handle is
        // retained (schtasks detaches), so a user-session tool's status falls back to
        // NotRunning - image-name tracking is a follow-up. The default System session
        // does not use this path.
        Nexus.Service.Lifecycle.UserHelperBootstrapper.RunInUserSession(
            $"\"{path}\"", $"tools-{spec.ToolId}", "NexusTool");
        ServiceLog.Info($"[tools] launched {spec.ToolId} in user session (schtasks)");
        return null;
#else
        // Non-Windows has no Session-0/desktop split - launch directly.
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = spec.Launch.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        return Process.Start(psi);
#endif
    }
}
