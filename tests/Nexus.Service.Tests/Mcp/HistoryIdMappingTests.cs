using System.Collections.Generic;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for HistoryIdMapping's own selection logic, independent of
/// the tools that call it (GetSensorsTool, QuerySensorHistoryTool - see their
/// test files for the end-to-end wiring).
/// </summary>
public sealed class HistoryIdMappingTests
{
    [Fact]
    public void CpuTempSensorId_ReturnsNull_WhenThereIsNoTemperatureSensor()
    {
        var cpuSensors = new List<HardwareSensor> { new() { Id = "a", Type = "Load", Name = "CPU Total" } };

        Assert.Null(HistoryIdMapping.CpuTempSensorId(cpuSensors));
    }

    [Fact]
    public void CpuTempSensorId_FallsBackToTheFirstTemperatureSensor_WhenNoneIsNamedPackage()
    {
        var cpuSensors = new List<HardwareSensor>
        {
            new() { Id = "a", Type = "Temperature", Name = "Core #1" },
            new() { Id = "b", Type = "Temperature", Name = "Core #2" },
        };

        Assert.Equal("a", HistoryIdMapping.CpuTempSensorId(cpuSensors));
    }

    [Fact]
    public void GpuSensorIds_MapsLoadAndTemp_ToTheSanitizedGpuId()
    {
        var gpu = new GpuReadout
        {
            Id = "/nvidiagpu/0",
            Sensors = new List<HardwareSensor>
            {
                new() { Id = "load-id", Type = "Load", Name = "GPU Core" },
                new() { Id = "temp-id", Type = "Temperature", Name = "GPU Core" },
                new() { Id = "clock-id", Type = "Clock", Name = "GPU Core" },
            },
        };

        var map = HistoryIdMapping.GpuSensorIds(gpu);

        Assert.Equal("gpu.nvidiagpu-0.load", map["load-id"]);
        Assert.Equal("gpu.nvidiagpu-0.temp", map["temp-id"]);
        Assert.False(map.ContainsKey("clock-id"));
    }

    [Fact]
    public void RamSensorIds_SkipsNonTemperatureAndUnnamedSensors_ThenIndexesTheRest()
    {
        var memorySensors = new List<HardwareSensor>
        {
            new() { Id = "load-id", Type = "Load", Name = "Memory" },
            new() { Id = "wrong-name-id", Type = "Temperature", Name = "VRM" },
            new() { Id = "dimm-a", Type = "Temperature", Name = "DIMM_A1" },
            new() { Id = "dimm-b", Type = "Temperature", Name = "Memory Bank 2" },
        };

        var map = HistoryIdMapping.RamSensorIds(memorySensors);

        Assert.False(map.ContainsKey("load-id"));
        Assert.False(map.ContainsKey("wrong-name-id"));
        Assert.Equal("temp.ram:0", map["dimm-a"]);
        Assert.Equal("temp.ram:1", map["dimm-b"]);
    }
}
