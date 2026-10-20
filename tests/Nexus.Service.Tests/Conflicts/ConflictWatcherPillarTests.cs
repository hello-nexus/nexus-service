using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictWatcherPillarTests
{
    // These tests use fake pids, so the signer-pinned entries need a gate that vouches for them.
    public static readonly ConflictSignerGate TrustAll = new(_ => "x.exe", _ => new("Micro-Star Corsair Mercier NZXT Razer Elgato", null));

    private static Dictionary<string, List<(int Pid, string? Path)>> Running(params string[] names) =>
        names.ToDictionary(n => n, n => new List<(int, string?)> { (100, null) }, System.StringComparer.OrdinalIgnoreCase);

    private static (ConflictWatcher Watcher, InMemoryConfigStore Store) Build(
        Dictionary<string, List<(int Pid, string? Path)>> processes, System.Func<string, bool>? displayInUse = null)
    {
        var store = new InMemoryConfigStore();
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), displayInUse, () => processes, null, TrustAll) { HandlerCacheMs = 0 };
        return (watcher, store);
    }

    [Fact]
    public void ALightingOnlyApp_IsHiddenWhileLightingIsOff_AndReturnsLiveWithoutARescan()
    {
        var (watcher, store) = Build(Running("SignalRgb"));
        store.Update(s => s.Features.Lighting = false);

        Assert.Empty(watcher.GetConflicts());

        store.Update(s => s.Features.Lighting = true);

        Assert.Equal(new[] { "signalrgb" }, watcher.GetConflicts().Select(c => c.Id));
    }

    [Fact]
    public void AnUndecidedInstall_HidesLightingAndCoolingApps_UntilTheFeatureStepCompletes()
    {
        var (watcher, store) = Build(Running("SignalRgb", "FanControl"));
        store.Update(s => s.FeaturesOnboardingCompleted = false);
        Assert.Empty(watcher.GetConflicts());

        store.Update(s => s.FeaturesOnboardingCompleted = true);
        watcher.PillarsChanged();

        Assert.Equal(2, watcher.GetConflicts().Count);
    }

    [Fact]
    public void IsAppRunning_IgnoresThePillarFilter()
    {
        var (watcher, store) = Build(Running("SignalRgb"));
        store.Update(s => s.Features.Lighting = false);

        Assert.Empty(watcher.GetConflicts());
        Assert.True(watcher.IsAppRunning("signalrgb"));
    }

    [Fact]
    public void ADisplayApp_CompetesWithBothPillarsOff_OnlyWhileNexusDrivesItsDisplay()
    {
        var driving = true;
        var (watcher, store) = Build(Running("NZXT CAM"), h => driving && h == "nzxt-kraken");
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
        });

        Assert.Single(watcher.GetConflicts());

        driving = false;

        Assert.Empty(watcher.GetConflicts());
    }
}

public class ConflictHandlerFilterTests
{
    [Fact]
    public void IcueWithLightingOff_IsListedOnlyWhileACorsairLinkHandlerIsInUse()
    {
        var inUse = false;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["iCUE"] = new() { (100, null) },
        };
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, h => inUse && h == "corsair", ConflictWatcherPillarTests.TrustAll)
        {
            HandlerCacheMs = 0,
        };

        Assert.Empty(watcher.GetConflicts());

        inUse = true;

        Assert.Single(watcher.GetConflicts());
    }
}

public class ConflictDisplayLaunchTests
{
    private static Dictionary<string, List<(int Pid, string? Path)>> Cam() =>
        new(System.StringComparer.OrdinalIgnoreCase) { ["NZXT CAM"] = new() { (100, null) } };

    [Fact]
    public void ADisplayStartingToBeDriven_RevealsTheApp()
    {
        var driving = false;
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
        });
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), h => driving && h == "nzxt-kraken", Cam, null, ConflictWatcherPillarTests.TrustAll) { HandlerCacheMs = 0 };
        Assert.Empty(watcher.GetConflicts());

        driving = true;

        Assert.Single(watcher.GetConflicts());
        Assert.Single(watcher.RunningIds);
    }

    [Fact]
    public void ADisplayComingIntoUse_IsNotToastedAsALaunch()
    {
        var driving = false;
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Features.Cooling = false;
            s.Ui.ConflictAutoKillExclusions.Clear();
        });
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), h => driving && h == "nzxt-kraken", Cam, null, ConflictWatcherPillarTests.TrustAll) { HandlerCacheMs = 0 };
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        driving = true;
        notifier.Tick(6_000);

        Assert.Empty(launched);
    }

    [Fact]
    public void ARealLaunch_IsStillToasted()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions.Clear());
        var processes = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase);
        var watcher = new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, null, ConflictWatcherPillarTests.TrustAll) { HandlerCacheMs = 0 };
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        processes["SignalRgb"] = new() { (100, null) };
        watcher.PillarsChanged();
        notifier.Tick(6_000);

        Assert.Equal(new[] { "signalrgb" }, launched);
    }
}
