using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>Drives DeckAppPresetSwitcher.Tick against the real host, mirroring
/// AppPresetSwitcherTests - the bindings gate, the activate hand-off, the
/// editor pause and its settle pass are exercised, not just the (already
/// covered) shared AppPresetFocusTracker decision table.</summary>
public sealed class DeckAppPresetSwitcherTests : IDisposable
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public string Focused = "";
        public event Action? FocusChanged;
        public void Focus(string app) { Focused = app; FocusChanged?.Invoke(); }
        public FocusSession? GetCurrentSession() => Focused.Length == 0 ? null : new FocusSession { Id = "1", Name = Focused };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private readonly StubDeviceHostFactory _factory;
    private readonly FakeScreenTime _screenTime = new();
    private DeckAppPresetSwitcher? _switcher;

    public DeckAppPresetSwitcherTests()
    {
        // Own host per test, like AppPresetSwitcherTests - the switcher runs a
        // live dwell timer against the shared store.
        _factory = new StubDeviceHostFactory();
        _ = _factory.CreateClient();
    }

    public void Dispose()
    {
        _switcher?.Dispose();
        _factory.Dispose();
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();
    private MultiplexHub Hub => _factory.Services.GetRequiredService<MultiplexHub>();

    private DeckAppPresetSwitcher BuildSwitcher()
    {
        var sp = _factory.Services;
        return new DeckAppPresetSwitcher(
            sp.GetRequiredService<IConfigStore>(),
            _screenTime,
            sp.GetRequiredService<MultiplexHub>(),
            sp.GetRequiredService<DeckPresetActivator>());
    }

    private (string bound, string other) SeedPresets(bool withBinding, string instanceId = "widget:w1")
    {
        var bound = Guid.NewGuid().ToString("n");
        var other = Guid.NewGuid().ToString("n");
        Store.Update(s =>
        {
            s.StreamDeck.Presets.Clear();
            s.StreamDeck.Presets.Add(new DeckPreset
            {
                Id = bound,
                Name = "Discord",
                Cols = 2,
                Rows = 2,
                Apps = withBinding
                    ? new List<PresetAppBinding> { new() { Id = "proc:chrome", Name = "chrome", ProcessName = "chrome" } }
                    : null,
            });
            s.StreamDeck.Presets.Add(new DeckPreset { Id = other, Name = "Desk", Cols = 2, Rows = 2 });
            s.StreamDeck.Instances[instanceId] = new DeckInstance { Mode = "appAware", ActivePresetId = other };
        });
        return (bound, other);
    }

    private void Focus(string app)
    {
        if (_switcher is null)
        {
            _switcher = BuildSwitcher();
            _switcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        _screenTime.Focus(app);
        // The dwell timer re-evaluates once the candidate has settled.
        Thread.Sleep(Nexus.Service.Lighting.AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void Focusing_a_bound_app_activates_its_deck_preset()
    {
        var (bound, _) = SeedPresets(withBinding: true);

        Focus("chrome");

        Assert.Equal(bound, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }

    [Fact]
    public void Focusing_an_unbound_app_after_a_bound_one_restores_the_previous_preset()
    {
        var (_, other) = SeedPresets(withBinding: true);

        Focus("chrome");
        Focus("notepad");

        Assert.Equal(other, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }

    [Fact]
    public void No_bindings_anywhere_never_activates()
    {
        var (_, other) = SeedPresets(withBinding: false);

        Focus("chrome");

        Assert.Equal(other, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }

    [Fact]
    public void CustomModeInstance_IsNeverTouched()
    {
        var (bound, other) = SeedPresets(withBinding: true);
        Store.Update(s => s.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "custom", ActivePresetId = other });

        Focus("chrome");

        Assert.Equal(other, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
        Assert.NotEqual(bound, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }

    [Fact]
    public void Paused_while_streamdeckTiles_has_a_subscriber_then_settles_on_close()
    {
        var (bound, _) = SeedPresets(withBinding: true);
        using var editorOpen = Hub.AddTestSubscription(PanelTopics.StreamDeckTiles);

        Focus("chrome");
        // Paused: the dwell fired, but the topic still had a subscriber when
        // Tick ran, so nothing should have activated yet.
        Assert.NotEqual(bound, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);

        editorOpen.Dispose();
        // The settle pass on last-unsubscribe runs synchronously off the hub
        // event; give its dwell timer a moment.
        Thread.Sleep(Nexus.Service.Lighting.AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(400));

        Assert.Equal(bound, Store.Load().StreamDeck.Instances["widget:w1"].ActivePresetId);
    }
}
