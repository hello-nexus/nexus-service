using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// Drives RecentAppsService against the real host (like AppPresetSwitcherTests
/// drives AppPresetSwitcher) so the FocusChanged wiring, the coalesced
/// broadcast, and the shortcut-id resolution are exercised end to end.
/// </summary>
public sealed class RecentAppsServiceTests : IDisposable
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public string Focused = "";
        public event Action? FocusChanged;
        public void Focus(string app) { Focused = app; FocusChanged?.Invoke(); }
        public FocusSession? GetCurrentSession() => Focused.Length == 0 ? null : new FocusSession { Id = "1", Name = Focused };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private sealed class FakeShortcuts : IShortcutsProvider
    {
        public readonly List<Shortcut> All = new();
        public IReadOnlyList<Shortcut> GetAll() => All;
        public Shortcut? GetById(string targetId) => All.Find(s => s.Id == targetId);
        public byte[] GetIcon(string targetId) => Array.Empty<byte>();
        public bool Launch(string targetId) => true;
        public string ResolveProcessName(string targetId) => GetById(targetId)?.ProcessName ?? "";
    }

    /// <summary>Records every GetAll() call so a test can assert it was never reached from the synchronous focus-handling path (see RecentAppsService's own class doc comment for why that would deadlock a Windows helper connection).</summary>
    private sealed class RecordingShortcuts : IShortcutsProvider
    {
        public int GetAllCallCount;
        public IReadOnlyList<Shortcut> GetAll() { GetAllCallCount++; return Array.Empty<Shortcut>(); }
        public Shortcut? GetById(string targetId) => null;
        public byte[] GetIcon(string targetId) => Array.Empty<byte>();
        public bool Launch(string targetId) => true;
        public string ResolveProcessName(string targetId) => "";
    }

    private readonly StubDeviceHostFactory _factory;
    private readonly FakeScreenTime _screenTime = new();
    private readonly FakeShortcuts _shortcuts = new();
    private RecentAppsService? _service;

    public RecentAppsServiceTests()
    {
        // Own host per test, like AppPresetSwitcherTests - the service runs a
        // live coalesce timer against the shared store.
        _factory = new StubDeviceHostFactory();
        _ = _factory.CreateClient();
    }

    public void Dispose()
    {
        _service?.Dispose();
        _factory.Dispose();
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();
    private RecentAppsState State => _factory.Services.GetRequiredService<RecentAppsState>();

    private RecentAppsService BuildService()
    {
        var sp = _factory.Services;
        return new RecentAppsService(
            sp.GetRequiredService<IConfigStore>(),
            _screenTime,
            _shortcuts,
            sp.GetRequiredService<MultiplexHub>(),
            sp.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>(),
            sp.GetRequiredService<RecentAppsState>());
    }

    private void Focus(string app)
    {
        if (_service is null)
        {
            _service = BuildService();
            _service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        _screenTime.Focus(app);
        // The 100ms coalescer needs to fire before the ring/broadcast assertions.
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void FocusChange_AddsToRing()
    {
        Focus("chrome");

        Assert.Contains(State.RingSnapshot(), a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void FocusChange_MultipleApps_MostRecentFirst()
    {
        Focus("chrome");
        Focus("discord");

        var ring = State.RingSnapshot();
        Assert.Equal("discord", ring[0].ProcessKey);
        Assert.Contains(ring, a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void FocusChange_ExcludedApp_NeverEntersRing()
    {
        Store.Update(s => s.StreamDeck.RecentAppsExcluded.Add("obs"));

        Focus("obs");

        Assert.DoesNotContain(State.RingSnapshot(), a => a.ProcessKey == "obs");
    }

    [Fact]
    public void FocusChange_ResolvesShortcutIdFromInstalledApps()
    {
        _shortcuts.All.Add(new Shortcut { Id = "shortcut-chrome", Name = "Chrome", ProcessName = "chrome" });

        Focus("chrome");

        var entry = State.RingSnapshot().Find(a => a.ProcessKey == "chrome");
        Assert.Equal("shortcut-chrome", entry?.ShortcutId);
    }

    /// <summary>
    /// On Windows, FocusChanged fires synchronously on HelperConnection's own
    /// pipe read loop; a shortcuts lookup made from inside the focus handler
    /// would block waiting for a reply only that same loop can deliver.
    /// Resolution must run off this call entirely (RecentAppsService.Broadcast,
    /// via the coalesce timer), never inline in Tick().
    /// </summary>
    [Fact]
    public async Task FocusChange_NeverCallsTheShortcutsProviderSynchronously()
    {
        var recording = new RecordingShortcuts();
        var service = new RecentAppsService(
            Store,
            _screenTime,
            recording,
            _factory.Services.GetRequiredService<MultiplexHub>(),
            _factory.Services.GetRequiredService<Nexus.Service.Peripherals.StreamDeck.StreamDeckConnectionWorker>(),
            State);
        await service.StartAsync(CancellationToken.None);
        try
        {
            recording.GetAllCallCount = 0;

            _screenTime.Focus("chrome");

            Assert.Equal(0, recording.GetAllCallCount);
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// A store write on every alt+tab would pulse ProfileManager's dirty-mark
    /// and every other IConfigStore.OnChanged subscriber regardless of
    /// whether anything is even showing Recent Apps. The live ring updates
    /// immediately (previous tests); settings.json must not until the
    /// coalesced persist runs.
    /// </summary>
    [Fact]
    public void FocusChange_DoesNotWriteToStoreBeforeThePersistIntervalElapses()
    {
        Focus("chrome");

        Assert.DoesNotContain(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void ForcedPersist_WritesTheLiveRingToStore()
    {
        Focus("chrome");

        _service!.Persist(force: true);

        Assert.Contains(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "chrome");
    }

    /// <summary>
    /// A periodic (unforced) persist must not mark the profile dirty for a
    /// host where nothing is even in Recent Apps mode - only the routes'
    /// forced flush (previous test) is unconditional.
    /// </summary>
    [Fact]
    public void UnforcedPersist_SkipsWritingWhenNoInstanceIsInRecentAppsMode()
    {
        Focus("chrome");

        _service!.Persist();

        Assert.DoesNotContain(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void UnforcedPersist_WritesWhenAnInstanceIsInRecentAppsMode()
    {
        Store.Update(s => s.StreamDeck.Instances["streamdeck:sim-0001"] = new DeckInstance { Mode = "recentApps" });

        Focus("chrome");
        _service!.Persist();

        Assert.Contains(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "chrome");
    }

    /// <summary>A pid from a previous service run can be reused by an unrelated process by the time this run starts, so ExecuteAsync must never seed one across a restart.</summary>
    [Fact]
    public async Task ExecuteAsync_NeverTrustsAPersistedPidAcrossARestart()
    {
        Store.Update(s => s.StreamDeck.RecentApps.Add(new RecentApp { ProcessKey = "stale", Name = "Stale", Pid = 9999 }));

        var service = BuildService();
        await service.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync does not guarantee ExecuteAsync's body
        // has completed by the time it returns; give the seed a moment.
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        try
        {
            var seeded = State.RingSnapshot().Find(a => a.ProcessKey == "stale");
            Assert.NotNull(seeded);
            Assert.Null(seeded!.Pid);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void FocusChange_BroadcastsCoalescedRecentsFrame()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var kinds = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => { if (topic == PanelTopics.Deck) kinds.Add(topic); };
        using var sub = hub.AddTestSubscription(PanelTopics.Deck);

        Focus("chrome");

        Assert.Contains(PanelTopics.Deck, kinds);
    }

    /// <summary>The recents frame's ring must serialize as "apps" (matching RecentAppsResponse and the CONTRACT ADDENDUM), not the C# property's own name.</summary>
    [Fact]
    public void FocusChange_RecentsFrame_SerializesRingUnderTheAppsKey()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        byte[]? payload = null;
        hub.OnBroadcastForTest += (topic, bytes) => { if (topic == PanelTopics.Deck) payload = bytes.ToArray(); };
        using var sub = hub.AddTestSubscription(PanelTopics.Deck);

        Focus("chrome");

        Assert.NotNull(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var frame = doc.RootElement.GetProperty("d");
        Assert.Equal("recents", frame.GetProperty("kind").GetString());
        Assert.True(frame.TryGetProperty("apps", out var apps));
        Assert.False(frame.TryGetProperty("recentApps", out _));
        Assert.Contains(System.Linq.Enumerable.Range(0, apps.GetArrayLength()), i => apps[i].GetProperty("processKey").GetString() == "chrome");
    }
}
