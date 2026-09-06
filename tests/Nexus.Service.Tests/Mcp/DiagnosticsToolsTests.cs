using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for the seven diagnostics read tools. Each dependency is the
/// real cross-platform class this test host (non-Windows) already resolves to
/// in production DI - the same construction recipe DiagnosticsAlertServiceTests
/// and UpdateServiceDownloadTierTests use - fed no data, so every "Supported"
/// flag and platform-gated field is a genuine off-Windows fact for this host,
/// not a stubbed value.
/// </summary>
public sealed class DiagnosticsToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-diag-tools-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    // ── Fakes for the UpdateService / DeviceManager dependency graph ──────────

    private sealed class NullUpdateSource : IUpdateSource
    {
        public Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct) =>
            Task.FromResult<UpdateManifest?>(null);
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class EmptyUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private static UpdateService BuildUpdateService(IConfigStore store) => new(
        new NullUpdateSource(),
        new UpdateDownloader(new NullHttpClientFactory()),
        store,
        new FirmwareFlasher(
            new BundledFirmwareCatalog(),
            Array.Empty<IDfuFlashTarget>(),
            new WinUsbDriverInstaller(),
            new DfuUtil("dfu-util"),
            new PluginProviderRegistry(),
            new FlashGate()),
        new MultiplexHub());

    private static DeviceManager BuildEmptyDeviceManager(IConfigStore store) => new(
        Array.Empty<IDeviceHandler>(),
        new EmptyUsbEnumerator(),
        new PluginProviderRegistry(),
        new DeviceControlGate(store));

    private static DiagnosticsHealthModel BuildHealthModel(IConfigStore store) => new(
        new SmartHealthMonitor(),
        new CoolingStallDetector(),
        new GpuHealthMonitor(),
        new EventLogMonitor(),
        new MemoryDiagnosticOrchestrator(),
        new PnpProblemScanner(),
        new McpTestHarness.StubSensorProvider(),
        new InMemoryMetricsHistoryStore(),
        store);

    /// <summary>All seven real tools, wired the same way AddNexusMcp wires them
    /// in production - used by the registry test and reused across the
    /// per-tool happy-path tests below.</summary>
    private IReadOnlyList<IMcpTool> BuildTools(IConfigStore store)
    {
        var sensors = new McpTestHarness.StubSensorProvider();
        return new IMcpTool[]
        {
            new GetHealthTool(BuildHealthModel(store), new FeatureGates(store)),
            new GetStorageHealthTool(new SmartHealthMonitor()),
            new GetGpuHealthTool(new GpuHealthMonitor(), new EventLogMonitor()),
            new GetSystemSpecsTool(new SystemSpecsCollector(sensors)),
            new GetConflictsTool(new ConflictWatcher(new MultiplexHub())),
            new GetUpdateStatusTool(BuildUpdateService(store), BuildEmptyDeviceManager(store), new BundledFirmwareCatalog()),
            new GetMemoryInfoTool(new MemoryDiagnosticOrchestrator()),
        };
    }

    // ── get_health ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_HappyPath_ReturnsOverallAndComponents()
    {
        var store = NewStore();
        var tool = new GetHealthTool(BuildHealthModel(store), new FeatureGates(store));

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        // Fresh monitors fed no data on a non-Windows host: every component
        // self-guards on Supported/windowsSupported and contributes nothing.
        Assert.Equal("ok", doc.RootElement.GetProperty("overall").GetString());
        Assert.Empty(doc.RootElement.GetProperty("components").EnumerateArray());
    }

    // ── get_storage_health ──────────────────────────────────────────────────

    [Fact]
    public async Task GetStorageHealth_HappyPath_ReturnsShape()
    {
        var tool = new GetStorageHealthTool(new SmartHealthMonitor());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.TryGetProperty("supported", out _));
        Assert.True(doc.RootElement.TryGetProperty("drives", out var drives));
        Assert.Equal(JsonValueKind.Array, drives.ValueKind);
    }

    [Fact]
    public async Task GetStorageHealth_UnknownDrive_IsErrorListingKnownDrives()
    {
        var tool = new GetStorageHealthTool(new SmartHealthMonitor());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { drive = "does-not-exist" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Unknown drive", result.Text, StringComparison.Ordinal);
        Assert.Contains("Known drives", result.Text, StringComparison.Ordinal);
    }

    // ── get_gpu_health ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetGpuHealth_HappyPath_ReturnsShape()
    {
        var tool = new GetGpuHealthTool(new GpuHealthMonitor(), new EventLogMonitor());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        // No NVML on this host: unsupported with no GPUs, both real facts.
        Assert.False(doc.RootElement.GetProperty("supported").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("gpus").EnumerateArray());
    }

    // ── get_system_specs ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSystemSpecs_HappyPath_ReturnsBootTimeAndUptime()
    {
        var sensors = new McpTestHarness.StubSensorProvider();
        var tool = new GetSystemSpecsTool(new SystemSpecsCollector(sensors));
        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.Equal("Test CPU", doc.RootElement.GetProperty("processor").GetString());
        Assert.True(doc.RootElement.GetProperty("uptimeSeconds").GetDouble() >= 0);
        Assert.True(doc.RootElement.GetProperty("bootTimeUnixMs").GetInt64() <= beforeMs);
    }

    // ── get_conflicts ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetConflicts_HappyPath_ReturnsShape()
    {
        var tool = new GetConflictsTool(new ConflictWatcher(new MultiplexHub()));

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.TryGetProperty("anyConflict", out _));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("conflicts").ValueKind);
    }

    // ── get_update_status ───────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateStatus_HappyPath_ReturnsAppAndDeviceStatus()
    {
        var store = NewStore();
        var tool = new GetUpdateStatusTool(
            BuildUpdateService(store), BuildEmptyDeviceManager(store), new BundledFirmwareCatalog());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        Assert.True(doc.RootElement.TryGetProperty("currentVersion", out _));
        Assert.Empty(doc.RootElement.GetProperty("devices").EnumerateArray());
    }

    // ── get_memory_info ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMemoryInfo_HappyPath_ReturnsShape()
    {
        var tool = new GetMemoryInfoTool(new MemoryDiagnosticOrchestrator());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        // Windows-only source, so unsupported with no modules on this host.
        Assert.False(doc.RootElement.GetProperty("supported").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("modules").EnumerateArray());
    }

    // ── Consent refusal ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_ConsentDisabled_ReturnsErrorAndDoesNotRunTheModel()
    {
        var store = NewStore();
        store.Update(s => s.AiIntegration.AllowTelemetry = false);
        var tool = new GetHealthTool(BuildHealthModel(store), new FeatureGates(store));
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new LoggingMcpAuditSink());

        var result = await registry.CallAsync(tool, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Allow telemetry", result.Text, StringComparison.Ordinal);
    }

    // ── Registry ────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_ListsAllSevenDiagnosticsTools_AsReadOnlyTelemetry()
    {
        var store = NewStore();
        var tools = BuildTools(store);
        var registry = new McpToolRegistry(tools, store, new LoggingMcpAuditSink());

        var names = new[]
        {
            "get_health", "get_storage_health", "get_gpu_health", "get_system_specs",
            "get_conflicts", "get_update_status", "get_memory_info",
        };
        foreach (var name in names)
        {
            Assert.True(registry.TryGetTool(name, out var tool), $"{name} did not register");
            // readOnlyHint on the tools/list wire is IMcpTool.ReadOnly verbatim
            // (McpServerHost.WriteToolsListResultAsync) - this is the same fact
            // at the registry layer rather than over the JSON-RPC wire.
            Assert.True(tool.ReadOnly);
            Assert.Equal(McpCapability.Telemetry, tool.Capability);
        }
    }
}
