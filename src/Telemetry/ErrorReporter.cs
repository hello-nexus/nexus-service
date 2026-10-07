using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Telemetry;

internal static class ErrorKinds
{
    public const string Crash = "crash";
    public const string UnobservedTask = "unobserved-task";
    public const string Logged = "logged";
    public const string Request = "request";
    public const string ReportedMarker = "nexus.error.reported";
}

/// <summary>
/// Aggregates unhandled errors by fingerprint and delivers them to nexus-api in
/// small batches. Honors the anonymous-data opt-out at the source (while off:
/// no-op, state cleared, crash file removed). Never throws and never recurses.
/// </summary>
internal sealed partial class ErrorReporter
{
    internal const int MaxDistinct = 100;
    internal const int MaxPerPost = 20;
    internal const int MaxCrashFileBytes = 64 * 1024;
    internal static readonly TimeSpan ResendWindow = TimeSpan.FromHours(1);

    private const int KindMax = 32, FingerprintMax = 64, TypeMax = 128, MessageMax = 1000, StackMax = 8000, ContextMax = 500;
    private const int FrameCount = 5;

    private readonly IConfigStore _store;
    private readonly IErrorTransport _transport;
    private readonly TimeProvider _clock;
    private readonly string _crashFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, ErrorReportItem> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private volatile bool _enabled;

    [ThreadStatic] private static bool t_inReport;

    public ErrorReporter(IConfigStore store, IErrorTransport transport)
        : this(store, transport, TimeProvider.System, DefaultCrashFile())
    {
    }

    internal ErrorReporter(IConfigStore store, IErrorTransport transport, TimeProvider clock, string crashFile)
    {
        _store = store;
        _transport = transport;
        _clock = clock;
        _crashFile = crashFile;
        _enabled = store.Load().Telemetry.CollectAnonymousData;
        _store.OnChanged += OnSettingsChanged;
    }

    private static string DefaultCrashFile() =>
        Path.Combine(Media.MediaLibrary.NexusDataDir(), "error-crash.json");

    internal int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    private void OnSettingsChanged()
    {
        try
        {
            var enabled = _store.Load().Telemetry.CollectAnonymousData;
            _enabled = enabled;
            if (!enabled)
                ClearState();
        }
        catch
        {
            // Settings callbacks must not throw into the store.
        }
    }

    private void ClearState()
    {
        lock (_lock)
        {
            _pending.Clear();
            _lastSent.Clear();
        }
        try { File.Delete(_crashFile); } catch { /* best-effort */ }
    }

    /// <summary>Records a service-side exception. Safe to call from anywhere, including failure paths.</summary>
    public void Report(Exception ex, string kind, string? context)
    {
        if (!_enabled || t_inReport)
            return;
        t_inReport = true;
        try
        {
            Add(BuildItem(ex, kind, context));
        }
        catch (Exception inner)
        {
            Console.Error.WriteLine($"[error-report] report failed: {inner.GetType().Name}");
        }
        finally
        {
            t_inReport = false;
        }
    }

    /// <summary>Records an error the browser already serialized (source "web").</summary>
    public void ReportClient(string kind, string fingerprint, string type, string message, string stack, string? context, int count)
    {
        if (!_enabled || t_inReport)
            return;
        t_inReport = true;
        try
        {
            var now = _clock.GetUtcNow();
            Add(new ErrorReportItem
            {
                Source = "web",
                Kind = Cap(kind, KindMax),
                Fingerprint = Cap(fingerprint, FingerprintMax),
                Type = string.IsNullOrWhiteSpace(type) ? "Error" : Cap(Scrub(type), TypeMax),
                Message = Cap(Scrub(message), MessageMax),
                Stack = Cap(Scrub(stack), StackMax),
                Context = Cap(Scrub(context ?? ""), ContextMax),
                Count = Math.Max(1, count),
                FirstSeen = now,
                LastSeen = now,
            });
        }
        catch (Exception inner)
        {
            Console.Error.WriteLine($"[error-report] report failed: {inner.GetType().Name}");
        }
        finally
        {
            t_inReport = false;
        }
    }

    internal static string DefaultCrashFilePath() => DefaultCrashFile();

    /// <summary>Called as the process dies, regardless of consent (the file never leaves the machine until a flush finds consent on): writes one bounded file synchronously; the next start's first flush delivers and deletes it.</summary>
    public static void WriteCrashFile(Exception ex, string path)
    {
        if (t_inReport)
            return;
        t_inReport = true;
        try
        {
            var json = JsonSerializer.Serialize(BuildItem(ex, ErrorKinds.Crash, null, TimeProvider.System), AppJsonContext.Default.ErrorReportItem);
            if (Encoding.UTF8.GetByteCount(json) > MaxCrashFileBytes)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception inner)
        {
            Console.Error.WriteLine($"[error-report] crash write failed: {inner.GetType().Name}");
        }
        finally
        {
            t_inReport = false;
        }
    }

