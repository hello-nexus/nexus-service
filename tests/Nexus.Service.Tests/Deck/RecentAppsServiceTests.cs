using System;
using System.Collections.Generic;
using System.Threading;
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

        Assert.Contains(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void FocusChange_MultipleApps_MostRecentFirst()
    {
        Focus("chrome");
        Focus("discord");

        var ring = Store.Load().StreamDeck.RecentApps;
        Assert.Equal("discord", ring[0].ProcessKey);
        Assert.Contains(ring, a => a.ProcessKey == "chrome");
    }

    [Fact]
    public void FocusChange_ExcludedApp_NeverEntersRing()
    {
        Store.Update(s => s.StreamDeck.RecentAppsExcluded.Add("obs"));

        Focus("obs");

        Assert.DoesNotContain(Store.Load().StreamDeck.RecentApps, a => a.ProcessKey == "obs");
    }

    [Fact]
    public void FocusChange_ResolvesShortcutIdFromInstalledApps()
    {
        _shortcuts.All.Add(new Shortcut { Id = "shortcut-chrome", Name = "Chrome", ProcessName = "chrome" });

        Focus("chrome");

        var entry = Store.Load().StreamDeck.RecentApps.Find(a => a.ProcessKey == "chrome");
        Assert.Equal("shortcut-chrome", entry?.ShortcutId);
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
}
