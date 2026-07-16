using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public class AppInstallActionsTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolCacheRoot;

    public AppInstallActionsTests()
    {
        var b = "nexus-appinstall-tests-" + Guid.NewGuid().ToString("N")[..8];
        _root = Path.Combine(Path.GetTempPath(), b, "apps");
        _toolCacheRoot = Path.Combine(Path.GetTempPath(), b, "tools");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_toolCacheRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private AppRegistry NewRegistry(string id, bool withDriver, string target = "host-exe", string? package = null)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");

        var driverBlock = withDriver
            ? BuildDriverJson(target, package)
            : "";

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"" + id + "\",\"name\":\"x\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.0.1\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"]," +
            "\"sizes\":[\"2x2\"],\"capabilities\":{}" + driverBlock + "}");

        return new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
    }

    private static string BuildDriverJson(string target, string? package)
    {
        var pkg = package is not null ? $",\"package\":\"{package}\"" : "";
        return ",\"driver\":{\"toolId\":\"test-tool\"" +
               ",\"match\":{\"vid\":\"1234\",\"pids\":[\"0001\"]}" +
               ",\"variants\":{\"0001\":\"v1\"}" +
               ",\"manifestUrlBase\":\"https://assets.hellonexus.com/test-tool\"" +
               ",\"filePattern\":\"*.bin\"" +
               $",\"target\":\"{target}\"" +
               pkg + "}";
    }

    private static ExternalToolManager NewToolManager(string? manifestJson = null)
    {
        var handler = manifestJson is not null
            ? (HttpMessageHandler)new StaticManifestHandler(manifestJson)
            : new OfflineHandler();
        return new ExternalToolManager(new HttpClient(handler), Path.GetTempPath());
    }

    private IServiceProvider BuildServices(
        AppRegistry registry,
        ExternalToolManager? toolManager = null,
        IAdbDeviceRegistry? adbRegistry = null,
        bool driverExe = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton(toolManager ?? NewToolManager());
        services.AddSingleton(adbRegistry ?? new AdbDeviceRegistry());
        services.AddSingleton<IUsbEnumerator, StubUsbEnumerator>();
        services.AddSingleton<IToolInstallStrategy, HostExeInstallStrategy>();
        // This suite is the vendor driver path's coverage, so it enables it by
        // default. It ships disabled (the AW5 is driven natively); see DriverExePolicy.
        services.AddSingleton(new DriverExePolicy(enabled: driverExe));
        return services.BuildServiceProvider();
    }

    private static async Task<JsonElement?> InvokeAction(
        string actionName,
        IServiceProvider services,
        Dictionary<string, JsonElement>? args = null,
        string appId = "com.example.app")
    {
        var registry = new AppActionRegistry();
        AppInstallActions.RegisterAll(registry);

        if (!registry.TryGet(actionName, out var handler))
        {
            throw new InvalidOperationException($"action '{actionName}' not found");
        }

        var enriched = new Dictionary<string, JsonElement>(args ?? new(), StringComparer.Ordinal);
        using var idDoc = JsonDocument.Parse($"\"{appId}\"");
        enriched["__appId"] = idDoc.RootElement.Clone();

        return await handler(services, enriched, CancellationToken.None);
    }

    // ── Tests: app.installStatus ──────────────────────────────────────────────

    [Fact]
    public async Task InstallStatus_returns_hasInstall_false_when_no_driver_block()
    {
        var registry = NewRegistry("com.example.app", withDriver: false);
        var services = BuildServices(registry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.False(obj.GetProperty("hasInstall").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_android_adb_device_present_when_registered()
    {
        var registry = NewRegistry("com.example.app", withDriver: true, target: "android-adb", package: "com.test.app");
        var adbRegistry = new AdbDeviceRegistry();
        adbRegistry.Register(new StubAdbDeviceTarget("com.test.app"));
        var services = BuildServices(registry, adbRegistry: adbRegistry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.True(obj.GetProperty("hasInstall").GetBoolean());
        Assert.Equal("android-adb", obj.GetProperty("target").GetString());
        Assert.True(obj.GetProperty("devicePresent").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_android_adb_device_absent_when_not_registered()
    {
        var registry = NewRegistry("com.example.app", withDriver: true, target: "android-adb", package: "com.test.app");
        var services = BuildServices(registry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.True(obj.GetProperty("hasInstall").GetBoolean());
        Assert.Equal("android-adb", obj.GetProperty("target").GetString());
        Assert.False(obj.GetProperty("devicePresent").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_updateAvailable_true_when_not_running_and_version_known()
    {
        var manifestJson = "{\"latestVersion\":\"1.0.0\",\"versions\":{\"1.0.0\":" +
                           "{\"version\":\"1.0.0\",\"fileName\":\"tool.bin\"," +
                           "\"sha256\":\"" + new string('a', 64) + "\",\"size\":1024}}}";

        var registry = NewRegistry("com.example.app", withDriver: true, target: "host-exe");
        var toolManager = NewToolManager(manifestJson);
        var services = BuildServices(registry, toolManager);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.True(obj.GetProperty("hasInstall").GetBoolean());
        Assert.Equal("1.0.0", obj.GetProperty("latestVersion").GetString());
        Assert.True(obj.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_android_reads_installedVersion_and_updateAvailable_from_device()
    {
        // manifest: latestVersion=2.0.0 versionCode=20; device: installed versionCode=10 => updateAvailable
        var manifestJson = "{\"latestVersion\":\"2.0.0\",\"versions\":{\"2.0.0\":" +
                           "{\"version\":\"2.0.0\",\"versionCode\":20,\"fileName\":\"app.apk\"," +
                           "\"sha256\":\"" + new string('b', 64) + "\",\"size\":2048}}}";

        var registry = NewRegistry("com.example.app", withDriver: true, target: "android-adb", package: "com.test.app");
        var toolManager = NewToolManager(manifestJson);
        var adbRegistry = new AdbDeviceRegistry();
        // ShellAsync returns dumpsys output with versionName=1.0.0 and versionCode=10
        adbRegistry.Register(new StubAdbDeviceTarget("com.test.app",
            "  versionName=1.0.0\n  versionCode=10 targetSdk=33\n"));
        var services = BuildServices(registry, toolManager, adbRegistry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.True(obj.GetProperty("hasInstall").GetBoolean());
        Assert.Equal("android-adb", obj.GetProperty("target").GetString());
        Assert.True(obj.GetProperty("devicePresent").GetBoolean());
        Assert.Equal("1.0.0", obj.GetProperty("installedVersion").GetString());
        Assert.Equal("2.0.0", obj.GetProperty("latestVersion").GetString());
        Assert.True(obj.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_android_updateAvailable_false_when_already_current()
    {
        // manifest: latestVersion=1.0.0 versionCode=10; device: installed versionCode=10 => not updateAvailable
        var manifestJson = "{\"latestVersion\":\"1.0.0\",\"versions\":{\"1.0.0\":" +
                           "{\"version\":\"1.0.0\",\"versionCode\":10,\"fileName\":\"app.apk\"," +
                           "\"sha256\":\"" + new string('c', 64) + "\",\"size\":1024}}}";

        var registry = NewRegistry("com.example.app", withDriver: true, target: "android-adb", package: "com.test.app");
        var toolManager = NewToolManager(manifestJson);
        var adbRegistry = new AdbDeviceRegistry();
        adbRegistry.Register(new StubAdbDeviceTarget("com.test.app",
            "  versionName=1.0.0\n  versionCode=10 targetSdk=33\n"));
        var services = BuildServices(registry, toolManager, adbRegistry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.Equal("1.0.0", obj.GetProperty("installedVersion").GetString());
        Assert.False(obj.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task InstallStatus_android_installedVersion_absent_when_no_device()
    {
        var registry = NewRegistry("com.example.app", withDriver: true, target: "android-adb", package: "com.test.app");
        // No device registered
        var services = BuildServices(registry);

        var result = await InvokeAction("app.installStatus", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.False(obj.GetProperty("devicePresent").GetBoolean());
        // WhenWritingNull omits the property when no device is present
        Assert.False(obj.TryGetProperty("installedVersion", out _));
        Assert.False(obj.GetProperty("updateAvailable").GetBoolean());
    }

    // ── Tests: app.install ────────────────────────────────────────────────────

    [Fact]
    public async Task Install_returns_started_false_reason_no_install_block_when_no_driver()
    {
        var registry = NewRegistry("com.example.app", withDriver: false);
        var services = BuildServices(registry);

        var result = await InvokeAction("app.install", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.False(obj.GetProperty("started").GetBoolean());
        Assert.Equal("no-install-block", obj.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Install_refuses_while_the_vendor_driver_exe_is_disabled()
    {
        // The shipped configuration: Nexus drives the AW5 itself, so pressing
        // Install must not fetch or start a second writer for the same device.
        var registry = NewRegistry("com.example.app", withDriver: true, target: "host-exe");
        var services = BuildServices(registry, driverExe: false);

        var result = await InvokeAction("app.install", services);

        var obj = result!.Value;
        Assert.False(obj.GetProperty("started").GetBoolean());
        Assert.Equal("driver-exe-disabled", obj.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InstallStatus_offers_no_install_while_the_vendor_driver_exe_is_disabled()
    {
        // Reported as having no install block at all, so the app page renders no
        // button for a path the service refuses.
        var registry = NewRegistry("com.example.app", withDriver: true, target: "host-exe");
        var services = BuildServices(registry, driverExe: false);

        var result = await InvokeAction("app.installStatus", services);

        Assert.False(result!.Value.GetProperty("hasInstall").GetBoolean());
    }

    [Fact]
    public async Task Install_returns_started_true_when_driver_present()
    {
        var registry = NewRegistry("com.example.app", withDriver: true, target: "host-exe");
        var services = BuildServices(registry);

        var result = await InvokeAction("app.install", services);

        Assert.NotNull(result);
        var obj = result!.Value;
        Assert.True(obj.GetProperty("started").GetBoolean());
        // reason is null so WhenWritingNull omits it from the JSON output
        Assert.False(obj.TryGetProperty("reason", out _));
    }

    // ── Inner test helpers ────────────────────────────────────────────────────

    private sealed class StaticManifestHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticManifestHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class StubAdbDeviceTarget : IAdbDeviceTarget
    {
        private readonly string _dumpsysOutput;
        public string Package { get; }
        public string Serial { get; } = "emulator-5554";
        public bool InstallInProgress { get; set; }

        public StubAdbDeviceTarget(string package, string dumpsysOutput = "")
        {
            Package = package;
            _dumpsysOutput = dumpsysOutput;
        }

        public Task<string> ShellAsync(string command, CancellationToken ct)
            => Task.FromResult(_dumpsysOutput);
    }
}
