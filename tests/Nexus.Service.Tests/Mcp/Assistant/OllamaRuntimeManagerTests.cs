using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.Assistant;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp.Assistant;

/// <summary>
/// OllamaRuntimeManager's state machine against a fake GitHub-shaped HTTP
/// handler and a fake process host: download + SHA-256 verify + extract +
/// launch + readiness poll, system-Ollama detection short-circuiting the
/// download, single-flight, and remove-with-retry.
/// </summary>
public sealed class OllamaRuntimeManagerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-ollama-mgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private TestableConfigStore NewStore(string dataDir) => new(Path.Combine(dataDir, "settings.json"));

    /// <summary>A store with the system-Ollama opt-in on. Off by default: a
    /// LocalSystem service must not hand every prompt and tool call to whatever
    /// local process bound the default port first.</summary>
    private TestableConfigStore OptedInStore(string dataDir)
    {
        var store = NewStore(dataDir);
        store.Update(s => s.AiIntegration.UseSystemOllama = true);
        return store;
    }

    [Fact]
    public async Task InstallRuntimeAsync_adopts_a_detected_system_ollama_without_any_download()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var processHost = new FakeProcessHost();
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), processHost, _ => client,
            OptedInStore(dataDir), new MultiplexHub(), dataDir);

        await manager.InstallRuntimeAsync(CancellationToken.None);

        Assert.Equal(AssistantRuntimeState.Running, manager.State);
        Assert.True(manager.SystemOllamaDetected);
        Assert.Equal(0, processHost.StartCount);
        Assert.Same(client, manager.Client);
    }

    [Fact]
    public async Task A_listener_on_the_default_port_is_ignored_unless_the_user_opted_in()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            NewStore(dataDir), new MultiplexHub(), dataDir);

        await manager.RefreshAsync(CancellationToken.None);

        Assert.Equal(AssistantRuntimeState.NotInstalled, manager.State);
        Assert.False(manager.SystemOllamaDetected);
        Assert.Equal(0, client.VersionProbes);
    }

    [Fact]
    public async Task Turning_the_opt_in_off_drops_an_adopted_system_ollama()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var store = OptedInStore(dataDir);
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            store, new MultiplexHub(), dataDir);
        await manager.RefreshAsync(CancellationToken.None);
        Assert.True(manager.SystemOllamaDetected);

        store.Update(s => s.AiIntegration.UseSystemOllama = false);
        manager.ApplyUseSystemOllama(false);

        Assert.Equal(AssistantRuntimeState.NotInstalled, manager.State);
        Assert.False(manager.SystemOllamaDetected);
        Assert.Null(manager.Client);
    }

    [Fact]
    public async Task InstallRuntimeAsync_downloads_verifies_extracts_and_reaches_running()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return; // zstd (Linux) extraction is not implemented; see the NotSupported test below.
        }

        var dataDir = NewTempDir();
        var assetName = OllamaRuntimeManager.ResolveAssetName();
        var binaryName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var payload = Encoding.UTF8.GetBytes("fake ollama binary contents");
        var archiveBytes = BuildArchive(assetName, binaryName, payload);
        var sha = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

        var http = new HttpClient(new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                return JsonResponse(
                    "{\"tag_name\":\"v0.32.1\",\"assets\":[" +
                    $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"https://example.test/{assetName}\",\"size\":{archiveBytes.Length}}}," +
                    "{\"name\":\"sha256sum.txt\",\"browser_download_url\":\"https://example.test/sha256sum.txt\"}]}");
            }
            if (url.EndsWith("sha256sum.txt", StringComparison.Ordinal))
            {
                return TextResponse($"{sha}  {assetName}\n");
            }
            if (url.EndsWith(assetName, StringComparison.Ordinal))
            {
                return BytesResponse(archiveBytes);
            }
            throw new InvalidOperationException($"unexpected request: {url}");
        }));

        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var processHost = new FakeProcessHost { OnStart = () => client.Ready = true };
        var hub = new MultiplexHub();
        var broadcasts = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => broadcasts.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.AiAssistant);

        var manager = new OllamaRuntimeManager(http, processHost, _ => client, NewStore(dataDir), hub, dataDir);

        await manager.InstallRuntimeAsync(CancellationToken.None);

        Assert.Equal(AssistantRuntimeState.Running, manager.State);
        Assert.False(manager.SystemOllamaDetected);
        Assert.Equal(1, processHost.StartCount);
        Assert.True(File.Exists(Path.Combine(dataDir, "runtime", "bin", binaryName)));
        Assert.Contains(PanelTopics.AiAssistant, broadcasts);
    }

    [Fact]
    public async Task InstallRuntimeAsync_rejects_a_sha256_mismatch_and_leaves_no_partial_file()
    {
        var dataDir = NewTempDir();
        var assetName = OllamaRuntimeManager.ResolveAssetName();
        var payload = Encoding.UTF8.GetBytes("some bytes");

        var http = new HttpClient(new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                return JsonResponse(
                    "{\"tag_name\":\"v0.32.1\",\"assets\":[" +
                    $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"https://example.test/{assetName}\",\"size\":{payload.Length}}}," +
                    "{\"name\":\"sha256sum.txt\",\"browser_download_url\":\"https://example.test/sha256sum.txt\"}]}");
            }
            if (url.EndsWith("sha256sum.txt", StringComparison.Ordinal))
            {
                return TextResponse($"{new string('0', 64)}  {assetName}\n"); // deliberately wrong hash
            }
            if (url.EndsWith(assetName, StringComparison.Ordinal))
            {
                return BytesResponse(payload);
            }
            throw new InvalidOperationException($"unexpected request: {url}");
        }));

        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var processHost = new FakeProcessHost();
        var manager = new OllamaRuntimeManager(http, processHost, _ => client, NewStore(dataDir), new MultiplexHub(), dataDir);

        await manager.InstallRuntimeAsync(CancellationToken.None);

        Assert.Equal(AssistantRuntimeState.Error, manager.State);
        Assert.Contains("SHA-256 mismatch", manager.LastError, StringComparison.Ordinal);
        Assert.Equal(0, processHost.StartCount);
        Assert.False(File.Exists(Path.Combine(dataDir, "runtime", assetName + ".tmp")));
    }

    [Fact]
    public async Task InstallRuntimeAsync_converts_an_internal_timeout_into_error_state_not_a_stuck_downloading_state()
    {
        // A client-side timeout throws TaskCanceledException (an
        // OperationCanceledException) with CancellationToken.None, which never
        // reports IsCancellationRequested - the exact shape a real HttpClient
        // timeout takes. This must resolve to Error, not silently propagate
        // and leave State stuck at Downloading with the busy flag cleared.
        var dataDir = NewTempDir();
        var http = new HttpClient(new RouteHandler(_ => throw new TaskCanceledException("simulated timeout")));
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var manager = new OllamaRuntimeManager(http, new FakeProcessHost(), _ => client, NewStore(dataDir), new MultiplexHub(), dataDir);

        await manager.InstallRuntimeAsync(CancellationToken.None);

        Assert.Equal(AssistantRuntimeState.Error, manager.State);
        Assert.NotNull(manager.LastError);
        Assert.Null(manager.BusyKind);
    }

    [Fact]
    public async Task InstallRuntimeAsync_is_single_flight_a_concurrent_call_is_a_no_op()
    {
        var dataDir = NewTempDir();
        var assetName = OllamaRuntimeManager.ResolveAssetName();
        var binaryName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        var payload = Encoding.UTF8.GetBytes("fake ollama binary");
        var archiveBytes = BuildArchive(assetName, binaryName, payload);
        var sha = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var manifestCalls = 0;

        var http = new HttpClient(new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                Interlocked.Increment(ref manifestCalls);
                Thread.Sleep(75); // widen the race window between the two concurrent calls
                return JsonResponse(
                    "{\"tag_name\":\"v0.32.1\",\"assets\":[" +
                    $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"https://example.test/{assetName}\",\"size\":{archiveBytes.Length}}}," +
                    "{\"name\":\"sha256sum.txt\",\"browser_download_url\":\"https://example.test/sha256sum.txt\"}]}");
            }
            if (url.EndsWith("sha256sum.txt", StringComparison.Ordinal))
            {
                return TextResponse($"{sha}  {assetName}\n");
            }
            if (url.EndsWith(assetName, StringComparison.Ordinal))
            {
                return BytesResponse(archiveBytes);
            }
            throw new InvalidOperationException($"unexpected request: {url}");
        }));

        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var processHost = new FakeProcessHost { OnStart = () => client.Ready = true };
        var manager = new OllamaRuntimeManager(http, processHost, _ => client, NewStore(dataDir), new MultiplexHub(), dataDir);

        await Task.WhenAll(
            manager.InstallRuntimeAsync(CancellationToken.None),
            manager.InstallRuntimeAsync(CancellationToken.None));

        Assert.Equal(AssistantRuntimeState.Running, manager.State);
        Assert.Equal(1, processHost.StartCount);
        Assert.Equal(1, manifestCalls);
    }

    [Fact]
    public async Task RemoveRuntimeAsync_waits_for_the_child_to_exit_before_deleting_the_runtime_dir()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return; // needs a real install first; zstd (Linux) extraction is unimplemented.
        }

        var dataDir = NewTempDir();
        var assetName = OllamaRuntimeManager.ResolveAssetName();
        var binaryName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var archiveBytes = BuildArchive(assetName, binaryName, Encoding.UTF8.GetBytes("fake binary"));
        var sha = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var http = new HttpClient(new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                return JsonResponse(
                    "{\"tag_name\":\"v0.32.1\",\"assets\":[" +
                    $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"https://example.test/{assetName}\",\"size\":{archiveBytes.Length}}}," +
                    "{\"name\":\"sha256sum.txt\",\"browser_download_url\":\"https://example.test/sha256sum.txt\"}]}");
            }
            if (url.EndsWith("sha256sum.txt", StringComparison.Ordinal))
            {
                return TextResponse($"{sha}  {assetName}\n");
            }
            return BytesResponse(archiveBytes);
        }));

        var order = new List<string>();
        var handle = new OrderTrackingHandle(order);
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var processHost = new FakeProcessHost { HandleToReturn = handle, OnStart = () => client.Ready = true };
        var manager = new OllamaRuntimeManager(http, processHost, _ => client, NewStore(dataDir), new MultiplexHub(), dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None);
        Assert.Equal(AssistantRuntimeState.Running, manager.State);

        await manager.RemoveRuntimeAsync(deleteModels: false, CancellationToken.None);

        Assert.Equal(new[] { "killed", "waited" }, order);
        Assert.False(Directory.Exists(Path.Combine(dataDir, "runtime", "bin")));
        Assert.Equal(AssistantRuntimeState.NotInstalled, manager.State);
    }

    [Fact]
    public async Task RemoveRuntimeAsync_is_a_no_op_when_the_runtime_is_a_detected_system_install()
    {
        var dataDir = NewTempDir();
        var binDir = Path.Combine(dataDir, "runtime", "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "ollama"), "data");

        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            OptedInStore(dataDir), new MultiplexHub(), dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None); // adopts the system install

        await manager.RemoveRuntimeAsync(deleteModels: false, CancellationToken.None);

        Assert.True(Directory.Exists(binDir)); // untouched: not Nexus's binary to delete
        Assert.Equal(AssistantRuntimeState.Running, manager.State);
    }

    [Fact]
    public async Task RemoveRuntimeAsync_retries_past_a_transiently_locked_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the locked-handle race this guards is Windows-specific (see failure log 2026-06-20).
        }

        var dataDir = NewTempDir();
        var binDir = Path.Combine(dataDir, "runtime", "bin");
        Directory.CreateDirectory(binDir);
        var lockedFile = Path.Combine(binDir, "ollama.exe");
        File.WriteAllText(lockedFile, "data");

        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            NewStore(dataDir), new MultiplexHub(), dataDir);

        var lockHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(400).ContinueWith(_ => lockHandle.Dispose());

        await manager.RemoveRuntimeAsync(deleteModels: false, CancellationToken.None);

        Assert.False(Directory.Exists(binDir));
        await release;
    }

    [Fact]
    public async Task PullModelAsync_broadcasts_progress_and_is_single_flight_per_manager()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var hub = new MultiplexHub();
        var pulls = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => pulls.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.AiAssistant);
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            OptedInStore(dataDir), hub, dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None);

        var gate = new SemaphoreSlim(0, 1);
        client.PullBehavior = async (model, onProgress, ct) =>
        {
            onProgress(new OllamaPullStatus { Status = "downloading", Total = 100, Completed = 10 });
            await gate.WaitAsync(ct);
        };

        var first = manager.PullModelAsync("qwen3.5:0.8b", CancellationToken.None);
        await Task.Delay(50); // let the first pull acquire the gate and report progress
        var second = await manager.PullModelAsync("qwen3.5:2b", CancellationToken.None);

        Assert.False(second); // busy: a pull is already in flight
        gate.Release();
        Assert.True(await first);
        Assert.Contains(PanelTopics.AiAssistant, pulls);
    }

    [Fact]
    public async Task PullModelAsync_broadcasts_a_cleared_pull_frame_when_the_pull_fails()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        var hub = new MultiplexHub();
        JsonElement? lastFrame = null;
        hub.OnBroadcastForTest += (topic, env) =>
        {
            if (topic != PanelTopics.AiAssistant)
            {
                return;
            }
            using var doc = JsonDocument.Parse(env.ToArray());
            lastFrame = doc.RootElement.GetProperty("d").Clone();
        };
        using var sub = hub.AddTestSubscription(PanelTopics.AiAssistant);
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            NewStore(dataDir), hub, dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None);

        client.PullBehavior = (model, onProgress, ct) =>
        {
            onProgress(new OllamaPullStatus { Status = "downloading", Total = 100, Completed = 40 });
            throw new InvalidOperationException($"Pulling {model} failed: disk full");
        };

        var pulled = await manager.PullModelAsync("qwen3.5:4b", CancellationToken.None);

        Assert.False(pulled);
        Assert.NotNull(lastFrame);
        Assert.False(lastFrame!.Value.TryGetProperty("pull", out _));
    }

    [Fact]
    public async Task RemoveModelAsync_returns_false_instead_of_throwing_when_the_delete_call_fails()
    {
        var dataDir = NewTempDir();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort) { Ready = true };
        client.DeleteBehavior = (_, _) => throw new HttpRequestException("connection reset");
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()), new FakeProcessHost(), _ => client,
            NewStore(dataDir), new MultiplexHub(), dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None);

        var removed = await manager.RemoveModelAsync("qwen3.5:4b", CancellationToken.None);

        Assert.False(removed);
        Assert.Null(manager.BusyKind); // EndBusy still ran despite the throw
    }

    [Fact]
    public async Task An_unexpected_child_exit_after_running_downgrades_state_and_broadcasts_a_terminal_frame()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return; // zstd (Linux) extraction is not implemented; see the archive-based install tests above.
        }

        var dataDir = NewTempDir();
        var assetName = OllamaRuntimeManager.ResolveAssetName();
        var binaryName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var archiveBytes = BuildArchive(assetName, binaryName, Encoding.UTF8.GetBytes("fake binary"));
        var sha = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var http = new HttpClient(new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                return JsonResponse(
                    "{\"tag_name\":\"v0.32.1\",\"assets\":[" +
                    $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"https://example.test/{assetName}\",\"size\":{archiveBytes.Length}}}," +
                    "{\"name\":\"sha256sum.txt\",\"browser_download_url\":\"https://example.test/sha256sum.txt\"}]}");
            }
            if (url.EndsWith("sha256sum.txt", StringComparison.Ordinal))
            {
                return TextResponse($"{sha}  {assetName}\n");
            }
            return BytesResponse(archiveBytes);
        }));

        var handle = new CrashableHandle();
        var client = new ToggleableClient(OllamaRuntimeManager.DefaultPort);
        var processHost = new FakeProcessHost { HandleToReturn = handle, OnStart = () => client.Ready = true };
        var hub = new MultiplexHub();
        var terminalFrame = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.OnBroadcastForTest += (topic, env) =>
        {
            if (topic != PanelTopics.AiAssistant)
            {
                return;
            }
            using var doc = JsonDocument.Parse(env.ToArray());
            var data = doc.RootElement.GetProperty("d");
            if (data.GetProperty("runtimeState").GetString() == "error")
            {
                terminalFrame.TrySetResult(data.Clone());
            }
        };
        using var sub = hub.AddTestSubscription(PanelTopics.AiAssistant);

        var manager = new OllamaRuntimeManager(http, processHost, _ => client, NewStore(dataDir), hub, dataDir);
        await manager.InstallRuntimeAsync(CancellationToken.None);
        Assert.Equal(AssistantRuntimeState.Running, manager.State);

        handle.SimulateCrash();
        var frame = await terminalFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AssistantRuntimeState.Error, manager.State);
        Assert.Null(manager.Client);
        Assert.Equal("The Ollama runtime stopped unexpectedly.", manager.LastError);
        Assert.False(frame.TryGetProperty("pull", out _)); // no pull row is left wedged by the crash
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] BuildArchive(string assetName, string binaryName, byte[] content)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "nexus-ollama-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            File.WriteAllBytes(Path.Combine(srcDir, binaryName), content);
            var outPath = Path.Combine(Path.GetTempPath(), "nexus-ollama-archive-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (assetName.EndsWith(".zip", StringComparison.Ordinal))
                {
                    ZipFile.CreateFromDirectory(srcDir, outPath);
                }
                else
                {
                    var tarPath = outPath + ".tar";
                    TarFile.CreateFromDirectory(srcDir, tarPath, includeBaseDirectory: false);
                    using (var tarStream = File.OpenRead(tarPath))
                    using (var outStream = File.Create(outPath))
                    using (var gz = new GZipStream(outStream, CompressionLevel.Fastest))
                    {
                        tarStream.CopyTo(gz);
                    }
                    File.Delete(tarPath);
                }
                return File.ReadAllBytes(outPath);
            }
            finally
            {
                if (File.Exists(outPath)) File.Delete(outPath);
            }
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TextResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage BytesResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"unexpected HTTP request: {request.RequestUri}");
    }

    private sealed class ToggleableClient : IOllamaClient
    {
        public ToggleableClient(int port) => Port = port;
        public bool Ready { get; set; }
        public Func<string, Action<OllamaPullStatus>, CancellationToken, Task>? PullBehavior { get; set; }
        public Func<string, CancellationToken, Task<bool>>? DeleteBehavior { get; set; }
        public int Port { get; }

        public int VersionProbes { get; private set; }
        public Task<string?> TryGetVersionAsync(CancellationToken ct)
        {
            VersionProbes++;
            return Task.FromResult(Ready ? "0.32.1" : null);
        }
        public Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OllamaModelInfo>>(Array.Empty<OllamaModelInfo>());
        public Task<IReadOnlyList<OllamaModelInfo>> ListLoadedModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OllamaModelInfo>>(Array.Empty<OllamaModelInfo>());
        public Task PullModelAsync(string model, Action<OllamaPullStatus> onProgress, CancellationToken ct) =>
            PullBehavior?.Invoke(model, onProgress, ct) ?? Task.CompletedTask;
        public Task<bool> DeleteModelAsync(string model, CancellationToken ct) =>
            DeleteBehavior?.Invoke(model, ct) ?? Task.FromResult(true);
        public Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDef>? tools, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised by manager tests.");
    }

    private sealed class FakeProcessHost : IOllamaProcessHost
    {
        public Action? OnStart { get; set; }
        public IOllamaProcessHandle? HandleToReturn { get; set; }
        public int StartCount { get; private set; }

        public IOllamaProcessHandle Start(string exePath, string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            StartCount++;
            OnStart?.Invoke();
            return HandleToReturn ?? new FakeHandle();
        }

        private sealed class FakeHandle : IOllamaProcessHandle
        {
            public bool HasExited => false;
            public Task WaitForExitAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
            public void Kill()
            {
            }
        }
    }

    /// <summary>
    /// Backed by a real completion signal (not "complete on first call") so it
    /// supports the two independent concurrent waiters production code now has:
    /// the child-exit watcher started right after StartChildAsync succeeds, and
    /// StopChildAsync's own wait after Kill. "waited" is logged once regardless
    /// of how many callers observe the same exit.
    /// </summary>
    private sealed class OrderTrackingHandle : IOllamaProcessHandle
    {
        private readonly List<string> _order;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitedLogged;
        public OrderTrackingHandle(List<string> order) => _order = order;
        public bool HasExited => _exited.Task.IsCompleted;

        public async Task WaitForExitAsync(CancellationToken ct)
        {
            await _exited.Task.WaitAsync(ct).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _waitedLogged, 1) == 0)
            {
                _order.Add("waited");
            }
        }

        public void Kill()
        {
            _order.Add("killed");
            _exited.TrySetResult();
        }
    }

    /// <summary>Simulates the child dying on its own (crash), independent of a
    /// deliberate Kill call, for exercising the unexpected-exit watcher.</summary>
    private sealed class CrashableHandle : IOllamaProcessHandle
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited => _exited.Task.IsCompleted;
        public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);
        public void Kill() => _exited.TrySetResult();
        public void SimulateCrash() => _exited.TrySetResult();
    }
}
