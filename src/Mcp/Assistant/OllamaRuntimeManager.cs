using System;
using System.Buffers;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Update;

namespace Nexus.Service.Mcp.Assistant;

public enum AssistantRuntimeState
{
    NotInstalled,
    Downloading,
    Installed,
    Starting,
    Running,
    Error,
}

/// <summary>Consistent point-in-time view of <see cref="OllamaRuntimeManager"/>'s
/// status fields, taken under its internal lock.</summary>
public readonly record struct AssistantRuntimeSnapshot(
    AssistantRuntimeState State,
    bool SystemDetected,
    long? DownloadReceived,
    long? DownloadTotal,
    string? BusyKind,
    string? BusyModel,
    string? LastError);

/// <summary>
/// Owns the managed Ollama runtime: resolve the latest release, download the
/// portable archive over HTTPS from github.com/ollama/ollama, verify its
/// SHA-256 against the release's published sha256sum.txt, extract, and
/// supervise <c>ollama serve</c> as a child process bound to loopback. Detects
/// and reuses a system-installed Ollama instead of downloading when one is
/// already answering on the default port. Install state is derived from disk
/// + process/HTTP reachability, not persisted.
///
/// IHostedService only for shutdown: StartAsync never downloads or spawns
/// anything (install is user-initiated), StopAsync stops a managed child.
/// </summary>
public sealed class OllamaRuntimeManager : IHostedService
{
    public const int DefaultPort = 11434;

    public const string BusyKindInstallingRuntime = "installingRuntime";
    public const string BusyKindRemovingRuntime = "removingRuntime";
    public const string BusyKindPullingModel = "pullingModel";
    public const string BusyKindRemovingModel = "removingModel";

    private const string OwnerRepo = "ollama/ollama";
    private const string PinnedFallbackVersion = "v0.32.1";
    private const string Sha256SumsAssetName = "sha256sum.txt";
    private const int CopyBufferSize = 81920;
    private const int ReadyPollIntervalMs = 200;
    private const int DeleteMaxAttempts = 5;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressBroadcastInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ChildStopTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IOllamaProcessHost _processHost;
    private readonly Func<int, IOllamaClient> _clientFactory;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly string _runtimeDir;
    private readonly string _modelsDir;
    private readonly bool _testHost;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Guards every field below: the fire-and-forget install/pull/remove tasks
    // and the child-exit watcher all mutate these from different threads,
    // and BuildStatusAsync/BroadcastProgress must see a consistent snapshot
    // rather than a torn read across a nullable long pair.
    private readonly object _lock = new();

    private AssistantRuntimeState _state = AssistantRuntimeState.NotInstalled;
    private bool _systemDetected;
    private long? _downloadReceived;
    private long? _downloadTotal;
    private string? _lastError;
    private string? _busyKind;
    private string? _busyModel;
    private int _effectivePort;
    private IOllamaProcessHandle? _child;
    private IOllamaClient? _client;

