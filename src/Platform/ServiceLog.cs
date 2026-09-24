using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform;

/// <summary>
/// Routes Console.Out and Console.Error through a TextWriter that also writes
/// to a rotating nexus-service.log file under per-platform LocalAppData. Captures
/// the existing 170+ Console.Error.WriteLine call sites without touching them, so
/// crash context survives off the user's machine without provider-by-provider
/// migration to ILogger&lt;T&gt;.
///
/// Each fresh log opens with a "Nexus &lt;version&gt;" header line.
///
/// Rotation: on every service start the previous run's nexus-service.log is
/// archived to nexus-service-&lt;timestamp&gt;.log and a fresh nexus-service.log is
/// begun, so each run has its own log instead of accumulating across restarts. The newest
/// <see cref="MaxRotatedLogs"/> archives are kept; older ones are deleted. Best
/// effort, not audit logging.
/// </summary>
public static class ServiceLog
{
    /// <summary>Number of timestamped, rotated-out logs kept; older ones are deleted.</summary>
    public const int MaxRotatedLogs = 5;

    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static string? _path;

    // The real console streams, captured before the tee is installed. Explicit
    // Info/Warn/Error writes go here directly (not through the tee) so the file
    // line carries the intended level instead of the tee's stream-derived one.
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;

    public static string? LogFilePath => _path;

    /// <summary>
    /// Directory holding nexus-service.log (and nexus-overlay.log on Windows). Resolves
    /// even before <see cref="Initialize"/> runs, so the open-logs endpoint works
    /// regardless of init order.
    /// </summary>
    public static string LogsDirectory =>
        _path is not null ? Path.GetDirectoryName(_path)! : ResolveLogsDir();

    public static void Initialize()
    {
        // Re-entry would tee the tee: each call captures the current Console.Out
        // as its primary, so N calls cost N locked file writes per console write.
        if (_writer is not null)
            return;

        try
        {
            var dir = ResolveLogsDir();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "nexus-service.log");
            RotatePreviousRun(_path);
            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            // First line of every fresh log records the build, so a shipped log
            // is self-identifying without cross-referencing the install.
            _writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} INF Nexus {BuildInfo.Version}");

            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(new TeeTextWriter(Console.Out, isError: false));
            Console.SetError(new TeeTextWriter(Console.Error, isError: true));
            Console.Out.WriteLine($"[service-log] writing to {_path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[service-log] init failed: {ex.Message}");
        }
    }

    /// <summary>Uninstalls the Console tee and closes the log, so a test exercising Initialize does not leave it installed for the rest of the process.</summary>
    internal static void ResetForTests()
    {
        StreamWriter? doomed;
        lock (Lock)
        {
            if (_originalOut is not null) Console.SetOut(_originalOut);
            if (_originalError is not null) Console.SetError(_originalError);
            // Clear the field under the lock before disposing: a tee still held
            // by another thread resolves the writer through this field on every
            // write, so nulling it first closes the use-after-dispose window.
            doomed = _writer;
            _writer = null;
            _originalOut = null;
            _originalError = null;
            _path = null;
        }
        doomed?.Dispose();
    }

    /// <summary>Appends to the log if one is open. Resolves the writer under the lock so a concurrent reset cannot leave a caller holding a disposed one.</summary>
    private static void WriteToFile(string text, bool newLine)
    {
        lock (Lock)
        {
            if (_writer is null)
                return;
            if (newLine) _writer.WriteLine(text);
            else _writer.Write(text);
        }
    }

    /// <summary>Routine status (lifecycle, connect, discovery) - logged at INF.</summary>
    public static void Info(string message) => Write("INF", message, isError: false);

    /// <summary>A handled anomaly worth surfacing but not a failure - logged at WRN.</summary>
    public static void Warn(string message) => Write("WRN", message, isError: false);

    /// <summary>A real failure - logged at ERR (same destination as Console.Error).</summary>
    public static void Error(string message) => Write("ERR", message, isError: true);

    private static void Write(string level, string message, bool isError)
    {
        // Mirror to the ORIGINAL console stream (not the tee), then write the file
        // line ourselves with the explicit level - otherwise the tee would re-stamp
        // it from the stream and double-prefix. Before Initialize, fall back to the
        // current Console (no file yet).
        var console = isError ? _originalError ?? Console.Error : _originalOut ?? Console.Out;
        if (isError)
        {
            console.WriteLine(message);
        }
        else
        {
            // Unix ConsolePal locks Console.Out (the tee) around every console
            // write, so the mirror must take it before the original writer or
            // it deadlocks against a Console.Out.WriteLine in flight. The error
            // path already orders its own writer before Console.Out.
            lock (Console.Out)
            {
                console.WriteLine(message);
            }
        }

        WriteToFile($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}", newLine: true);
    }

    private static string ResolveLogsDir()
    {
        // Root system daemon: logs under the machine root, like %ProgramData%
        // on Windows. The per-user path would follow HOME, which is root's
        // before login and the user's after - two log trees for one daemon.
        if (Persistence.NexusDataPaths.SystemDaemonRoot is { } daemonRoot)
            return Path.Combine(daemonRoot, "logs");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, "Library", "Logs", "Nexus");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, ".local", "state", "nexus", "logs");
        }
        // Windows: machine-scope logs under %ProgramData% so the LocalSystem
        // service can write them and an admin can inspect them post-incident.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "Nexus", "logs");
    }

    private static void RotatePreviousRun(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            // Skip an empty file (e.g. a crash-restart that never logged) so we
            // don't spend an archive slot on nothing.
            if (new FileInfo(path).Length == 0)
                return;

            // Archive the previous run to nexus-service-<localtimestamp>.log so each
            // run gets its own log; names sort chronologically. Keep newest MaxRotatedLogs.
            var dir = Path.GetDirectoryName(path)!;
            var rotated = Path.Combine(dir, $"nexus-service-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (File.Exists(rotated))
                File.Delete(rotated);
            File.Move(path, rotated);
            PruneOldRotations(dir);
        }
        catch { /* best effort */ }
    }

    private static void PruneOldRotations(string dir)
    {
        try
        {
            // The yyyyMMdd-HHmmss stamp makes the names sort oldest-first by
            // ordinal, so delete everything before the last MaxRotatedLogs.
            var rotated = Directory.GetFiles(dir, "nexus-service-*.log");
            if (rotated.Length <= MaxRotatedLogs)
                return;
            Array.Sort(rotated, StringComparer.Ordinal);
            for (var i = 0; i < rotated.Length - MaxRotatedLogs; i++)
                File.Delete(rotated[i]);
        }
        catch { /* best effort */ }
    }

    // Resolves the log writer through ServiceLog on each write rather than
    // capturing it, so a reset that closes the log cannot strand this writer
    // holding a disposed one.
    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly bool _isError;

        public TeeTextWriter(TextWriter primary, bool isError)
        {
            _primary = primary;
            _isError = isError;
        }

        public override System.Text.Encoding Encoding => _primary.Encoding;

        public override void Write(char value)
        {
            _primary.Write(value);
            WriteToFile(value.ToString(), newLine: false);
        }

        public override void Write(string? value)
        {
            _primary.Write(value);
            WriteToFile(value ?? string.Empty, newLine: false);
        }

        public override void WriteLine(string? value)
        {
            _primary.WriteLine(value);
            var prefix = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {(_isError ? "ERR" : "INF")} ";
            WriteToFile(prefix + (value ?? string.Empty), newLine: true);
        }

        public override void WriteLine()
        {
            _primary.WriteLine();
            WriteToFile(string.Empty, newLine: true);
        }
    }
}
