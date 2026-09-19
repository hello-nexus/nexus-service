using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.StreamDeck;

internal sealed class FakeRecentAppsProcessActions : IProcessActionsProvider
{
    public bool ActivateResult;
    public int? LastActivatedPid;
    public bool IsAvailable => true;
    public Task<(int Killed, int Failed)> KillAsync(string processName) => Task.FromResult((0, 0));
    public Task<bool> OpenLocationAsync(string exePath) => Task.FromResult(false);
    public Task<bool> ActivateWindowAsync(int pid)
    {
        LastActivatedPid = pid;
        return Task.FromResult(ActivateResult);
    }
}

internal sealed class FakeRecentAppsWindowSet : IWindowSetProvider
{
    public readonly HashSet<int> WindowedPids = new();
    public bool IsWindowed(int pid) => WindowedPids.Contains(pid);
}

/// <summary>
/// Covers the physical worker's Recent Apps mode: rendering the current
/// ring/focused view (Mini = 3x2 = 6 keys, matching tests/Deck/recentAppsView.vectors.json's
/// grid), page navigation, and press activation via RecentAppsActivator
/// (never through IDeckActionExecutor - that queue must stay empty).
/// </summary>
public sealed class RecentAppsWorkerTests : IDisposable
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly FakeSensorProvider _sensors = new();
    private readonly SimulatedStreamDeckSurface _simulated;
    private readonly RecentAppsState _state = new();
    private readonly FakeRecentAppsProcessActions _processActions = new();
    private readonly FakeRecentAppsWindowSet _windowSet = new();
    private readonly FakeShortcutsProvider _shortcuts = new();
    private readonly RecentAppsActivator _activator;
    private readonly StreamDeckConnectionWorker _worker;

    public RecentAppsWorkerTests()
    {
        _simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var presence = new HardwarePresence(new FixedUsbEnumerator());
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);

        var system = new Nexus.Service.Actions.SystemActions(
            new FakeInputterProvider(), new FakeClipboardProvider(), new FakeSystemPowerProvider(),
            new FakeAudioDeviceProvider(), new FakeVolumeProvider(), _shortcuts,
            new ServiceCollection().BuildServiceProvider());
        _activator = new RecentAppsActivator(_processActions, _shortcuts, system, _windowSet);

        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), presence, gate, _store, _executor, NewTestKeyRenderer(), new MultiplexHub(), _sensors,
            _simulated, recentAppsState: _state, recentAppsActivator: _activator);
    }

    public void Dispose() => _worker.Dispose();

    private void ConnectInRecentAppsMode()
    {
        _store.Update(s => s.StreamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId("sim-0001")] = new DeckInstance { Mode = "recentApps" });
        _worker.Tick();
    }

    /// <summary>Seeds the live ring the worker actually reads (RecentAppsState), in the given order - RecentApps is no longer mirrored to _store synchronously, only on RecentAppsService's own coalesced persist.</summary>
    private void SeedRing(params (string Key, string Name)[] apps)
    {
        _state.Seed(apps.Select(a => new RecentApp { ProcessKey = a.Key, Name = a.Name }), Array.Empty<string>());
    }

    private void SeedRing(params RecentApp[] apps) => _state.Seed(apps, Array.Empty<string>());

    [Fact]
    public void Connect_RendersRingEntriesThenBlanksTheRest()
    {
        SeedRing(("chrome", "Chrome"), ("discord", "Discord"));

        ConnectInRecentAppsMode();

        Assert.NotNull(_simulated.PeekKeyImage(0));
        Assert.NotNull(_simulated.PeekKeyImage(1));
        for (var i = 2; i < Mini.KeyCount; i++)
        {
            Assert.Null(_simulated.PeekKeyImage(i));
        }
    }

    [Fact]
    public void RefreshWithNoRingChange_RepaintsNoKeys()
    {
        SeedRing(("chrome", "Chrome"), ("discord", "Discord"));
        ConnectInRecentAppsMode();
        var callsAfterConnect = _simulated.SetKeyImageCallCount;

        _worker.RefreshView("sim-0001");

        Assert.Equal(callsAfterConnect, _simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void RefreshAfterFocusChange_RepaintsOnlyTheChangedKeys()
    {
        SeedRing(("chrome", "Chrome"), ("discord", "Discord"));
        ConnectInRecentAppsMode();
        var callsAfterConnect = _simulated.SetKeyImageCallCount;

        // Focusing the already-front-of-ring app changes only that one key's
        // rendered content (the selected treatment); the rest of the layout
        // is untouched.
        _state.SetFocused("chrome");
        _worker.RefreshView("sim-0001");

        Assert.Equal(callsAfterConnect + 1, _simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void FocusedApp_RendersDifferentlyFromUnfocusedKeys()
    {
        SeedRing(("chrome", "Chrome"), ("discord", "Discord"));
        _state.SetFocused("discord");

        ConnectInRecentAppsMode();

        // Focused key (discord, ordered first) renders with the selected
        // treatment - different bytes than an identical unselected key would.
        var focusedKeyBytes = _simulated.PeekKeyImage(0);
        Assert.NotNull(focusedKeyBytes);
        var unselectedRender = NewTestKeyRenderer().Render(
            new Nexus.Service.Deck.DeckSlot { Label = "Discord", Action = new Nexus.Service.Deck.DeckAction { Type = "openFile" } },
            isToggleOn: false, Mini, orientation: 0, selected: false);
        Assert.NotEqual(unselectedRender, focusedKeyBytes);
    }

    [Fact]
    public void OverflowingRing_PagesWithNavKeysAndClamps()
    {
        // 8 entries on a 6-key deck -> 2 pages (5 apps + next, then prev + 3 apps + blanks).
        SeedRing(("p1", "A1"), ("p2", "A2"), ("p3", "A3"), ("p4", "A4"), ("p5", "A5"), ("p6", "A6"), ("p7", "A7"), ("p8", "A8"));

        ConnectInRecentAppsMode();
        Assert.Equal(0, _worker.GetCurrentPage("sim-0001"));

        // Press the last key (index 5) - the auto "next" key on page 0.
        _simulated.Poke(5, true);
        _simulated.Poke(5, false);
        _worker.Tick();

        Assert.Equal(1, _worker.GetCurrentPage("sim-0001"));
        Assert.Empty(_executor.Calls); // nav never reaches the persisted-action executor

        // Press key 0 (the auto "prev" key on page 1) to go back.
        _simulated.Poke(0, true);
        _simulated.Poke(0, false);
        _worker.Tick();

        Assert.Equal(0, _worker.GetCurrentPage("sim-0001"));
    }

    [Fact]
    public async Task Press_AppWithLiveWindow_ActivatesInsteadOfLaunching()
    {
        SeedRing(new RecentApp { ProcessKey = "chrome", Name = "Chrome", Pid = 4242 });
        _windowSet.WindowedPids.Add(4242);
        _processActions.ActivateResult = true;

        ConnectInRecentAppsMode();

        _simulated.Poke(0, true);
        _simulated.Poke(0, false);
        _worker.Tick();
        Assert.NotNull(_worker.LastDispatchTask);
        await _worker.LastDispatchTask!;

        Assert.Equal(4242, _processActions.LastActivatedPid);
        Assert.Null(_shortcuts.Launched);
        Assert.Empty(_executor.Calls);
    }

    [Fact]
    public async Task Press_AppWithNoLiveWindow_LaunchesByShortcutId()
    {
        SeedRing(new RecentApp { ProcessKey = "figma", Name = "Figma", ShortcutId = "shortcut-figma" });

        ConnectInRecentAppsMode();

        _simulated.Poke(0, true);
        _simulated.Poke(0, false);
        _worker.Tick();
        Assert.NotNull(_worker.LastDispatchTask);
        await _worker.LastDispatchTask!;

        Assert.Equal("shortcut-figma", _shortcuts.Launched);
        Assert.Null(_processActions.LastActivatedPid);
    }

    [Fact]
    public async Task Press_FocusedKey_IsANoOp()
    {
        SeedRing(new RecentApp { ProcessKey = "chrome", Name = "Chrome", ShortcutId = "shortcut-chrome" });
        _state.SetFocused("chrome");

        ConnectInRecentAppsMode();

        _simulated.Poke(0, true);
        _simulated.Poke(0, false);
        _worker.Tick();

        Assert.Null(_worker.LastDispatchTask);
        Assert.Null(_shortcuts.Launched);
    }

    [Fact]
    public void Press_AppKey_ShowsThePressedInsetThenRestoresOnRelease()
    {
        SeedRing(("chrome", "Chrome"), ("discord", "Discord"));
        ConnectInRecentAppsMode();
        var unpressed = _simulated.PeekKeyImage(0);
        Assert.NotNull(unpressed);

        _simulated.Poke(0, true);
        _worker.Tick();
        Assert.NotEqual(unpressed, _simulated.PeekKeyImage(0));

        _simulated.Poke(0, false);
        _worker.Tick();
        Assert.Equal(unpressed, _simulated.PeekKeyImage(0));
    }

    [Fact]
    public void Press_FocusedKey_StillShowsThePressedInsetDespiteBeingANoOpActivation()
    {
        SeedRing(new RecentApp { ProcessKey = "chrome", Name = "Chrome", ShortcutId = "shortcut-chrome" });
        _state.SetFocused("chrome");
        ConnectInRecentAppsMode();
        var unpressed = _simulated.PeekKeyImage(0);

        _simulated.Poke(0, true);
        _worker.Tick();

        Assert.NotEqual(unpressed, _simulated.PeekKeyImage(0));
        Assert.Null(_worker.LastDispatchTask);
    }
}
