using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Diagnostics.SystemInfo;

public sealed record PnpProblemDevice(string Name, string DeviceId, int ProblemCode, string ProblemText);

public sealed record PnpProblemSnapshot(bool Supported, IReadOnlyList<PnpProblemDevice> Devices)
{
    public static readonly PnpProblemSnapshot Unsupported = new(false, Array.Empty<PnpProblemDevice>());
}

/// <summary>
/// Scans Win32_PnPEntity for devices reporting a Device Manager problem code
/// (ConfigManagerErrorCode != 0, excluding the user-chosen CM_PROB_DISABLED)
/// via the existing powershell.exe shell-out
/// pattern (see LibreHardwareSensorProvider.GetStorageBrandModel). Windows-only;
/// self-gates on <see cref="OperatingSystem.IsWindows"/>. Result is cached for
/// 5 minutes - this is a diagnostics-page read, not a live poll.
/// </summary>
public sealed class PnpProblemScanner
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Floor below which a forceRefresh request is served from cache anyway, so
    // a stuck client retry loop cannot make this spawn powershell.exe continuously.
    private static readonly TimeSpan ForceRefreshFloor = TimeSpan.FromSeconds(5);
    private const int ShellTimeoutMs = 15_000;
    private const int CmProbDisabled = 22;

    private readonly object _gate = new();
    private PnpProblemSnapshot _cached = PnpProblemSnapshot.Unsupported;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public PnpProblemSnapshot Snapshot(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PnpProblemSnapshot.Unsupported;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var honorForce = forceRefresh && now - _cachedAtUtc >= ForceRefreshFloor;
            if (!honorForce && now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }

            _cached = Scan();
            _cachedAtUtc = now;
            return _cached;
        }
    }

    private static PnpProblemSnapshot Scan()
    {
        var result = DiagnosticsShell.Run("powershell.exe", ShellTimeoutMs,
            "-NoProfile", "-Command",
            "Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0' | " +
            "Select-Object Name,DeviceID,ConfigManagerErrorCode | ConvertTo-Json -Compress");
        return new PnpProblemSnapshot(true, ParseJson(result.Stdout));
    }

    /// <summary>Pure parse of the PowerShell ConvertTo-Json output. Handles the
    /// three shapes ConvertTo-Json can produce for this query: empty (no
    /// problem devices), a single bare object (exactly one), or an array.</summary>
    internal static IReadOnlyList<PnpProblemDevice> ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PnpProblemDevice>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new List<PnpProblemDevice>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    AddIfValid(el, result);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddIfValid(root, result);
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<PnpProblemDevice>();
        }
    }

    private static void AddIfValid(JsonElement el, List<PnpProblemDevice> result)
    {
        var name = el.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        var deviceId = el.TryGetProperty("DeviceID", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";

        int code = 0;
        if (el.TryGetProperty("ConfigManagerErrorCode", out var c))
        {
            if (c.ValueKind == JsonValueKind.Number) c.TryGetInt32(out code);
            else if (c.ValueKind == JsonValueKind.String) int.TryParse(c.GetString(), out code);
        }
        // A disabled device is a state the user chose, not a problem to report.
        if (code == 0 || code == CmProbDisabled) return;

        result.Add(new PnpProblemDevice(name, deviceId, code, ProblemCodeName(code)));
    }

    // Common Device Manager (cfgmgr32.h CM_PROB_*) codes. Unknown codes fall
    // back to a generic CM_PROB_<n> label rather than dropping the device.
    internal static string ProblemCodeName(int code) => code switch
    {
        1 => "CM_PROB_NOT_CONFIGURED",
        3 => "CM_PROB_OUT_OF_MEMORY",
        10 => "CM_PROB_FAILED_START",
        12 => "CM_PROB_NORMAL_CONFLICT",
        14 => "CM_PROB_NEED_RESTART",
        18 => "CM_PROB_REINSTALL",
        21 => "CM_PROB_WILL_BE_REMOVED",
        22 => "CM_PROB_DISABLED",
        24 => "CM_PROB_DEVICE_NOT_THERE",
        28 => "CM_PROB_FAILED_INSTALL",
        31 => "CM_PROB_FAILED_ADD",
        37 => "CM_PROB_FAILED_DRIVER_ENTRY",
        39 => "CM_PROB_DRIVER_FAILED_LOAD",
        43 => "CM_PROB_FAILED_POST_START",
        45 => "CM_PROB_PHANTOM",
        _ => $"CM_PROB_{code}",
    };
}
