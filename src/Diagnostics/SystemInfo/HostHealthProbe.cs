using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nexus.Service.Diagnostics.SystemInfo;

/// <summary>
/// Non-Windows host facts behind the Memory and System health tiles.
/// <see cref="Supported"/> is false off Linux (macOS): both tiles then report
/// no health source. On Linux, <see cref="FailedUnits"/> are failed system
/// scope systemd units (null when systemctl could not be read, which is not
/// the same as none failed), <see cref="HardwareCorruptedKb"/> is the kernel's
/// poisoned-page total and the two EDAC counts sum the corrected and
/// uncorrected memory controller error counters.
/// </summary>
public sealed record HostHealthSnapshot(
    bool Supported,
    IReadOnlyList<string>? FailedUnits,
    long HardwareCorruptedKb,
    long EdacCorrected,
    long EdacUncorrected)
{
    public static readonly HostHealthSnapshot Unsupported = new(false, null, 0, 0, 0);
}

/// <summary>
/// Reads systemd failed units, /proc/meminfo HardwareCorrupted and EDAC counters
/// on Linux. Cached like the Windows pnp scan, so the health
/// page never spawns systemctl per request. Kernel taint is deliberately not
/// read: a proprietary GPU driver sets it on healthy machines.
/// </summary>
public sealed class HostHealthProbe
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const int SystemctlTimeoutMs = 5000;

    private readonly Func<HostHealthSnapshot> _scan;
    private readonly object _gate = new();
    private HostHealthSnapshot _cached = HostHealthSnapshot.Unsupported;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public HostHealthProbe()
        : this(ScanLinux)
    {
    }

    internal HostHealthProbe(Func<HostHealthSnapshot> scan)
    {
        _scan = scan;
    }

    /// <summary>Null on Windows, which has its own sources.</summary>
    public HostHealthSnapshot? Snapshot()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        if (!OperatingSystem.IsLinux())
        {
            return HostHealthSnapshot.Unsupported;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }
            _cached = _scan();
            _cachedAtUtc = now;
            return _cached;
        }
    }

    private static HostHealthSnapshot ScanLinux()
    {
        var failed = ReadFailedUnits(args => DiagnosticsShell.Run("systemctl", SystemctlTimeoutMs, args));

        long corrupted = 0;
        try
        {
            corrupted = ParseHardwareCorruptedKb(File.ReadAllText("/proc/meminfo"));
        }
        catch
        {
            // unreadable meminfo reads as no signal
        }

        var (corrected, uncorrected) = ReadEdacErrors("/sys/devices/system/edac/mc");
        return new HostHealthSnapshot(true, failed, corrupted, corrected, uncorrected);
    }

    /// <summary>
    /// Failed system units, or null when systemctl is missing, times out or
    /// exits non-zero (the shell helper reports a spawn failure or timeout as
    /// exit -1): an unreadable source must never read as "none failed".
    /// --system keeps user-session autostart failures out; they are noise on a
    /// Wayland desktop and not the machine's health.
    /// </summary>
    internal static IReadOnlyList<string>? ReadFailedUnits(Func<string[], ShellResult> run)
    {
        try
        {
            var result = run(new[] { "--system", "--failed", "--no-legend", "--plain", "--no-pager" });
            return result.ExitCode == 0 ? ParseFailedUnits(result.Stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Unit names from <c>systemctl --failed --no-legend --plain</c> output.</summary>
    internal static IReadOnlyList<string> ParseFailedUnits(string output)
    {
        var units = new List<string>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // Newer systemd prefixes failed rows with a bullet token.
            var first = tokens.FirstOrDefault(t => t != "●" && t != "*" && t != "x");
            if (first is null || !first.Contains('.'))
            {
                continue;
            }
            // A failed row carries LOAD, ACTIVE, SUB columns after the name.
            if (!tokens.Contains("failed"))
            {
                continue;
            }
            units.Add(Unescape(first));
        }
        return units;
    }

    /// <summary>Reverses systemd's \xNN escaping in a unit name (a dash arrives as the four characters backslash x 2 d).</summary>
    internal static string Unescape(string unit) =>
        System.Text.RegularExpressions.Regex.Replace(
            unit, @"\\x([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>The HardwareCorrupted kB value, 0 when the line is absent.</summary>
    internal static long ParseHardwareCorruptedKb(string meminfo)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("HardwareCorrupted:", StringComparison.Ordinal))
            {
                continue;
            }
            var digits = new string(line["HardwareCorrupted:".Length..].Where(char.IsDigit).ToArray());
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb : 0;
        }
        return 0;
    }

    /// <summary>Summed mc*/ce_count and mc*/ue_count under the EDAC root; zeros when none exist.</summary>
    internal static (long Corrected, long Uncorrected) ReadEdacErrors(string edacRoot)
    {
        long corrected = 0;
        long uncorrected = 0;
        try
        {
            if (!Directory.Exists(edacRoot))
            {
                return (0, 0);
            }
            foreach (var mc in Directory.EnumerateDirectories(edacRoot, "mc*"))
            {
                foreach (var file in new[] { "ce_count", "ue_count" })
                {
                    var path = Path.Combine(mc, file);
                    if (File.Exists(path)
                        && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        if (file == "ce_count")
                        {
                            corrected += n;
                        }
                        else
                        {
                            uncorrected += n;
                        }
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }
        return (corrected, uncorrected);
    }
}
