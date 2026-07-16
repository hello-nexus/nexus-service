using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Common.ExternalTools;

[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ToolBundledPin))]
internal partial class ExternalToolsJsonContext : JsonSerializerContext;

/// <summary>
/// Generic "device shows up -> fetch its sidecar payload -> install/run it" manager
/// (Linear NEX-13). One singleton resolves a tool's artifact on disk - fetching it
/// from <c>assets.hellonexus.com</c> on first use, hash-pinned, and caching it -
/// then routes install/launch to the <see cref="IToolInstallStrategy"/> for the
/// spec's <see cref="ExternalToolSpec.Target"/>. Generic over the app's driver
/// manifest block; no consuming app is named here.
///
/// Trust: a tool runs only when a bundled app declares it (the <c>driver</c>
/// manifest block, gated by install source in the registry); this class is the
/// actuator, not the gate.
/// </summary>
public sealed class ExternalToolManager : IHostedService, IToolResolver
{
    /// <summary>Dev-only escape hatch: set to "1" to allow an unverified glob match
    /// when no manifest and no hash-pinned <c>bundled.json</c> are available. Off in
    /// shipping builds - every resolved binary is hash-pinned.</summary>
    private const string AllowUnverifiedEnv = "NEXUS_TOOLS_ALLOW_UNVERIFIED";

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly IReadOnlyDictionary<ToolTarget, IToolInstallStrategy> _strategies;
    private readonly Func<string, bool> _tryLock;
    private readonly ConcurrentDictionary<string, Task<string?>> _resolving = new(StringComparer.Ordinal);

    public ExternalToolManager(IEnumerable<IToolInstallStrategy> strategies)
        : this(strategies, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, ResolveDefaultRoot()) { }

    /// <summary>Test seam: inject an <see cref="HttpClient"/> and a temp cache root;
    /// defaults to the host-exe strategy.</summary>
    public ExternalToolManager(HttpClient http, string root)
        : this(new IToolInstallStrategy[] { new HostExeInstallStrategy() }, http, root) { }

    public ExternalToolManager(IEnumerable<IToolInstallStrategy> strategies, HttpClient http, string root)
        : this(strategies, http, root, TryLockDir) { }

