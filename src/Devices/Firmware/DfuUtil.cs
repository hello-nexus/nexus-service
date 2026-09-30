using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Devices.Firmware;

/// <summary>Result of a dfu-util invocation: exit code + captured stdout/stderr.</summary>
public sealed record DfuUtilResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Thin wrapper around the bundled <c>dfu-util</c> binary. All HYTE DFU
/// devices enumerate as VID 3402 / PID 0A00 in bootloader mode and flash at
/// app base 0x0800C000 (DfuSe address syntax). The argument builders are
/// separated from process execution so they can be unit-tested without the
/// binary or a device present.
///
/// dfu-util has no built-in verify, so the caller does upload-then-compare;
/// "erase sector 63" becomes a 16-byte 0xFF write at the boot-flag address.
/// </summary>
public sealed class DfuUtil
{
    public const string DfuVidPid = "3402:0a00";
    public const uint AppBaseAddress = 0x0800C000;
    public const uint BootFlagAddress = 0x0801FFF0;

    private readonly string _exePath;

    public DfuUtil(string exePath)
    {
        _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
    }

    /// <summary>Resolve the bundled dfu-util next to the service binary (Windows), else rely on PATH.</summary>
    public static string ResolveDefaultPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(AppContext.BaseDirectory, "tools", "dfu-util", "dfu-util.exe");
        // Linux/macOS: packaged or on PATH.
        return "dfu-util";
    }

    public bool BinaryExists => _exePath == "dfu-util" || File.Exists(_exePath);

    // ── Argument builders (pure, unit-tested) ──

    /// <summary><c>-l</c>: list attached DFU devices.</summary>
    public static string[] ListArgs() => new[] { "-l" };

    /// <summary><c>-D file -d 3402:0a00 -a 0 -s 0xADDR[:leave]</c>: download (flash) a raw bin.</summary>
    public static string[] DownloadArgs(string binPath, uint address, bool leave) => new[]
    {
        "-d", DfuVidPid,
        "-a", "0",
        "-s", $"0x{address:X}{(leave ? ":leave" : "")}",
        "-D", binPath,
    };

    /// <summary><c>-U file -d 3402:0a00 -a 0 -s 0xADDR:LEN</c>: upload (read back) LEN bytes for verify.</summary>
    public static string[] UploadArgs(string outPath, uint address, int length) => new[]
    {
        "-d", DfuVidPid,
        "-a", "0",
        "-s", $"0x{address:X}:{length}",
        "-U", outPath,
    };

    /// <summary>Count VID:PID matches in `dfu-util -l` output (each "Found DFU" line for our id).</summary>
    public static int CountDfuDevices(string listOutput)
    {
        if (string.IsNullOrEmpty(listOutput)) return 0;
        return Regex.Matches(listOutput, @"\[3402:0a00\]", RegexOptions.IgnoreCase).Count;
    }

    /// <summary>The last <paramref name="count"/> non-empty lines of dfu-util output, joined for a log line.</summary>
    public static string TailLines(string output, int count)
    {
        if (string.IsNullOrEmpty(output)) return "";
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - count)));
    }

    // ── Execution ──

    public Task<DfuUtilResult> ListAsync(CancellationToken ct) => RunAsync(ListArgs(), TimeSpan.FromSeconds(15), ct);

    public Task<DfuUtilResult> DownloadAsync(string binPath, uint address, bool leave, CancellationToken ct)
        => RunAsync(DownloadArgs(binPath, address, leave), TimeSpan.FromMinutes(3), ct);

    public Task<DfuUtilResult> UploadAsync(string outPath, uint address, int length, CancellationToken ct)
        => RunAsync(UploadArgs(outPath, address, length), TimeSpan.FromMinutes(2), ct);

    private async Task<DfuUtilResult> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            lock (sb) sb.AppendLine($"[dfu-util] timed out after {timeout.TotalSeconds:0}s");
            return new DfuUtilResult(-1, sb.ToString());
        }

        string output;
        lock (sb) output = sb.ToString();
        return new DfuUtilResult(proc.ExitCode, output);
    }
}
