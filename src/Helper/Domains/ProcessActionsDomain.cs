#if WINDOWS
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Platform.Windows;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

public sealed class ProcessKillRequest { public string Name { get; set; } = ""; }
public sealed class ProcessKillResult { public int Killed { get; set; } public int Failed { get; set; } }
public sealed class ProcessOpenLocationRequest { public string ExePath { get; set; } = ""; }
public sealed class ProcessOpenLocationResult { public bool Ok { get; set; } }
public sealed class ProcessActivateWindowRequest { public int Pid { get; set; } }
public sealed class ProcessActivateWindowResult { public bool Ok { get; set; } }
public sealed class ProcessEndSessionRequest { public int[] Pids { get; set; } = Array.Empty<int>(); }
public sealed class ProcessEndSessionResult { public int Windows { get; set; } public int Accepted { get; set; } }

/// <summary>
/// Service-side outbound facade for the monitoring sidebar's process kill and
/// reveal-in-Explorer actions. Both run in the user-session helper so the OS
/// enforces the console user's own privileges - the LocalSystem service never
/// kills or launches Explorer directly (see ConflictRoutes for the sibling
/// "kill by vetted catalog id" path this deliberately does not replace: that
/// one still runs LocalSystem-side, because it only ever targets processes
/// the catalog names, never an arbitrary caller-supplied one).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessActionsCommands
{
    public static async Task<ProcessKillResult?> KillAsync(HelperRegistry r, string processName, CancellationToken ct = default)
    {
        var conn = r.GetAny();
        if (conn is null)
        {
            return null;
        }

        var result = await conn.SendCommandAsync(
            "process.kill", new ProcessKillRequest { Name = processName },
            AppJsonContext.Default.ProcessKillRequest, timeoutMs: 8000, ct: ct).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.ProcessKillResult);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> OpenLocationAsync(HelperRegistry r, string exePath, CancellationToken ct = default)
    {
        var conn = r.GetAny();
        if (conn is null)
        {
            return false;
        }

        var result = await conn.SendCommandAsync(
            "process.openLocation", new ProcessOpenLocationRequest { ExePath = exePath },
            AppJsonContext.Default.ProcessOpenLocationRequest, timeoutMs: 6000, ct: ct).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.ProcessOpenLocationResult);
            return parsed?.Ok ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks the helper to send the session-end messages to the top-level windows of <paramref name="pids"/>; the windows found and the ones that accepted, or null when no helper answered.</summary>
    /// <summary>Per window: the end message is sent after the query, each up to 3 s, and a target may have a dozen windows.</summary>
    private const int EndSessionTimeoutMs = 60_000;

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    private static int SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out var sid) ? (int)sid : -1;

    /// <summary>Asks the helper of each target's own session to send the session-end messages to the top-level windows of <paramref name="pids"/>; the windows found and accepted, or null when no helper answered.</summary>
    public static async Task<ProcessEndSessionResult?> EndSessionAsync(HelperRegistry r, int[] pids, CancellationToken ct = default)
    {
        ProcessEndSessionResult? total = null;
        foreach (var group in pids.GroupBy(SessionOf))
        {
            var conn = group.Key >= 0 ? r.GetForSession(group.Key) : null;
            if (conn is null)
            {
                continue;
            }

            var result = await conn.SendCommandAsync(
                "process.endSession", new ProcessEndSessionRequest { Pids = group.ToArray() },
                AppJsonContext.Default.ProcessEndSessionRequest, timeoutMs: EndSessionTimeoutMs, ct: ct).ConfigureAwait(false);
            if (!result.Ok || result.Payload is null)
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.ProcessEndSessionResult);
                if (parsed is null)
                {
                    continue;
                }
                total ??= new ProcessEndSessionResult();
                total.Windows += parsed.Windows;
                total.Accepted += parsed.Accepted;
            }
            catch
            {
            }
        }
        return total;
    }

    public static async Task<bool> ActivateWindowAsync(HelperRegistry r, int pid, CancellationToken ct = default)
    {
        var conn = r.GetAny();
        if (conn is null)
        {
            return false;
        }

        var result = await conn.SendCommandAsync(
            "process.activateWindow", new ProcessActivateWindowRequest { Pid = pid },
            AppJsonContext.Default.ProcessActivateWindowRequest, timeoutMs: 4000, ct: ct).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.ProcessActivateWindowResult);
            return parsed?.Ok ?? false;
        }
        catch
        {
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProcessActionsHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("process.kill", (env, _) =>
        {
            var name = ReadKillReq(env).Name;
            var (killed, failed) = ProcessActionGuards.ShouldRefuseKill(name)
                ? (0, 0)
                : ProcessKiller.KillAllCounted(name);
            return Reply(env, new ProcessKillResult { Killed = killed, Failed = failed }, AppJsonContext.Default.ProcessKillResult);
        });

        registry.Register("process.openLocation", (env, _) =>
        {
            var path = ReadOpenLocationReq(env).ExePath;
            var ok = false;
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                try { ForegroundNudge.OpenFolderAndSelectOverApp(path); ok = true; }
                catch { ok = false; }
            }
            return Reply(env, new ProcessOpenLocationResult { Ok = ok }, AppJsonContext.Default.ProcessOpenLocationResult);
        });

        registry.Register("process.endSession", (env, _) =>
        {
            var pids = ReadEndSessionReq(env).Pids;
            var windows = 0;
            var accepted = 0;
            foreach (var pid in pids)
            {
                if (pid <= 0 || IsRefusedPid(pid)) continue;
                var (found, took) = SessionEnd.SendToProcess(pid);
                windows += found;
                accepted += took;
            }
            return Reply(env, new ProcessEndSessionResult { Windows = windows, Accepted = accepted }, AppJsonContext.Default.ProcessEndSessionResult);
        });

        registry.Register("process.activateWindow", (env, _) =>
        {
            var pid = ReadActivateWindowReq(env).Pid;
            var ok = false;
            if (pid > 0)
            {
                var hwnd = FindTopLevelWindowForPid(pid);
                if (hwnd != IntPtr.Zero)
                {
                    ForegroundNudge.TryForeground(hwnd);
                    ok = true;
                }
            }
            return Reply(env, new ProcessActivateWindowResult { Ok = ok }, AppJsonContext.Default.ProcessActivateWindowResult);
        });
    }

    /// <summary>First visible, unowned top-level window belonging to pid, or Zero. An on-demand scan (one Recent Apps press, not a poll loop) - unlike WindowSetPoller this skips the cloak/title classification since any window for the target process is worth foregrounding.</summary>
    private static IntPtr FindTopLevelWindowForPid(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindowsProc proc = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != (uint)pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }
            found = hwnd;
            return false;
        };
        EnumWindows(proc, IntPtr.Zero);
        return found;
    }

    private static bool IsRefusedPid(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return ProcessActionGuards.ShouldRefuseKill(p.ProcessName);
        }
        catch
        {
            return true;
        }
    }

    private static ProcessEndSessionRequest ReadEndSessionReq(HelperEnvelope env)
        => env.Payload is null
            ? new ProcessEndSessionRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProcessEndSessionRequest) ?? new ProcessEndSessionRequest();

    private static ProcessKillRequest ReadKillReq(HelperEnvelope env)
        => env.Payload is null
            ? new ProcessKillRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProcessKillRequest) ?? new ProcessKillRequest();

    private static ProcessOpenLocationRequest ReadOpenLocationReq(HelperEnvelope env)
        => env.Payload is null
            ? new ProcessOpenLocationRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProcessOpenLocationRequest) ?? new ProcessOpenLocationRequest();

    private static ProcessActivateWindowRequest ReadActivateWindowReq(HelperEnvelope env)
        => env.Payload is null
            ? new ProcessActivateWindowRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProcessActivateWindowRequest) ?? new ProcessActivateWindowRequest();

    private static Task<HelperResult> Reply<T>(HelperEnvelope env, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => Task.FromResult(new HelperResult
        {
            Id = env.Id ?? "",
            Ok = true,
            Payload = JsonSerializer.SerializeToElement(value, typeInfo),
        });

    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
#endif
