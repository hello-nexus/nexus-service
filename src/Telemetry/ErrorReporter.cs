using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal const int MaxRaw = 200;
    internal const int MaxRawWeb = 50;
    internal const int MaxWebDistinct = 30;
    internal const int MaxCount = 1_000_000;
    internal const int MaxCrashFileBytes = 64 * 1024;
    internal static readonly TimeSpan ResendWindow = TimeSpan.FromHours(1);

    private const int KindMax = 32, FingerprintMax = 64, TypeMax = 128, MessageMax = 1000, StackMax = 4000, ContextMax = 500, LogMax = 4000;
    private const int FrameCount = 5;
    private const int OsVersionMax = 32;

    private readonly IConfigStore _store;
    private readonly IErrorTransport _transport;
    private readonly TimeProvider _clock;
    private readonly string _crashFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, ErrorReportItem> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ConcurrentQueue<RawReport> _raw = new();
    private readonly TokenBucket _serviceBucket;
    private readonly TokenBucket _webBucket;
    private readonly bool _capture;
    private int _rawCount;
    private int _rawWebCount;
    private long _dropped;
    private volatile bool _enabled;

    // Consent as seen by the crash hook, which runs before DI exists. Defaults to allowed so a startup crash is still written.
    private static volatile bool s_crashFileAllowed = true;

    internal static bool CrashFileAllowed => s_crashFileAllowed;

    [ThreadStatic] private static bool t_inReport;

    public ErrorReporter(IConfigStore store, IErrorTransport transport)
        : this(store, transport, TimeProvider.System, DefaultCrashFile(), 10, 5, Common.ClientCredential.IsOfficial)
    {
    }

    internal ErrorReporter(IConfigStore store, IErrorTransport transport, TimeProvider clock, string crashFile,
        int ratePerSecond = 1_000_000, int webRatePerSecond = 1_000_000, bool capture = true)
    {
        _serviceBucket = new TokenBucket(ratePerSecond);
        _webBucket = new TokenBucket(webRatePerSecond);
        _capture = capture;
        _store = store;
        _transport = transport;
        _clock = clock;
        _crashFile = crashFile;
        _enabled = store.Load().Telemetry.CollectAnonymousData;
        if (capture)
        {
            s_crashFileAllowed = _enabled;
            if (!_enabled)
            {
                try { File.Delete(_crashFile); } catch { /* best-effort */ }
            }
        }
        _store.OnChanged += OnSettingsChanged;
    }

    private static string DefaultCrashFile() =>
        Path.Combine(Persistence.NexusDataPaths.NexusRoot(), "error-crash.json");

    internal int PendingCount
    {
        get
        {
            Drain();
            lock (_lock) return _pending.Count;
        }
    }

    private void OnSettingsChanged()
    {
        try
        {
            var enabled = _store.Load().Telemetry.CollectAnonymousData;
            var was = _enabled;
            _enabled = enabled;
            if (_capture)
                s_crashFileAllowed = enabled;
            if (was && !enabled)
                ClearState();
        }
        catch
        {
            // Settings callbacks must not throw into the store.
        }
    }

    private void ClearState()
    {
        while (_raw.TryDequeue(out var dropped))
        {
            if (dropped.Exception is null)
                Interlocked.Decrement(ref _rawWebCount);
            else
                Interlocked.Decrement(ref _rawCount);
        }
        lock (_lock)
        {
            _pending.Clear();
            _lastSent.Clear();
        }
        try { File.Delete(_crashFile); } catch { /* best-effort */ }
    }

    /// <summary>Records a service-side exception. Safe to call from anywhere, including failure paths. Costs a rate-gate check and a queue push; fingerprinting, scrubbing and truncation happen at flush time.</summary>
    public void Report(Exception ex, string kind, string? context)
    {
        if (!_capture || !_enabled || !_serviceBucket.TryTake())
        {
            CountDrop();
            return;
        }
        Enqueue(new RawReport { Exception = ex, Kind = kind, Context = context, At = _clock.GetUtcNow() });
    }

    /// <summary>Records a 500 response no exception explained (a route returning Results.Problem); keyed on route, so repeats only raise the count.</summary>
    public void ReportStatus(int status, string route)
    {
        if (!_capture || !_enabled || !_serviceBucket.TryTake())
        {
            CountDrop();
            return;
        }
        var type = $"HTTP {status}";
        var now = _clock.GetUtcNow();
        Add(new ErrorReportItem
        {
            Source = "service",
            Kind = ErrorKinds.Request,
            Fingerprint = Hash("service\n" + type + "\n" + route),
            Type = type,
            Message = Cap($"{type} from {route}", MessageMax),
            Context = Cap(route, ContextMax),
            FirstSeen = now,
            LastSeen = now,
        });
    }

    /// <summary>Records an error the browser already serialized (source "web"); same cost profile as <see cref="Report"/>.</summary>
    public void ReportClient(string kind, string fingerprint, string type, string message, string stack, string? context, int count)
    {
        if (!_capture || !_enabled || !_webBucket.TryTake())
        {
            CountDrop();
            return;
        }
        Enqueue(new RawReport
        {
            Kind = kind, Fingerprint = fingerprint, Type = type, Message = message, Stack = stack, Context = context,
            Count = count, At = _clock.GetUtcNow(),
        });
    }

    /// <summary>Reports refused by the process-wide rate gate or a full raw queue.</summary>
    internal long DroppedCount => Interlocked.Read(ref _dropped);

    private void CountDrop()
    {
        if (_capture && _enabled)
            Interlocked.Increment(ref _dropped);
    }

    // Capacity and refill are both the per-second rate. A hot loop pays one short lock.
    private sealed class TokenBucket
    {
        private readonly object _gate = new();
        private readonly double _rate;
        private double _tokens;
        private long _last = Stopwatch.GetTimestamp();

        public TokenBucket(int ratePerSecond)
        {
            _rate = ratePerSecond;
            _tokens = ratePerSecond;
        }

        public bool TryTake()
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(_rate, _tokens + (now - _last) * _rate / Stopwatch.Frequency);
                _last = now;
                if (_tokens < 1)
                    return false;
                _tokens -= 1;
                return true;
            }
        }
    }

    private void Enqueue(RawReport raw)
    {
        var web = raw.Exception is null;
        if (web)
        {
            if (Interlocked.Increment(ref _rawWebCount) > MaxRawWeb)
            {
                Interlocked.Decrement(ref _rawWebCount);
                Interlocked.Increment(ref _dropped);
                return;
            }
        }
        else if (Interlocked.Increment(ref _rawCount) > MaxRaw)
        {
            Interlocked.Decrement(ref _rawCount);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _raw.Enqueue(raw);
    }

    // Turns queued raw reports into scrubbed, capped, fingerprinted items. Runs on the flush thread.
    private void Drain()
    {
        while (_raw.TryDequeue(out var r))
        {
            if (r.Exception is null)
                Interlocked.Decrement(ref _rawWebCount);
            else
                Interlocked.Decrement(ref _rawCount);
            try
            {
                Add(r.Exception is not null ? BuildItem(r.Exception, r.Kind, r.Context, r.At) : BuildClientItem(r));
            }
            catch (Exception inner)
            {
                Console.Error.WriteLine($"[error-report] item failed: {inner.GetType().Name}");
            }
        }
    }

    private static ErrorReportItem BuildClientItem(RawReport r) => new()
    {
        Source = "web",
        Kind = Cap(r.Kind, KindMax),
        Fingerprint = Cap(r.Fingerprint ?? "", FingerprintMax),
        Type = string.IsNullOrWhiteSpace(r.Type) ? "Error" : Cap(Scrub(r.Type), TypeMax),
        Message = Cap(Scrub(r.Message ?? ""), MessageMax),
        Stack = Cap(Scrub(r.Stack ?? ""), StackMax),
        Context = Cap(Scrub(StripQuery(r.Context ?? "")), ContextMax),
        Count = Math.Clamp(r.Count, 1, MaxCount),
        FirstSeen = r.At,
        LastSeen = r.At,
    };

    private sealed class RawReport
    {
        public Exception? Exception { get; init; }
        public string Kind { get; init; } = "";
        public string? Fingerprint { get; init; }
        public string? Type { get; init; }
        public string? Message { get; init; }
        public string? Stack { get; init; }
        public string? Context { get; init; }
        public int Count { get; init; } = 1;
        public DateTimeOffset At { get; init; }
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
            var item = BuildItem(ex, ErrorKinds.Crash, null, DateTimeOffset.UtcNow);
            item.Log = CapTail(ScrubLog(Platform.ServiceLog.RecentLines()), LogMax);
            var json = JsonSerializer.Serialize(item, AppJsonContext.Default.ErrorReportItem);
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
                existing.Count = (int)Math.Min(MaxCount, (long)existing.Count + item.Count);
                existing.LastSeen = item.LastSeen;
                return;
            }
            if (_pending.Count >= MaxDistinct)
                return;
            if (item.Source == "web" && _pending.Values.Count(i => i.Source == "web") >= MaxWebDistinct)
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
        Drain();
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
                // Held back (keeps aggregating) until the resend window has passed since its last send.
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
            OsVersion = Cap(RuntimeInformation.OSDescription, OsVersionMax),
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

    internal ErrorReportItem BuildItem(Exception ex, string kind, string? context) => BuildItem(ex, kind, context, _clock.GetUtcNow());

    private static ErrorReportItem BuildItem(Exception ex, string kind, string? context, DateTimeOffset now)
    {
        var raw = ex.ToString();
        var type = ex.GetType().FullName ?? ex.GetType().Name;
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
        return Hash(source + "\n" + type + "\n" + string.Join("\n", frames));
    }

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..32];

    /// <summary>Log lines interpolate device serials, session ids, hardware addresses and the PC's own names that exception text does not, so they get these on top of <see cref="Scrub"/>. Best effort: free text such as a file name can still pass.</summary>
    internal static string ScrubLog(string text) => ScrubLog(text, Environment.MachineName, Environment.UserName);

    internal static string ScrubLog(string text, string machineName, string userName)
    {
        text = Scrub(text);
        if (machineName.Length >= 3)
            text = text.Replace(machineName, "<host>", StringComparison.OrdinalIgnoreCase);
        if (userName.Length >= 3)
            text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        text = MacAddressRegex().Replace(text, "<mac>");
        text = Ipv6Regex().Replace(text, "<ip>");
        text = GuidRegex().Replace(text, "<id>");
        return OpaqueIdRegex().Replace(text, "<id>");
    }

    internal static string Scrub(string text)
    {
        if (text.Length == 0)
            return text;
        text = WindowsUserPathRegex().Replace(text, "~");
        text = MacUserPathRegex().Replace(text, "~");
        text = LinuxHomePathRegex().Replace(text, "~");
        text = EmailRegex().Replace(text, "<email>");
        text = Ipv4Regex().Replace(text, "<ip>");
        return text;
    }

    private static string StripQuery(string s)
    {
        var i = s.AsSpan().IndexOfAny('?', '#');
        return i < 0 ? s : s[..i];
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    // Keeps the end: the lines closest to the crash.
    private static string CapTail(string s, int max) => s.Length <= max ? s : s[^max..];

    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/][^\\/\s]+")]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"/Users/[^/\s]+")]
    private static partial Regex MacUserPathRegex();

    [GeneratedRegex(@"/home/[^/\s]+")]
    private static partial Regex LinuxHomePathRegex();

    [GeneratedRegex(@"^\s*at\s+([^\s(]+)", RegexOptions.Multiline)]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacAddressRegex();

    // Full form, or a compressed "::" form; clock times never contain "::".
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}\b|\b(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?")]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}(?![A-Za-z0-9_-])")]
    private static partial Regex GuidRegex();

    // One token is a run of [A-Za-z0-9_-] (base64url, hyphen-grouped serials). It is an id when it has 6+ chars with a
    // digit and a letter, is 8+ digits, or is 12+ hex letters; a 0x literal, a number with a unit, a WxH
    // resolution and the ISO timestamp that starts each log line are kept.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?:(?!0x[0-9A-Fa-f]+(?![A-Za-z0-9_-]))(?!\d+[A-Za-z]{1,3}(?![A-Za-z0-9_-]))(?!\d{2,5}x\d{2,5}(?![A-Za-z0-9_-]))(?!\d{4}-\d{2}-\d{2}T)(?=[A-Za-z0-9_-]*\d)(?=[A-Za-z0-9_-]*[A-Za-z])[A-Za-z0-9_-]{6,}|\d{8,}|[A-Fa-f]{12,})(?![A-Za-z0-9_-])")]
    private static partial Regex OpaqueIdRegex();
}
