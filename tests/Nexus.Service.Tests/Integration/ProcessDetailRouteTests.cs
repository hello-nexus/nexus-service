using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The three /monitoring/process-* endpoints against fake
/// IProcessDetailProvider / IProcessActionsProvider, so this runs on every
/// platform - the real Windows signature/version/helper pipeline has no
/// portable unit test, same posture as ProcessIconRouteTests.
/// </summary>
public sealed class ProcessDetailRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeProcessDetailProvider _detail = new();
    private readonly FakeProcessActionsProvider _actions = new();

    public ProcessDetailRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessDetailProvider>();
                services.AddSingleton<IProcessDetailProvider>(_detail);
                services.RemoveAll<IProcessActionsProvider>();
                services.AddSingleton<IProcessActionsProvider>(_actions);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    // ResolveExecutablePath re-resolves the pid via Process.GetProcessById, so
    // the seeded pid must be a real running process - the test process itself.
    private void SeedProcess(string name)
    {
        var processes = _factory.Services.GetRequiredService<ProcessMonitor>();
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = name, CpuPercent = 1, MemoryMb = 10 },
        });
    }

    private sealed class FakeProcessDetailProvider : IProcessDetailProvider
    {
        public ProcessFileDetail Detail = new(null, null, null, "unknown", null, null, null);
        public string? Sha256;

        public ProcessFileDetail GetFileDetail(string path) => Detail;
        public Task<string?> ComputeSha256Async(string path, CancellationToken ct) => Task.FromResult(Sha256);
    }

    private sealed class FakeProcessActionsProvider : IProcessActionsProvider
    {
        public bool Available = true;
        public (int Killed, int Failed) KillResult = (1, 0);
        public bool OpenLocationResult = true;
        public string? LastKilledName;
        public string? LastOpenedPath;

        public bool IsAvailable => Available;

        public Task<(int Killed, int Failed)> KillAsync(string processName)
        {
            LastKilledName = processName;
            return Task.FromResult(KillResult);
        }

        public Task<bool> OpenLocationAsync(string exePath)
        {
            LastOpenedPath = exePath;
            return Task.FromResult(OpenLocationResult);
        }

        public Task<bool> ActivateWindowAsync(int pid) => Task.FromResult(false);
    }

    // ----- GET /monitoring/process-info -----

    [Fact]
    public async Task ProcessInfo_MissingName_Is400()
    {
        var res = await Client().GetAsync("/monitoring/process-info");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ProcessInfo_NoLiveOrCachedProcess_Is404()
    {
        var res = await Client().GetAsync("/monitoring/process-info?name=never-seen.exe");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ProcessInfo_LiveProcess_Returns200_WithResolvedDetail()
    {
        SeedProcess("app.exe");
        _detail.Detail = new ProcessFileDetail("App", "1.0", "Acme", "signed", "Acme Inc", 1000, 2000);
        _detail.Sha256 = "abc123";

        var res = await Client().GetAsync("/monitoring/process-info?name=app.exe");
        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ProcessInfoResponse);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(body);
        Assert.True(body!.Supported);
        Assert.Equal("app.exe", body.Name);
        Assert.Equal(1, body.InstanceCount);
        Assert.Equal("App", body.Description);
        Assert.Equal("signed", body.Signed);
        Assert.Equal("Acme Inc", body.Publisher);
        Assert.Equal("abc123", body.Sha256);
    }

    // ----- POST /monitoring/process-kill -----

    [Fact]
    public async Task Kill_MissingName_Is400()
    {
        var res = await Client().PostAsJsonAsync("/monitoring/process-kill", new ProcessActionBody { Name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("Nexus")]
    [InlineData("lsass")]
    public async Task Kill_DenylistedName_Is400_AndNeverReachesTheProvider(string name)
    {
        var res = await Client().PostAsJsonAsync("/monitoring/process-kill", new ProcessActionBody { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null(_actions.LastKilledName);
    }

    [Fact]
    public async Task Kill_NoHelperAvailable_Is503()
    {
        _actions.Available = false;

        var res = await Client().PostAsJsonAsync("/monitoring/process-kill", new ProcessActionBody { Name = "app.exe" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Kill_Available_ReturnsTheProvidersCounts()
    {
        _actions.KillResult = (2, 1);

        var res = await Client().PostAsJsonAsync("/monitoring/process-kill", new ProcessActionBody { Name = "app.exe" });
        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ProcessKillResponse);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("app.exe", _actions.LastKilledName);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Killed);
        Assert.Equal(1, body.Failed);
    }

    // ----- POST /monitoring/process-open-location -----

    [Fact]
    public async Task OpenLocation_MissingName_Is400()
    {
        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task OpenLocation_DenylistedName_Is400()
    {
        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "dwm" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task OpenLocation_UnknownName_Is404()
    {
        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "never-seen.exe" });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OpenLocation_NoHelperAvailable_Is503()
    {
        SeedProcess("app.exe");
        _actions.Available = false;

        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "app.exe" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task OpenLocation_ProviderFails_Is502()
    {
        SeedProcess("app.exe");
        _actions.OpenLocationResult = false;

        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "app.exe" });

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    [Fact]
    public async Task OpenLocation_Succeeds_ReturnsOk_AndPassesTheResolvedPath()
    {
        SeedProcess("app.exe");

        var res = await Client().PostAsJsonAsync("/monitoring/process-open-location", new ProcessActionBody { Name = "app.exe" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(_actions.LastOpenedPath);
    }
}
