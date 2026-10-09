using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests;

// ExperimentalUnlocked is process-global; a serial collection keeps the locked state from leaking into parallel tests.
[CollectionDefinition("ExperimentalUnlock", DisableParallelization = true)]
public class ExperimentalUnlockCollection { }

[Collection("ExperimentalUnlock")]
public class ExperimentalBetaLockTests : IDisposable
{
    public ExperimentalBetaLockTests() => DeviceControlPolicy.ExperimentalUnlocked = false;

    public void Dispose() => DeviceControlPolicy.ExperimentalUnlocked = true;

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli2")]
    [InlineData("corsair")]
    [InlineData("asus-ryujin-lcd")]
    public void RequiresBeta_ExperimentalHandlerOnStableBuild_IsTrue(string handlerId)
    {
        Assert.True(DeviceControlPolicy.RequiresBeta(handlerId));
    }

    [Theory]
    [InlineData("cnvs")]
    [InlineData("nollie")]
    [InlineData("nzxt-kraken")]
    [InlineData("smbus-dram")]
    public void RequiresBeta_FirstPartyOrStableHandler_IsFalse(string handlerId)
    {
        Assert.False(DeviceControlPolicy.RequiresBeta(handlerId));
    }

    [Fact]
    public void RequiresBeta_Unlocked_IsFalse()
    {
        DeviceControlPolicy.ExperimentalUnlocked = true;

        Assert.False(DeviceControlPolicy.RequiresBeta("lianli"));
    }

