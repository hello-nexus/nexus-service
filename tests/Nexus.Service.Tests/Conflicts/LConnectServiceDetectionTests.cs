using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class LConnectServiceDetectionTests
{
    [Theory]
    [InlineData("L-Connect-Service")]
    [InlineData("L-Connect-Service-Watcher")]
    public void ALoneServiceProcessCountsAsLConnectRunning(string processName)
    {
        var index = new Dictionary<string, List<(int Pid, string? Path)>>(StringComparer.OrdinalIgnoreCase)
        {
            [processName] = new() { (300, null) },
        };
        var watcher = new ConflictWatcher(new MultiplexHub(), null, null, null, () => index, null);
        var conflict = Assert.Single(watcher.GetConflicts());
        Assert.Equal("lian-li-l-connect", conflict.Id);
        Assert.Equal(processName, conflict.ProcessName);
    }

    [Fact]
    public void LConnectStaysWhitelistedByDefault()
    {
        var def = ConflictWatcher.FindById("lian-li-l-connect");
        Assert.NotNull(def);
        Assert.True(def!.DefaultWhitelisted);
    }

    [Theory]
    [InlineData("lianli-hydroshift2", true, "Lian Li L-Connect")]
    [InlineData("lianli-hydroshift2", false, null)]
    [InlineData("not-a-handler", true, null)]
    public void DeniedOpenNamesTheRunningHolder(string handlerId, bool running, string? expected)
    {
        Assert.Equal(expected, BulkPanelConnectionWorker.RunningHolderName(handlerId, _ => running));
    }

    [Fact]
    public void NoWatcherMeansNoHolderName()
    {
        Assert.Null(BulkPanelConnectionWorker.RunningHolderName("lianli-hydroshift2", null));
    }
}