    public OllamaRuntimeManager(HttpClient http, IConfigStore store, MultiplexHub hub)
        : this(http, new SystemOllamaProcessHost(), port => new OllamaClient(http, port), store, hub,
              DefaultDataDir(), Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") == "1")
    {
    }

    /// <summary>Test seam: inject a fake process host and/or client factory so the
    /// state machine can be exercised without a real download or a real child
    /// process. <paramref name="testHost"/> is caller-controlled (not env-derived)
    /// so a direct unit test stays deterministic regardless of whether another
    /// test class in the same process set NEXUS_TEST_HOST.</summary>
    internal OllamaRuntimeManager(
        HttpClient http,
        IOllamaProcessHost processHost,
        Func<int, IOllamaClient> clientFactory,
        IConfigStore store,
        MultiplexHub hub,
        string dataDir,
        bool testHost = false)
    {
        _http = http;
        _processHost = processHost;
        _clientFactory = clientFactory;
        _store = store;
        _hub = hub;
        _runtimeDir = Path.Combine(dataDir, "runtime");
        _modelsDir = Path.Combine(dataDir, "models");
        _testHost = testHost;
    }

    public AssistantRuntimeState State { get { lock (_lock) { return _state; } } }
    public bool SystemOllamaDetected { get { lock (_lock) { return _systemDetected; } } }
    public long? DownloadReceivedBytes { get { lock (_lock) { return _downloadReceived; } } }
    public long? DownloadTotalBytes { get { lock (_lock) { return _downloadTotal; } } }
    public string? LastError { get { lock (_lock) { return _lastError; } } }
    public string? BusyKind { get { lock (_lock) { return _busyKind; } } }
    public string? BusyModel { get { lock (_lock) { return _busyModel; } } }
    public int EffectivePort { get { lock (_lock) { return _effectivePort; } } }

    /// <summary>Ready client for the running runtime (managed child or detected
    /// system install); null unless <see cref="State"/> is Running.</summary>
    public IOllamaClient? Client { get { lock (_lock) { return _client; } } }

    /// <summary>Consistent snapshot of every field a status/progress frame reads,
    /// taken under one lock so a status poll can never see a torn combination
    /// (e.g. a download total from after a reset paired with a received count
    /// from before it).</summary>
    public AssistantRuntimeSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new AssistantRuntimeSnapshot(_state, _systemDetected, _downloadReceived, _downloadTotal, _busyKind, _busyModel, _lastError);
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => StopChildAsync();

    /// <summary>Synchronous shutdown path for the Windows fast-teardown route
    /// (no hosted StopAsync runs there).</summary>
    public void StopChildForShutdown()
    {
        IOllamaProcessHandle? handle;
        lock (_lock)
        {
            handle = _child;
            _child = null;
        }
        if (handle is null)
        {
            return;
        }
        handle.Kill();
        try
        {
            handle.WaitForExitAsync(CancellationToken.None).Wait(ChildStopTimeout);
        }
        catch
        {
        }
    }

    /// <summary>Called before building a status response. While NotInstalled,
    /// re-probes for a system Ollama so one started after this service booted
    /// is picked up without requiring an explicit install click. Skipped under
    /// NEXUS_TEST_HOST so a route test never makes a real loopback connection.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (State != AssistantRuntimeState.NotInstalled || _testHost || !UseSystemOllama)
        {
            return;
        }
        // Re-check after the async probe: an Install (or a concurrent Refresh)
        // may have moved state on while this one was in flight.
        if (await DetectSystemOllamaAsync(ct).ConfigureAwait(false) && State == AssistantRuntimeState.NotInstalled)
        {
            AdoptSystemOllama();
        }
    }