    /// <summary>Test seam: inject the dir-lock result. The real one only ever locks
    /// on the LocalSystem daemon, so no test platform reaches the untrusted branch
    /// the cache pin is gated on.</summary>
    internal ExternalToolManager(IEnumerable<IToolInstallStrategy> strategies, HttpClient http, string root,
        Func<string, bool> tryLock)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _strategies = (strategies ?? throw new ArgumentNullException(nameof(strategies)))
            .ToDictionary(s => s.Target);
        _tryLock = tryLock ?? throw new ArgumentNullException(nameof(tryLock));
    }

    /// <summary>
    /// Locks the cache root to SYSTEM + Administrators. The dir holds binaries this
    /// service launches as LocalSystem, and the <c>bundled.json</c> beside each one
    /// is the hash they are checked against, so a non-admin able to write here can
    /// pin and run their own executable - %ProgramData% grants Users create-file by
    /// inheritance. Done at start, before any resolve, so a tool dir cannot be
    /// pre-created by the user who would then own it.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tryLock(_root);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TerminateAll();
        return Task.CompletedTask;
    }

    // ── Resolve ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the path to the tool's verified binary, fetching + caching it if
    /// necessary, or null if it can't be resolved. Concurrent calls for the same
    /// <c>(ToolId, Variant)</c> share one in-flight resolve.
    /// </summary>
    public async Task<string?> ResolveAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        var key = spec.ToolId + " " + spec.Variant;
        var task = _resolving.GetOrAdd(key, _ => ResolveCoreAsync(spec, ct));
        try { return await task; }
        finally { _resolving.TryRemove(key, out _); }
    }

    private async Task<string?> ResolveCoreAsync(ExternalToolSpec spec, CancellationToken ct)
    {
        var cacheDir = ToolDir(spec, out var cacheLocked);

        // 1. The app's preloaded bundled.json pin. First, and network-free by
        //    contract: an OEM/air-gapped image pins the binary it shipped with.
        var preloaded = await TryResolvePinAsync(spec, spec.PreloadDir, ct);
        if (preloaded is not null) return preloaded;

        // 2. manifest fetch → hash-pinned download into the cache dir.
        var unreachable = false;
        try
        {
            var manifest = await FetchManifestAsync(spec.ManifestUrl, ct);
            if (manifest is not null
                && manifest.Versions.TryGetValue(manifest.LatestVersion, out var v)
                && !string.IsNullOrWhiteSpace(v.FileName))
            {
                var url = string.IsNullOrWhiteSpace(v.Url)
                    ? $"{spec.DownloadUrlBase.TrimEnd('/')}/{v.FileName}"
                    : v.Url!;
                var dest = Path.Combine(cacheDir, v.FileName);
                await VerifiedDownload.DownloadAsync(_http, url, dest, v.Sha256, v.Size, ct);
                WritePin(cacheDir, v);
                return dest;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            unreachable = true;
            ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} manifest unreachable: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // The host answered - a bad status, or a payload that failed its hash.
            // Both are answers, so the cache must not paper over them.
            ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} manifest/download failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. The pin left by an earlier download. Gated on the manifest being
        //    unreachable, so no-network keeps a cached tool running while a
        //    manifest that answers still decides: it upgrades the tool at step 2,
        //    and withdrawing a version there still stops it. The pin records what
        //    this service verified at download time, which only the locked cache
        //    dir makes trustworthy - an unlocked dir is one any user can pin from.
        if (unreachable && cacheLocked)
        {
            var cached = await TryResolvePinAsync(spec, cacheDir, ct);
            if (cached is not null)
            {
                ServiceLog.Info($"[tools] {spec.ToolId}/{spec.Variant} using cached {Path.GetFileName(cached)} (manifest unreachable)");
                return cached;
            }
        }

        // 4. dev-only unverified glob fallback.
        if (AllowUnverified())
        {
            foreach (var baseDir in EnumerateBaseDirs(cacheDir, spec.PreloadDir))
            {
                var match = SafeGlobFirst(baseDir, spec.FilePattern);
                if (match is not null)
                {
                    ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} using UNVERIFIED glob match {Path.GetFileName(match)} ({AllowUnverifiedEnv}=1)");
                    return match;
                }
            }
        }

        return null;
    }

    private async Task<ToolManifest?> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
        using var resp = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ExternalToolsJsonContext.Default.ToolManifest, ct);
    }

    /// <summary>Fetch the remote manifest and return its latest version entry
    /// (includes <see cref="ToolVersion.VersionCode"/>), without downloading the
    /// payload. Returns null when the manifest is unavailable.</summary>
    public async Task<ToolVersion?> GetLatestAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        try
        {
            var manifest = await FetchManifestAsync(spec.ManifestUrl, ct);
            if (manifest is null) return null;
            return manifest.Versions.TryGetValue(manifest.LatestVersion, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetch the remote manifest and return the latest version string,
    /// or null when unavailable.</summary>
    public async Task<string?> GetLatestVersionAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        var v = await GetLatestAsync(spec, ct);
        return v?.Version;
    }

    // ── Launch / lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolve and run the tool on its target medium, routed by
    /// <see cref="ExternalToolSpec.Target"/>. Single-instance and process tracking
    /// are the strategy's responsibility. A spec whose target has no registered
    /// strategy is a no-op.
    /// </summary>
    public Task LaunchAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(spec.Target, out var strategy))
        {
            ServiceLog.Warn($"[tools] {spec.ToolId}: no install strategy for target {spec.Target}; not launched");
            return Task.CompletedTask;
        }
        return strategy.LaunchAsync(spec, this, ct);
    }

    /// <summary>
    /// Tear down a single tool on whichever strategy owns it. True only when a
    /// strategy confirms the process is gone - a caller that then touches the
    /// device relies on this to know it is the only writer.
    /// </summary>
    public bool Terminate(string toolId)
    {
        var gone = false;
        foreach (var strategy in _strategies.Values)
            gone |= strategy.Terminate(toolId);
        return gone;
    }

    /// <summary>Tear down every tracked tool. Called from <see cref="StopAsync"/>,
    /// and directly from the Windows fast-shutdown path, which runs no hosted
    /// StopAsync - without that call the tools outlive the service.</summary>
    public void TerminateAll()
    {
        foreach (var strategy in _strategies.Values)
            strategy.TerminateAll();
    }

    /// <summary>
    /// Current status for a tool. <paramref name="devicePresent"/> distinguishes
    /// "no hardware" (NoDevice) from "hardware present but not running" (NotRunning);
    /// pass null when presence is unknown.
    /// </summary>
    public ToolStatus GetStatus(string toolId, bool? devicePresent = null)
    {
        foreach (var strategy in _strategies.Values)
        {
            var status = strategy.GetStatus(toolId);
            if (status is ToolStatus.Running or ToolStatus.Failed) return status;
        }
        if (devicePresent == false) return ToolStatus.NoDevice;
        return ToolStatus.NotRunning;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tool's cache dir. <paramref name="locked"/> reports whether it is safe to
    /// trust a pin from - every segment is re-locked rather than left to the root's
    /// inheritance, because a dir a non-admin pre-created is owned by that user, who
    /// keeps WRITE_DAC over it whatever the inherited DACL says, and DELETE_CHILD on
    /// a parent defeats a locked child.
    /// </summary>
    private string ToolDir(ExternalToolSpec spec, out bool locked)
    {
        var toolRoot = Path.Combine(_root, Sanitize(spec.ToolId));
        var dir = Path.Combine(toolRoot, Sanitize(spec.Variant));
        // Non-shortcircuiting: every segment must be locked, not just up to the first failure.
        locked = _tryLock(_root) & _tryLock(toolRoot) & _tryLock(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Lock a cache dir to SYSTEM + Administrators, reporting whether its contents
    /// may be trusted.
    ///
    /// Off Windows the root is under the user's own profile, so it is theirs to
    /// write. On Windows the root is machine-wide and %ProgramData% lets *every*
    /// local user create files in it - not just whoever runs the service - so
    /// anything but the LocalSystem daemon that can lock it reports untrusted: a
    /// non-admin plants, an admin's interactive run would execute it as themselves.
    /// </summary>
    private static bool TryLockDir(string dir)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (!WindowsDirectorySecurity.IsLocalSystem()) return false;
        try
        {
            WindowsDirectorySecurity.Protect(dir, resetOwner: true);
            // Protect swallows a failed owner reset, and an owner keeps WRITE_DAC
            // over the DACL just written - so the lock is only real if it took.
            if (WindowsDirectorySecurity.IsOwnedByAdmins(dir)) return true;
            ServiceLog.Warn($"[tools] {dir} not owned by SYSTEM/Administrators after locking; contents not trusted");
            return false;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tools] lock {dir} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the manifest host gave no usable answer - no route, no DNS, a
    /// timeout, or a body that does not parse as a manifest (a captive portal's
    /// login page, or a malformed publish). Falling back to the last binary this
    /// service verified is the conservative reading of all of those. An HTTP status
    /// and a manifest that parses are both answers, so neither is this.
    /// </summary>
    private static bool IsUnreachable(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null,
        JsonException => true,
        TaskCanceledException => true,
        _ => false,
    };

    private static System.Collections.Generic.IEnumerable<string> EnumerateBaseDirs(string cacheDir, string? preloadDir)
    {
        yield return cacheDir;
        if (!string.IsNullOrEmpty(preloadDir) && Directory.Exists(preloadDir))
            yield return preloadDir;
    }

    /// <summary>
    /// The hash-verified binary <paramref name="baseDir"/>'s <c>bundled.json</c>
    /// pins, or null when there is no pin, no dir, or the file fails the pin.
    /// </summary>
    private static async Task<string?> TryResolvePinAsync(ExternalToolSpec spec, string? baseDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return null;
        var pin = TryReadPin(baseDir);
        if (pin is null || string.IsNullOrWhiteSpace(pin.FileName)) return null;
        // Anything but a plain file name escapes the dir the ACL protects, and
        // Path.Combine honours the escape: a rooted or drive-relative name ("C:x")
        // replaces the base entirely, carrying no separator to filter on.
        if (!string.Equals(Path.GetFileName(pin.FileName), pin.FileName, StringComparison.Ordinal))
        {
            ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} bundled.json pin rejected: {pin.FileName} is not a plain file name");
            return null;
        }
        var pinned = Path.Combine(baseDir, pin.FileName);
        if (await VerifiedDownload.IsValidAsync(pinned, pin.Sha256, pin.Size, ct)) return pinned;
        ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} bundled.json pin failed verification: {pin.FileName}");
        return null;
    }

    private static ToolBundledPin? TryReadPin(string baseDir)
    {
        var path = Path.Combine(baseDir, "bundled.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize(fs, ExternalToolsJsonContext.Default.ToolBundledPin);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Record what was just downloaded and verified, so the next resolve can use it
    /// with no network. Best-effort: an unwritten pin costs the offline path, not
    /// this launch.
    /// </summary>
    private static void WritePin(string cacheDir, ToolVersion v)
    {
        try
        {
            var pin = new ToolBundledPin { FileName = v.FileName, Sha256 = v.Sha256, Size = v.Size };
            var tmp = Path.Combine(cacheDir, "bundled.json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(pin, ExternalToolsJsonContext.Default.ToolBundledPin));
            File.Move(tmp, Path.Combine(cacheDir, "bundled.json"), overwrite: true);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tools] pin write failed in {cacheDir}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? SafeGlobFirst(string baseDir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal)) return null;
        if (!Directory.Exists(baseDir)) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                return f;
        }
        catch { /* best effort */ }
        return null;
    }

    private static bool AllowUnverified()
        => string.Equals(Environment.GetEnvironmentVariable(AllowUnverifiedEnv), "1", StringComparison.Ordinal);

    private static string Sanitize(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Tool path segment is required.", nameof(segment));
        foreach (var c in segment)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new ArgumentException($"Tool path segment '{segment}' contains illegal characters.", nameof(segment));
        }
        return segment;
    }

    private static string ResolveDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope so the LocalSystem service owns the cache, like the
            // firmware store.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "drivers");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(xdg, "Nexus", "drivers");
    }
}
