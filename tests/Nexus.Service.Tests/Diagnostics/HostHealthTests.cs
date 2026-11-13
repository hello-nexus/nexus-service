using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class HostHealthTests
{
    private const string FailedOutput =
        "● logrotate.service                                loaded failed failed Rotate log files\n" +
        "● app-nvidia\\x2dsettings\\x2dload@autostart.service loaded failed failed Autostart\n";

    private static HealthComponent Component(HostHealthSnapshot? host, string id, bool windows = false)
    {
        var result = DiagnosticsHealthModel.Compute(
            smart: new SmartSnapshot { Supported = false, Drives = Array.Empty<SmartDriveInfo>() },
            cooling: new CoolingStallSnapshot(false, Array.Empty<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: windows,
            generatedAtUtc: new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
            host: host);
        return Assert.Single(result.Components, c => c.Id == id);
    }

    [Fact]
    public void ParseFailedUnits_ReadsBulletedAndPlainRows()
    {
        var units = HostHealthProbe.ParseFailedUnits(FailedOutput + "garbage line\nplain.service loaded failed failed X\n");
        Assert.Equal(
            new[] { "logrotate.service", "app-nvidia\\x2dsettings\\x2dload@autostart.service", "plain.service" },
            units);
    }

    [Fact]
    public void ParseFailedUnits_EmptyOutput_IsEmpty()
    {
        Assert.Empty(HostHealthProbe.ParseFailedUnits(""));
    }

    [Theory]
    [InlineData("MemTotal: 100 kB\nHardwareCorrupted:      0 kB\n", 0)]
    [InlineData("MemTotal: 100 kB\nHardwareCorrupted:   2048 kB\n", 2048)]
    [InlineData("MemTotal: 100 kB\n", 0)]
    public void ParseHardwareCorruptedKb_HandlesPresenceAndAbsence(string meminfo, long expected)
    {
        Assert.Equal(expected, HostHealthProbe.ParseHardwareCorruptedKb(meminfo));
    }

    [Fact]
    public void ReadEdacErrors_SumsCountersAndToleratesMissingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "edac-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, HostHealthProbe.ReadEdacErrors(root));
            Directory.CreateDirectory(Path.Combine(root, "mc0"));
            Directory.CreateDirectory(Path.Combine(root, "mc1"));
            File.WriteAllText(Path.Combine(root, "mc0", "ce_count"), "3\n");
            File.WriteAllText(Path.Combine(root, "mc0", "ue_count"), "0\n");
            File.WriteAllText(Path.Combine(root, "mc1", "ce_count"), "2\n");
            Assert.Equal(5, HostHealthProbe.ReadEdacErrors(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Linux_NoFailedUnits_SystemIsHealthy_MemoryIsUnknownWithReason()
    {
        var host = new HostHealthSnapshot(true, Array.Empty<string>(), 0, 0);
        var system = Component(host, "system");
        var memory = Component(host, "memory");

        Assert.Equal(HealthStatuses.Ok, system.Status);
        Assert.Empty(system.Reasons);
        Assert.Equal(HealthStatuses.Unknown, memory.Status);
        Assert.Equal("memory.noHealthSource", Assert.Single(memory.Reasons).Code);
    }

    [Fact]
    public void Linux_FailedUnit_WarnsAndNamesTheUnit()
    {
        var host = new HostHealthSnapshot(true, new[] { "logrotate.service" }, 0, 0);
        var system = Component(host, "system");

        Assert.Equal(HealthStatuses.Watch, system.Status);
        var reason = Assert.Single(system.Reasons);
        Assert.Equal("system.failedUnits", reason.Code);
        Assert.Equal("logrotate.service", reason.Params!["units"]);
        Assert.Equal("1", reason.Params["count"]);
    }

    [Theory]
    [InlineData(4, 0, "memory.hardwareCorrupted")]
    [InlineData(0, 2, "memory.edacErrors")]
    public void Linux_MemoryErrors_Warn(long corruptedKb, long edac, string code)
    {
        var memory = Component(new HostHealthSnapshot(true, Array.Empty<string>(), corruptedKb, edac), "memory");

        Assert.Equal(HealthStatuses.Act, memory.Status);
        Assert.Equal(code, Assert.Single(memory.Reasons).Code);
    }

    [Fact]
    public void Mac_BothTilesAreUnknownWithReasons()
    {
        var system = Component(HostHealthSnapshot.Unsupported, "system");
        var memory = Component(HostHealthSnapshot.Unsupported, "memory");

        Assert.Equal(HealthStatuses.Unknown, system.Status);
        Assert.Equal("system.noHealthSource", Assert.Single(system.Reasons).Code);
        Assert.Equal(HealthStatuses.Unknown, memory.Status);
        Assert.Equal("memory.noHealthSource", Assert.Single(memory.Reasons).Code);
    }

    [Fact]
    public void Windows_IgnoresTheHostSnapshot()
    {
        var host = new HostHealthSnapshot(true, new[] { "x.service" }, 9, 9);
        var memory = Component(host, "memory", windows: true);
        var system = Component(host, "system", windows: true);

        Assert.Equal(HealthStatuses.Ok, memory.Status);
        Assert.Equal(HealthStatuses.Ok, system.Status);
    }
}