    public async Task InstallRuntimeAsync(CancellationToken ct)
    {
        if (State is AssistantRuntimeState.Running or AssistantRuntimeState.Starting or AssistantRuntimeState.Downloading)
        {
            return;
        }
        if (_testHost)
        {
            SetError(new InvalidOperationException("Runtime install is unavailable under NEXUS_TEST_HOST."));
            return;
        }
        if (!TryBeginBusy(BusyKindInstallingRuntime, null))
        {
            return;
        }
        try
        {
            if (UseSystemOllama && await DetectSystemOllamaAsync(ct).ConfigureAwait(false))
            {
                AdoptSystemOllama();
                return;
            }

            SetState(AssistantRuntimeState.Downloading);
            lock (_lock)
            {
                _downloadReceived = null;
                _downloadTotal = null;
            }
            BroadcastProgress();

            var exePath = await DownloadAndExtractAsync(ct).ConfigureAwait(false);
            SetState(AssistantRuntimeState.Installed);
            BroadcastProgress();

            await StartChildAsync(exePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not a caller cancellation (ct is CancellationToken.None from the
            // route) - an internal timeout or a stray cancellation. Surface it
            // as Error instead of leaving the state stuck at Downloading/Starting
            // with no way to retry (the early-return guard above treats those as
            // already in progress).
            SetError(new TimeoutException("The runtime install was cancelled unexpectedly."));
            BroadcastProgress();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex);
            BroadcastProgress();
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task RemoveRuntimeAsync(bool deleteModels, CancellationToken ct)
    {
        if (SystemOllamaDetected)
        {
            return;
        }
        if (!TryBeginBusy(BusyKindRemovingRuntime, null))
        {
            return;
        }
        try
        {
            await StopChildAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _client = null;
            }
            await DeleteDirectoryWithRetryAsync(Path.Combine(_runtimeDir, "bin"), ct).ConfigureAwait(false);
            if (deleteModels)
            {
                await DeleteDirectoryWithRetryAsync(_modelsDir, ct).ConfigureAwait(false);
            }
            SetState(AssistantRuntimeState.NotInstalled);
            BroadcastProgress();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SetError(new TimeoutException("Removing the runtime was cancelled unexpectedly."));
            BroadcastProgress();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A retry-exhausted locked file (Windows) or any other delete
            // failure must still resolve to Error, not leave State reporting
            // whatever it was before the remove was attempted.
            SetError(ex);
            BroadcastProgress();
        }
        finally
        {
            EndBusy();
        }
    }

    public Task<IReadOnlyList<OllamaModelInfo>> ListInstalledModelsAsync(CancellationToken ct)
    {
        var client = Client;
        return client is null
            ? Task.FromResult<IReadOnlyList<OllamaModelInfo>>(Array.Empty<OllamaModelInfo>())
            : SafeListAsync(client, ct);
    }

    private static async Task<IReadOnlyList<OllamaModelInfo>> SafeListAsync(IOllamaClient client, CancellationToken ct)
    {
        try
        {
            return await client.ListModelsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<OllamaModelInfo>();
        }
    }

    public async Task<bool> PullModelAsync(string model, CancellationToken ct)
    {
        var client = Client;
        if (client is null || _testHost)
        {
            return false;
        }
        if (!TryBeginBusy(BusyKindPullingModel, model))
        {
            return false;
        }
        try
        {
            await client.PullModelAsync(model, status => BroadcastPullProgress(model, status), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"[assistant] pull {model} was cancelled unexpectedly (not by the caller).");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[assistant] pull {model} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            EndBusy();
            BroadcastProgress();
        }
    }

    public async Task<bool> RemoveModelAsync(string model, CancellationToken ct)
    {
        var client = Client;
        if (client is null)
        {
            return false;
        }
        if (!TryBeginBusy(BusyKindRemovingModel, model))
        {
            return false;
        }
        try
        {
            return await client.DeleteModelAsync(model, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"[assistant] remove {model} was cancelled unexpectedly (not by the caller).");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[assistant] remove {model} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            EndBusy();
            BroadcastProgress();
        }
    }

    // ── Single-flight ────────────────────────────────────────────────────────

    private bool TryBeginBusy(string kind, string? model)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }
        lock (_lock)
        {
            _busyKind = kind;
            _busyModel = model;
        }
        return true;
    }

    private void EndBusy()
    {
        lock (_lock)
        {
            _busyKind = null;
            _busyModel = null;
        }
        _gate.Release();
    }

    // ── System detection ────────────────────────────────────────────────────

    /// <summary>User opt-in (AiIntegration.UseSystemOllama). Off, nothing on the
    /// default port is ever probed or adopted: a listener there could be any
    /// local process, and the adopted runtime sees every prompt and every tool
    /// call the assistant makes as LocalSystem.</summary>
    private bool UseSystemOllama => _store.Load().AiIntegration.UseSystemOllama;