    [Fact]
    public void Gate_LockedHandler_StaysOffWhateverTheStoredChoice()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled = new List<string> { "lianli", "corsair" });
        var gate = new DeviceControlGate(store);

        Assert.False(gate.IsEnabled("lianli"));
        Assert.False(gate.IsChosenOn("corsair"));
        Assert.False(gate.IsEnabled("lianli3"));
    }

    [Fact]
    public void Gate_SetEnabledOnLockedHandler_LeavesSettingsUntouched()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var whitelist = store.Load().Ui.ConflictAutoKillExclusions.ToList();

        gate.SetEnabled("lianli-tl", true);

        Assert.Empty(store.Load().Devices.NexusControlEnabled);
        Assert.Equal(whitelist, store.Load().Ui.ConflictAutoKillExclusions);
        Assert.False(gate.IsEnabled("lianli-tl"));
    }

    [Fact]
    public void Gate_StoredChoiceAppliesOnceUnlocked()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled = new List<string> { "lianli" });
        var gate = new DeviceControlGate(store);

        DeviceControlPolicy.ExperimentalUnlocked = true;

        Assert.True(gate.IsEnabled("lianli"));
    }

    [Fact]
    public void ConflictMappings_SkipLockedHandlers()
    {
        Assert.Null(DeviceControlPolicy.ConflictAppFor("lianli"));
        Assert.Null(DeviceControlPolicy.ConflictAppFor("corsair"));
        Assert.Empty(DeviceControlPolicy.HandlersFor("lian-li-l-connect"));
        Assert.Empty(DeviceControlPolicy.HandlersFor("icue"));
        Assert.DoesNotContain(DeviceControlPolicy.HandlersWithConflictApp(), DeviceControlPolicy.IsExperimental);
        Assert.Equal("nzxt-cam", DeviceControlPolicy.ConflictAppFor("nzxt-kraken"));
        Assert.Equal(new[] { "nzxt-kraken" }, DeviceControlPolicy.HandlersFor("nzxt-cam"));
    }

    [Fact]
    public void GetAll_LockedHandler_ReportsRequiresBetaOffWithNoConflictApp()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.NexusControlEnabled = new List<string> { "lianli-tl" });
        var manager = new DeviceManager(
            new IDeviceHandler[] { new StubHandler("lianli-tl"), TestHandlers.Cnvs() },
            new StubUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(store));

        var items = manager.GetAll();

        var locked = items.Single(i => i.Id == "lianli-tl");
        Assert.True(locked.RequiresBeta);
        Assert.True(locked.Experimental);
        Assert.False(locked.NexusControlEnabled);
        Assert.Null(locked.ConflictAppId);
        Assert.False(items.Single(i => i.Id == "cnvs").RequiresBeta);
    }

    [Fact]
    public void ReconcileBetaLock_StableBuild_WhitelistsAppsWhoseDevicesAreAllLockedButChosenOn()
    {
        var doc = Doc(enabled: new[] { "lianli", "corsair", "nzxt-kraken" }, whitelist: new string[0]);

        Assert.True(ConflictWhitelistMigration.ReconcileBetaLock(doc));

        Assert.Equal(new[] { "lian-li-l-connect", "icue" }, doc.Ui.ConflictAutoKillExclusions);
        Assert.Equal(new[] { "lian-li-l-connect", "icue" }, doc.Devices.BetaLockWhitelisted);
        Assert.False(ConflictWhitelistMigration.ReconcileBetaLock(doc));
    }

    [Fact]
    public void ReconcileBetaLock_StableBuild_LeavesAppsAloneWhenNoLockedDeviceWasChosenOn()
    {
        var doc = Doc(enabled: new string[0], whitelist: new string[0]);

        Assert.False(ConflictWhitelistMigration.ReconcileBetaLock(doc));
        Assert.Empty(doc.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void ReconcileBetaLock_StableBuild_RespectsAUserWhoTookTheAppBackOff()
    {
        var doc = Doc(enabled: new[] { "lianli" }, whitelist: new string[0]);
        doc.Devices.BetaLockWhitelisted = new List<string> { "lian-li-l-connect" };

        Assert.False(ConflictWhitelistMigration.ReconcileBetaLock(doc));
        Assert.Empty(doc.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void ReconcileBetaLock_UnlockedBuild_TakesBackOnlyWhatTheLockAdded()
    {
        var doc = Doc(enabled: new[] { "lianli" }, whitelist: new[] { "lian-li-l-connect", "icue", "msi-companion" });
        doc.Devices.BetaLockWhitelisted = new List<string> { "lian-li-l-connect", "icue" };
        DeviceControlPolicy.ExperimentalUnlocked = true;

        Assert.True(ConflictWhitelistMigration.ReconcileBetaLock(doc));

        // iCUE stays: none of its devices is chosen on, the state SetEnabled(off) leaves.
        Assert.Equal(new[] { "icue", "msi-companion" }, doc.Ui.ConflictAutoKillExclusions);
        Assert.Empty(doc.Devices.BetaLockWhitelisted);
        Assert.False(ConflictWhitelistMigration.ReconcileBetaLock(doc));
    }

    [Fact]
    public void ReconcileBetaLock_StableBuild_TakesBackAnAppWhoseDevicesAreNoLongerAllLocked()
    {
        // nzxt-kraken is stable, standing in for a family promoted after the lock marked its app.
        var doc = Doc(enabled: new[] { "nzxt-kraken" }, whitelist: new[] { "nzxt-cam" });
        doc.Devices.BetaLockWhitelisted = new List<string> { "nzxt-cam" };

        Assert.True(ConflictWhitelistMigration.ReconcileBetaLock(doc));

        Assert.Empty(doc.Ui.ConflictAutoKillExclusions);
        Assert.Empty(doc.Devices.BetaLockWhitelisted);
    }

    private static NexusSettings Doc(string[] enabled, string[] whitelist)
    {
        var doc = new NexusSettings();
        doc.Devices.NexusControlEnabled = enabled.ToList();
        doc.Ui.ConflictAutoKillExclusions = whitelist.ToList();
        return doc;
    }

    private sealed class StubHandler : IDeviceHandler
    {
        public StubHandler(string id) => Id = id;
        public string Id { get; }
        public string Name => Id;
        public string Category => "fan";
        public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
        public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => true;
        public string GetFirmwareVersion() => "";
    }
}
