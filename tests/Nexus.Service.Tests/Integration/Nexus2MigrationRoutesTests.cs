using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Migration;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET/POST /migration/nexus2* over the real request pipeline, with
/// INexus2Detector swapped for a fake at the DI seam (Windows-specific
/// registry/filesystem checks are exercised only by Nexus2Detector itself,
/// compile-gated to Windows). Status composition (pending truth table) and
/// deviceEligible are cross-platform logic in the route, so they run here on
/// every OS.
/// </summary>
public sealed class Nexus2MigrationRoutesTests
{
    private sealed class FakeNexus2Detector : INexus2Detector
    {
        public Nexus2DetectionResult Result = Nexus2DetectionResult.None;
        public bool DisableAutostartResult;
        public bool CloseAppResult;
        public bool UninstallResult;
        public int UninstallCalls;

        public Nexus2DetectionResult Detect() => Result;
        public bool DisableAutostart() => DisableAutostartResult;
        public System.Threading.Tasks.Task<bool> CloseAppAsync() => System.Threading.Tasks.Task.FromResult(CloseAppResult);
        public System.Threading.Tasks.Task<bool> UninstallAsync()
        {
            UninstallCalls++;
            return System.Threading.Tasks.Task.FromResult(UninstallResult);
        }
    }

    private readonly FakeNexus2Detector _detector = new();

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<INexus2Detector>();
                s.AddSingleton<INexus2Detector>(_detector);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static PanelDeviceRecord DeviceWithSurface(string? surface) => new()
    {
        Id = "dev1",
        Capabilities = new PanelDeviceCapabilities { Surface = surface },
    };

    [Fact]
    public async Task Status_requires_a_token()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var res = await factory.CreateClient().GetAsync("/migration/nexus2");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task Dismiss_requires_a_token()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var res = await factory.CreateClient().PostAsync("/migration/nexus2/dismiss", null);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task DisableAutostart_requires_a_token()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var res = await factory.CreateClient().PostAsync("/migration/nexus2/disable-autostart", null);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task CloseApp_requires_a_token()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var res = await factory.CreateClient().PostAsync("/migration/nexus2/close-app", null);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task CloseApp_success_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.CloseAppResult = true;

            var res = await client.PostAsync("/migration/nexus2/close-app", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("error").GetBoolean());
        }
    }

    [Fact]
    public async Task CloseApp_failure_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.CloseAppResult = false;

            var res = await client.PostAsync("/migration/nexus2/close-app", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        }
    }

    [Fact]
    public async Task Uninstall_requires_auth()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var res = await factory.CreateClient().PostAsync("/migration/nexus2/uninstall", null);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal(0, _detector.UninstallCalls);
        }
    }

    [Fact]
    public async Task Uninstall_success_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.UninstallResult = true;

            var res = await client.PostAsync("/migration/nexus2/uninstall", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("error").GetBoolean());
            Assert.Equal(1, _detector.UninstallCalls);
        }
    }

    [Fact]
    public async Task Uninstall_failure_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.UninstallResult = false;

            var res = await client.PostAsync("/migration/nexus2/uninstall", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_reports_running_from_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.Result = new Nexus2DetectionResult(true, true, "2.16.0", true, Running: true);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("running").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_not_pending_when_not_detected()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.PanelDevices["dev1"] = DeviceWithSurface(PanelSurfaces.Y70));
            _detector.Result = new Nexus2DetectionResult(false, false, null, false);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.False(root.GetProperty("detected").GetBoolean());
            Assert.True(root.GetProperty("deviceEligible").GetBoolean());
            Assert.False(root.GetProperty("pending").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_not_pending_when_no_eligible_device()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.Result = new Nexus2DetectionResult(true, true, "2.16.0", true);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.True(root.GetProperty("detected").GetBoolean());
            Assert.False(root.GetProperty("deviceEligible").GetBoolean());
            Assert.False(root.GetProperty("pending").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_pending_when_detected_and_eligible_and_not_offered()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.PanelDevices["dev1"] = DeviceWithSurface(PanelSurfaces.Q60));
            _detector.Result = new Nexus2DetectionResult(true, true, "2.16.0", true);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.True(root.GetProperty("pending").GetBoolean());
            Assert.Equal("2.16.0", root.GetProperty("version").GetString());
            Assert.True(root.GetProperty("importAvailable").GetBoolean());
            Assert.True(root.GetProperty("autostartTaskPresent").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_not_pending_when_only_leftover_import_data_remains()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.PanelDevices["dev1"] = DeviceWithSurface(PanelSurfaces.Y70));
            _detector.Result = new Nexus2DetectionResult(false, true, null, true);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            var root = doc.RootElement;

            // The screen stays shut, but the import the web offers from
            // Settings is still advertised, as is the leftover task.
            Assert.False(root.GetProperty("detected").GetBoolean());
            Assert.False(root.GetProperty("pending").GetBoolean());
            Assert.True(root.GetProperty("importAvailable").GetBoolean());
            Assert.True(root.GetProperty("autostartTaskPresent").GetBoolean());
        }
    }

    [Fact]
    public async Task Status_not_pending_once_offered()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.PanelDevices["dev1"] = DeviceWithSurface(PanelSurfaces.Y70);
                s.Nexus2MigrationOffered = true;
            });
            _detector.Result = new Nexus2DetectionResult(true, true, "2.16.0", true);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("pending").GetBoolean());
        }
    }

    [Theory]
    [InlineData(PanelSurfaces.Y70, true)]
    [InlineData(PanelSurfaces.Q60, true)]
    [InlineData(PanelSurfaces.Phone, false)]
    [InlineData(PanelSurfaces.Desktop, false)]
    [InlineData(null, false)]
    public async Task DeviceEligible_reflects_panel_device_surface(string? surface, bool expectEligible)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.PanelDevices["dev1"] = DeviceWithSurface(surface));
            _detector.Result = new Nexus2DetectionResult(true, false, null, false);

            using var doc = JsonDocument.Parse(await (await client.GetAsync("/migration/nexus2")).Content.ReadAsStringAsync());
            Assert.Equal(expectEligible, doc.RootElement.GetProperty("deviceEligible").GetBoolean());
        }
    }

    [Fact]
    public async Task DisableAutostart_success_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.DisableAutostartResult = true;

            var res = await client.PostAsync("/migration/nexus2/disable-autostart", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("error").GetBoolean());
        }
    }

    [Fact]
    public async Task DisableAutostart_failure_reflects_the_detector()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            _detector.DisableAutostartResult = false;

            var res = await client.PostAsync("/migration/nexus2/disable-autostart", null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        }
    }
}

/// <summary>Dismiss sets a durable flag, so it is verified against a shared,
/// on-disk-backed factory (mirrors OnboardingCompleteIntegrationTests) rather
/// than the per-test WithWebHostBuilder factory above.</summary>
public sealed class Nexus2MigrationDismissIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public Nexus2MigrationDismissIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Dismiss_sets_the_flag_and_persists_across_a_store_reload()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/migration/nexus2/dismiss";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().Nexus2MigrationOffered);

        store.FlushNow();
        var reloaded = new JsonConfigStore(_factory.SettingsPath);
        Assert.True(reloaded.Load().Nexus2MigrationOffered);
    }
}