    private void Add(ErrorReportItem item)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(item.Fingerprint, out var existing))
            {
                existing.Count += item.Count;
                existing.LastSeen = item.LastSeen;
                return;
            }
            if (_pending.Count >= MaxDistinct)
                return;
            _pending[item.Fingerprint] = item;
        }
    }

    /// <summary>One delivery pass. Sends at most <see cref="MaxPerPost"/> fingerprints; a failed send drops them.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await _flushGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_enabled)
                {
                    ClearState();
                    return;
                }
                await FlushCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _flushGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error-report] flush failed: {ex.GetType().Name}");
        }
    }

    private async Task FlushCoreAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var batch = new List<ErrorReportItem>();
        var crash = ReadCrashFile();
        if (crash is not null)
            batch.Add(crash);

        lock (_lock)
        {
            foreach (var key in _lastSent.Where(kv => now - kv.Value >= ResendWindow).Select(kv => kv.Key).ToList())
                _lastSent.Remove(key);

            foreach (var item in _pending.Values.OrderBy(i => i.FirstSeen).ToList())
            {
                if (batch.Count >= MaxPerPost)
                    break;
                // Held back (keeps aggregating) until an hour has passed since its last send.
                if (_lastSent.TryGetValue(item.Fingerprint, out var sent) && now - sent < ResendWindow)
                    continue;
                if (batch.Any(b => b.Fingerprint == item.Fingerprint))
                    continue;
                batch.Add(item);
                _pending.Remove(item.Fingerprint);
                _lastSent[item.Fingerprint] = now;
            }
        }

        // One malformed item would 400 the whole batch at nexus-api; drop it instead.
        batch.RemoveAll(i => string.IsNullOrWhiteSpace(i.Kind) || string.IsNullOrWhiteSpace(i.Fingerprint) || string.IsNullOrWhiteSpace(i.Type));
        if (batch.Count == 0)
            return;
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var payload = new ErrorReportPayload
        {
            InstallId = installId,
            Version = BuildInfo.Version,
            Os = TelemetryPlatform.OsTag(),
            OsVersion = RuntimeInformation.OSDescription,
#if DEV_TOOLS
            DevTools = true,
#endif
            Errors = batch,
        };
        var ok = await _transport.SendAsync(payload, ct).ConfigureAwait(false);
        if (ok && crash is not null)
        {
            try { File.Delete(_crashFile); } catch { /* best-effort */ }
        }
    }

    private ErrorReportItem? ReadCrashFile()
    {
        try
        {
            var info = new FileInfo(_crashFile);
            if (!info.Exists)
                return null;
            if (info.Length > MaxCrashFileBytes)
            {
                info.Delete();
                return null;
            }
            var item = JsonSerializer.Deserialize(File.ReadAllText(_crashFile), AppJsonContext.Default.ErrorReportItem);
            if (item is null || item.Fingerprint.Length == 0)
            {
                info.Delete();
                return null;
            }
            return item;
        }
        catch
        {
            try { File.Delete(_crashFile); } catch { /* best-effort */ }
            return null;
        }
    }

    internal ErrorReportItem BuildItem(Exception ex, string kind, string? context) => BuildItem(ex, kind, context, _clock);

    private static ErrorReportItem BuildItem(Exception ex, string kind, string? context, TimeProvider clock)
    {
        var raw = ex.ToString();
        var type = ex.GetType().FullName ?? ex.GetType().Name;
        var now = clock.GetUtcNow();
        return new ErrorReportItem
        {
            Source = "service",
            Kind = Cap(kind, KindMax),
            Fingerprint = Fingerprint("service", type, raw),
            Type = Cap(type, TypeMax),
            Message = Cap(Scrub(ex.Message), MessageMax),
            Stack = Cap(Scrub(raw), StackMax),
            Context = Cap(Scrub(context ?? ""), ContextMax),
            Count = 1,
            FirstSeen = now,
            LastSeen = now,
        };
    }

    /// <summary>Hash of source, type and the top frames' method names; offsets, line numbers and arguments never contribute.</summary>
    internal static string Fingerprint(string source, string type, string stack)
    {
        var frames = FrameRegex().Matches(stack).Take(FrameCount).Select(m => m.Groups[1].Value);
        var input = source + "\n" + type + "\n" + string.Join("\n", frames);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..32];
    }

    internal static string Scrub(string text)
    {
        if (text.Length == 0)
            return text;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
        {
            text = text.Replace(profile, "~", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        text = EmailRegex().Replace(text, "<email>");
        text = Ipv4Regex().Replace(text, "<ip>");
        var user = Environment.UserName;
        if (user.Length >= 3)
            text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"^\s*at\s+([^\s(]+)", RegexOptions.Multiline)]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();
}
