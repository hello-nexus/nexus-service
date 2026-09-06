using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for get_sensors' historyId field: it must land on exactly the
/// sensor SystemMetricsSource itself samples for a given history series (see
/// HistoryIdMappingTests for the mapping's own per-family coverage), and stay
/// null for every sensor with no history-tracked equivalent.
/// </summary>
public sealed class GetSensorsToolTests
{
    private static async Task<JsonElement> RunAsync(McpTestHarness.StubSensorProvider sensors, string device, string? drive = null)
    {
        var tool = new GetSensorsTool(sensors);
        var argsObj = drive is null ? (object)new { device } : new { device, drive };
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(argsObj), CancellationToken.None);
        Assert.False(result.IsError);
        return JsonDocument.Parse(result.Text).RootElement.Clone();
    }

    // historyId is written under DefaultIgnoreCondition.WhenWritingNull, so a
    // null value is an absent property, not a JSON null.
    private static string? HistoryIdOf(JsonElement root, string sensorId)
    {
        var sensor = root.GetProperty("sensors").EnumerateArray().Single(s => s.GetProperty("id").GetString() == sensorId);
        return sensor.TryGetProperty("historyId", out var v) ? v.GetString() : null;
    }

    [Fact]
    public async Task Cpu_NeverTagsALoadSensor()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Cpu = new List<HardwareSensor>
            {
                new() { Id = "/amdcpu/0/temperature/1", Name = "Core (Tctl/Tdie)", Type = "Temperature" },
                new() { Id = "/amdcpu/0/load/0", Name = "CPU Total", Type = "Load" },
            },
        };

        var root = await RunAsync(sensors, "cpu");

        Assert.Equal("cpu.temp", HistoryIdOf(root, "/amdcpu/0/temperature/1"));
        Assert.Null(HistoryIdOf(root, "/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task Cpu_PrefersTheSensorNamedPackage_OverTheFirstTemperatureSensor()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Cpu = new List<HardwareSensor>
            {
                new() { Id = "/amdcpu/0/temperature/1", Name = "Core #1", Type = "Temperature" },
                new() { Id = "/amdcpu/0/temperature/2", Name = "Package", Type = "Temperature" },
            },
        };

        var root = await RunAsync(sensors, "cpu");

        Assert.Equal("cpu.temp", HistoryIdOf(root, "/amdcpu/0/temperature/2"));
        Assert.Null(HistoryIdOf(root, "/amdcpu/0/temperature/1"));
    }

    [Fact]
    public async Task Gpu_TagsLoadAndTempPerAdapter_KeyedByItsOwnGpuId()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Gpus = new List<GpuReadout>
            {
                new()
                {
                    Id = "gpu-0",
                    Name = "RTX 5080",
                    Sensors = new List<HardwareSensor>
                    {
                        new() { Id = "/nvidiagpu/0/load/0", Name = "GPU Core", Type = "Load" },
                        new() { Id = "/nvidiagpu/0/temperature/0", Name = "GPU Core", Type = "Temperature" },
                    },
                },
                new()
                {
                    Id = "gpu-1",
                    Name = "RTX 4060",
                    Sensors = new List<HardwareSensor>
                    {
                        new() { Id = "/nvidiagpu/1/load/0", Name = "GPU Core", Type = "Load" },
                    },
                },
            },
        };

        var root = await RunAsync(sensors, "gpu");

        Assert.Equal("gpu.gpu-0.load", HistoryIdOf(root, "/nvidiagpu/0/load/0"));
        Assert.Equal("gpu.gpu-0.temp", HistoryIdOf(root, "/nvidiagpu/0/temperature/0"));
        Assert.Equal("gpu.gpu-1.load", HistoryIdOf(root, "/nvidiagpu/1/load/0"));
    }

    [Fact]
    public async Task Memory_TagsOnlyDimmNamedTemperatureSensors_InEncounterOrder()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Memory = new List<HardwareSensor>
            {
                new() { Id = "/ram/0/load/0", Name = "Memory", Type = "Load" },
                new() { Id = "/ram/dimm/0", Name = "DIMM_A1", Type = "Temperature" },
                new() { Id = "/ram/dimm/1", Name = "DIMM_B1", Type = "Temperature" },
            },
        };

        var root = await RunAsync(sensors, "memory");

        Assert.Null(HistoryIdOf(root, "/ram/0/load/0"));
        Assert.Equal("temp.ram:0", HistoryIdOf(root, "/ram/dimm/0"));
        Assert.Equal("temp.ram:1", HistoryIdOf(root, "/ram/dimm/1"));
    }

    [Fact]
    public async Task Motherboard_NeverTagsAHistoryId()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Motherboard = new List<HardwareSensor>
            {
                new() { Id = "/lpc/nct6798d/0/fan/2", Name = "Fan 3", Type = "Fan" },
                new() { Id = "/lpc/nct6798d/0/control/2", Name = "Fan 3", Type = "Control" },
            },
        };

        var root = await RunAsync(sensors, "motherboard");

        Assert.Null(HistoryIdOf(root, "/lpc/nct6798d/0/fan/2"));
        Assert.Null(HistoryIdOf(root, "/lpc/nct6798d/0/control/2"));
    }

    [Fact]
    public async Task Storage_NeverTagsAHistoryId()
    {
        var sensors = new McpTestHarness.StubSensorProvider
        {
            Storage = new Dictionary<string, StorageComponent>
            {
                ["smart/nvme/0"] = new()
                {
                    Id = "smart/nvme/0",
                    Name = "Samsung 990 Pro",
                    Sensors = new List<HardwareSensor>
                    {
                        new() { Id = "smart/nvme/0/temperature/0", Name = "Temperature", Type = "Temperature" },
                    },
                },
            },
        };

        var root = await RunAsync(sensors, "storage");

        Assert.Null(HistoryIdOf(root, "smart/nvme/0/temperature/0"));
    }
}