    /// <summary>Called when the opt-in changes. Turning it off while a system
    /// Ollama is adopted drops back to NotInstalled so the next status poll
    /// stops routing prompts to it; turning it on lets the next poll adopt.</summary>
    public void ApplyUseSystemOllama(bool enabled)
    {
        if (enabled)
        {
            return;
        }
        lock (_lock)
        {
            if (!_systemDetected)
            {
                return;
            }
            _systemDetected = false;
            _client = null;
            _effectivePort = 0;
            _state = AssistantRuntimeState.NotInstalled;
            _lastError = null;
        }
        BroadcastProgress();
    }

    private async Task<bool> DetectSystemOllamaAsync(CancellationToken ct)
    {
        var probe = _clientFactory(DefaultPort);
        return await probe.TryGetVersionAsync(ct).ConfigureAwait(false) is not null;
    }

    private void AdoptSystemOllama()
    {
        lock (_lock)
        {
            // The probe ran outside the lock; the user may have opted out (or a
            // managed install may have started) while it was in flight.
            if (!UseSystemOllama || _state != AssistantRuntimeState.NotInstalled)
            {
                return;
            }
            _systemDetected = true;
            _effectivePort = DefaultPort;
            _client = _clientFactory(DefaultPort);
            _state = AssistantRuntimeState.Running;
            _lastError = null;
        }
        BroadcastProgress();
    }

    // ── Download / extract / launch ─────────────────────────────────────────

    private async Task<string> DownloadAndExtractAsync(CancellationToken ct)
    {
        var assetName = ResolveAssetName();
        var (_, assetUrl, sumsUrl) = await ResolveReleaseAsync(assetName, ct).ConfigureAwait(false);

        EnsureRuntimeDirTrusted();
        var archivePath = Path.Combine(_runtimeDir, assetName);
        var tmpPath = archivePath + ".tmp";
        TryDeleteFile(tmpPath);

        var sumsText = await _http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false);
        var expectedSha = GitHubReleaseProvider.ParseSha256Sums(sumsText, assetName)
            ?? throw new InvalidDataException($"{Sha256SumsAssetName} has no entry for {assetName}.");

        try
        {
            using (var resp = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                lock (_lock)
                {
                    _downloadTotal = resp.Content.Headers.ContentLength ?? 0;
                }
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
                await CopyWithProgressAsync(src, dst, ct).ConfigureAwait(false);
            }

            var actualSha = await VerifiedDownload.ComputeSha256Async(tmpPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"SHA-256 mismatch for {assetName}: got {actualSha}, expected {expectedSha}.");
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            File.Move(tmpPath, archivePath);
        }
        catch
        {
            TryDeleteFile(tmpPath);
            throw;
        }

