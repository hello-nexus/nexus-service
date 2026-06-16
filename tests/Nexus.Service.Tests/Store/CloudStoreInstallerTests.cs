using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;
using Nexus.Service.Store;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Store;

/// <summary>
/// Unit tests for the cloud install pipeline. Uses a stub HttpMessageHandler
/// to avoid real network calls. Verifies artifact_unavailable handling,
/// success path, and uninstall.
/// </summary>
public class CloudStoreInstallerTests : IDisposable
{
    private readonly string _userRoot;
    private readonly string _bundledRoot;
    private readonly string _settingsPath;

    public CloudStoreInstallerTests()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _userRoot = Path.Combine(Path.GetTempPath(), "nexus-store-test-user-" + id);
        _bundledRoot = Path.Combine(Path.GetTempPath(), "nexus-store-test-bundled-" + id);
        _settingsPath = Path.Combine(Path.GetTempPath(), "nexus-store-test-settings-" + id + ".json");
        Directory.CreateDirectory(_userRoot);
        Directory.CreateDirectory(_bundledRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_userRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_bundledRoot, recursive: true); } catch { /* best effort */ }
        try { File.Delete(_settingsPath); } catch { /* best effort */ }
    }

    private AppInstallPaths.Root[] TestRoots()
        =>
        [
            new(_userRoot, AppInstallPaths.Source.User),
            new(_bundledRoot, AppInstallPaths.Source.Bundled),
        ];

    private AppRegistry NewRegistry()
        => new AppRegistry(() => TestRoots());

    private CloudStoreInstaller NewInstaller(CloudApiClient cloud, TestableConfigStore store)
        => new CloudStoreInstaller(cloud, store, NewRegistry(), TestRoots);

    private static StubHttpClientFactory MakeFactory(HttpMessageHandler handler)
        => new StubHttpClientFactory(handler);

    [Fact]
    public async Task Install_WhenNotLinked_ReturnsNotLinked()
    {
        var store = new TestableConfigStore(_settingsPath);
        var cloud = new CloudApiClient(MakeFactory(new NoOpHandler()));
        var installer = NewInstaller(cloud, store);

        var result = await installer.InstallAsync("com.example.app", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("not_linked", result.Reason);
    }

    [Fact]
    public async Task Install_WhenAcquireFails_ReturnsAcquireFailed()
    {
        var store = new TestableConfigStore(_settingsPath);
        store.Update(s => s.CloudAccount.AccessToken = "tok");

        var handler = new SequentialHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
        );

        var cloud = new CloudApiClient(MakeFactory(handler));
        var installer = NewInstaller(cloud, store);

        var result = await installer.InstallAsync("com.example.app", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("acquire_failed", result.Reason);
    }

    [Fact]
    public async Task Install_WhenDownload404_ReturnsArtifactUnavailable()
    {
        var store = new TestableConfigStore(_settingsPath);
        store.Update(s => s.CloudAccount.AccessToken = "tok");

        var downloadInfo = new CloudAppDownloadInfo
        {
            Url = "https://cdn.example.com/app.nexus-app",
            Format = "nexus-app",
        };
        var downloadInfoJson = JsonSerializer.Serialize(downloadInfo, AppJsonContext.Default.CloudAppDownloadInfo);

        var handler = new SequentialHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(downloadInfoJson, Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.NotFound)
        );

        var cloud = new CloudApiClient(MakeFactory(handler));
        var installer = NewInstaller(cloud, store);

        var result = await installer.InstallAsync("com.example.app", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("artifact_unavailable", result.Reason);
    }

    [Fact]
    public async Task Uninstall_RemovesDirectory()
    {
        var appId = "com.example.testapp";
        var appDir = Path.Combine(_userRoot, appId);
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "manifest.json"), "{}");

        var store = new TestableConfigStore(_settingsPath);
        var cloud = new CloudApiClient(MakeFactory(new NoOpHandler()));
        var installer = NewInstaller(cloud, store);

        var result = await installer.UninstallAsync(appId, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(appDir));
    }

    [Fact]
    public async Task Uninstall_WhenDirAbsent_ReturnsOk()
    {
        var store = new TestableConfigStore(_settingsPath);
        var cloud = new CloudApiClient(MakeFactory(new NoOpHandler()));
        var installer = NewInstaller(cloud, store);

        var result = await installer.UninstallAsync("com.example.nonexistent", CancellationToken.None);

        Assert.True(result.Ok);
    }
}

/// <summary>Responds with pre-set responses in sequence.</summary>
internal sealed class SequentialHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage[] _responses;
    private int _index;

    public SequentialHandler(params HttpResponseMessage[] responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_index < _responses.Length)
        {
            return Task.FromResult(_responses[_index++]);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

/// <summary>Always returns 503; used when no HTTP calls are expected.</summary>
internal sealed class NoOpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
}

/// <summary>Wraps a handler in IHttpClientFactory.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
}