        var extractDir = Path.Combine(_runtimeDir, "bin");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }
        try
        {
            ExtractArchive(archivePath, extractDir, assetName);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }

        return ResolveExecutable(extractDir);
    }

    private async Task CopyWithProgressAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var lastBroadcast = DateTime.UtcNow;
        try
        {
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, CopyBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                lock (_lock)
                {
                    _downloadReceived = total;
                }
                var now = DateTime.UtcNow;
                if (now - lastBroadcast >= ProgressBroadcastInterval)
                {
                    lastBroadcast = now;
                    BroadcastProgress();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        BroadcastProgress();
    }

    private async Task<(string Version, string AssetUrl, string SumsUrl)> ResolveReleaseAsync(string assetName, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{OwnerRepo}/releases/latest");
            req.Headers.UserAgent.TryParseAdd("Nexus-Service/" + BuildInfo.Version);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var release = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.GitHubRelease, ct).ConfigureAwait(false);
                var asset = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
                var sums = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, Sha256SumsAssetName, StringComparison.OrdinalIgnoreCase));
                if (release is not null && asset?.BrowserDownloadUrl is not null && sums?.BrowserDownloadUrl is not null)
                {
                    return (release.TagName ?? PinnedFallbackVersion, asset.BrowserDownloadUrl, sums.BrowserDownloadUrl);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ServiceLog.Warn($"[assistant] GitHub release lookup failed, using pinned fallback {PinnedFallbackVersion}: {ex.Message}");
        }

        var baseUrl = $"https://github.com/{OwnerRepo}/releases/download/{PinnedFallbackVersion}";
        return (PinnedFallbackVersion, $"{baseUrl}/{assetName}", $"{baseUrl}/{Sha256SumsAssetName}");
    }

    internal static string ResolveAssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ollama-windows-arm64.zip" : "ollama-windows-amd64.zip";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "ollama-darwin.tgz";
        }
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ollama-linux-arm64.tar.zst" : "ollama-linux-amd64.tar.zst";
    }

    private static void ExtractArchive(string archivePath, string destDir, string assetName)
    {
        Directory.CreateDirectory(destDir);
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
            return;
        }
        if (assetName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(archivePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
            return;
        }
        // zstd tarballs (Linux) have no BCL decoder and no bundled zstd binary
        // today; Linux users fall back to the system-Ollama detection path.
        throw new NotSupportedException($"No extractor available for archive '{assetName}'.");
    }

    private static string ResolveExecutable(string extractDir)
    {
        var name = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var direct = Path.Combine(extractDir, name);
        if (File.Exists(direct))
        {
            MarkExecutable(direct);
            return direct;
        }
        foreach (var candidate in Directory.EnumerateFiles(extractDir, name, SearchOption.AllDirectories))
        {
            MarkExecutable(candidate);
            return candidate;
        }
        throw new FileNotFoundException($"'{name}' not found after extracting the runtime archive.", name);
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The runtime dir lives under %ProgramData%, whose default DACL lets any
    /// local user create files and folders. A LocalSystem daemon must only
    /// extract into, and launch a binary from, a dir it owns with a locked DACL
    /// (same rule as the OTA staging dir and the external-tools cache). An
    /// interactive run keeps the user's own permissions; nothing to lock.
    /// </summary>
    private void EnsureRuntimeDirTrusted()
    {
        Directory.CreateDirectory(_runtimeDir);
        if (!OperatingSystem.IsWindows() || _testHost || !WindowsDirectorySecurity.IsLocalSystem())
        {
            return;
        }
        WindowsDirectorySecurity.Protect(_runtimeDir, resetOwner: true);
        if (!WindowsDirectorySecurity.IsOwnedByAdmins(_runtimeDir))
        {
            throw new InvalidOperationException($"{_runtimeDir} is not owned by SYSTEM/Administrators; refusing to run a binary from it.");
        }
    }

    private async Task StartChildAsync(string exePath, CancellationToken ct)
    {
        EnsureRuntimeDirTrusted();
        SetState(AssistantRuntimeState.Starting);
        BroadcastProgress();

        var port = ResolveConfiguredPort();
        Directory.CreateDirectory(_modelsDir);
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = _modelsDir,
            ["OLLAMA_HOST"] = $"127.0.0.1:{port}",
        };

        var handle = _processHost.Start(exePath, Path.GetDirectoryName(exePath) ?? _runtimeDir, env);
        lock (_lock)
        {
            _child = handle;
        }

        var client = _clientFactory(port);
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (handle.HasExited)
            {
                lock (_lock)
                {
                    _child = null;
                }
                throw new InvalidOperationException("ollama serve exited before it became ready.");
            }
            if (await client.TryGetVersionAsync(ct).ConfigureAwait(false) is not null)
            {
                lock (_lock)
                {
                    _effectivePort = port;
                    _client = client;
                    _state = AssistantRuntimeState.Running;
                    _lastError = null;
                }
                BroadcastProgress();
                _ = WatchForUnexpectedExitAsync(handle);
                return;
            }
            await Task.Delay(ReadyPollIntervalMs, ct).ConfigureAwait(false);
        }

        handle.Kill();
        lock (_lock)
        {
            _child = null;
        }
        throw new TimeoutException("ollama serve did not become ready in time.");
    }

    /// <summary>
    /// Waits on the child's real exit signal for the lifetime of the managed
    /// runtime. A deliberate stop (StopChildAsync / StopChildForShutdown /
    /// RemoveRuntimeAsync) clears <see cref="_child"/> before killing, so this
    /// only reconciles state for an exit nobody asked for - a crash, an
    /// external kill, or the process dying on its own. Never restarts it.
    /// </summary>
    private async Task WatchForUnexpectedExitAsync(IOllamaProcessHandle handle)
    {
        try
        {
            await handle.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        bool wasUnexpected;
        lock (_lock)
        {
            wasUnexpected = ReferenceEquals(_child, handle);
            if (wasUnexpected)
            {
                _child = null;
                _client = null;
                _state = AssistantRuntimeState.Error;
                _lastError = "The Ollama runtime stopped unexpectedly.";
            }
        }
        if (!wasUnexpected)
        {
            return;
        }
        ServiceLog.Warn("[assistant] ollama serve exited unexpectedly; runtime marked Error.");
        BroadcastProgress();
    }

    private int ResolveConfiguredPort()
    {
        var configured = _store.Load().AiIntegration.AssistantRuntimePort;
        return configured > 0 ? configured : DefaultPort;
    }

    private async Task StopChildAsync()
    {
        IOllamaProcessHandle? handle;
        lock (_lock)
        {
            handle = _child;
            _child = null;
        }
        if (handle is null)
        {
            return;
        }
        handle.Kill();
        try
        {
            using var cts = new CancellationTokenSource(ChildStopTimeout);
            await handle.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        for (var attempt = 1; attempt <= DeleteMaxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < DeleteMaxAttempts)
            {
                // Windows can hold the just-exited process's binary locked for a
                // short window after the exit handle signals (AV scan, delayed
                // handle release) - retry rather than fail the remove outright.
                await Task.Delay(DeleteRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    // ── State + broadcast ────────────────────────────────────────────────────

    private void SetState(AssistantRuntimeState state)
    {
        lock (_lock)
        {
            _state = state;
            if (state != AssistantRuntimeState.Error)
            {
                _lastError = null;
            }
        }
    }

    private void SetError(Exception ex)
    {
        lock (_lock)
        {
            _state = AssistantRuntimeState.Error;
            _lastError = ex.Message;
        }
        ServiceLog.Warn($"[assistant] runtime error: {ex.GetType().Name}: {ex.Message}");
    }

    internal static string StateWireName(AssistantRuntimeState state) => state switch
    {
        AssistantRuntimeState.NotInstalled => "notInstalled",
        AssistantRuntimeState.Downloading => "downloading",
        AssistantRuntimeState.Installed => "installed",
        AssistantRuntimeState.Starting => "starting",
        AssistantRuntimeState.Running => "running",
        AssistantRuntimeState.Error => "error",
        _ => "error",
    };

    private void BroadcastProgress()
    {
        var snap = GetSnapshot();
        var frame = new AssistantProgressFrame
        {
            RuntimeState = StateWireName(snap.State),
            DownloadProgress = snap.DownloadTotal is { } total && snap.DownloadReceived is { } received
                ? new AssistantDownloadProgressDto { Received = received, Total = total }
                : null,
        };
        PanelTopics.BroadcastAiAssistant(_hub, frame);
    }

    private void BroadcastPullProgress(string model, OllamaPullStatus status)
    {
        var frame = new AssistantProgressFrame
        {
            RuntimeState = StateWireName(State),
            Pull = new AssistantPullProgressDto
            {
                Model = model,
                Status = status.Status,
                Received = status.Completed,
                Total = status.Total,
            },
        };
        PanelTopics.BroadcastAiAssistant(_hub, frame);
    }

    private static string DefaultDataDir() => Path.Combine(NexusDataPaths.NexusRoot(), "assistant");
}
