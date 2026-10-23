using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamedPanelCoordinatorTests : IDisposable
{
    private readonly string _configPath = Path.Combine(
        Path.GetTempPath(), "nexus-streamco-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), "nexus-streamst-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _config;
    private readonly PanelDeviceRegistry _registry;
    private readonly DeviceControlGate _gate;
    private readonly StreamedPanelStore _store;
    private readonly FakeDiscovery _discovery = new();
    private long _nowMs = 1_000_000;
    private readonly List<string> _panelChanges = new();
    private StreamedPanelCoordinator? _coordinator;
    private readonly LcdLockState _lock = new();

    public StreamedPanelCoordinatorTests()
    {
        _config = new JsonConfigStore(_configPath);
        _registry = new PanelDeviceRegistry(_config);
        _gate = new DeviceControlGate(_config);
        // "fake-panel" is not on the Hyte/iBUYPOWER default-on list, so these
        // coordinator tests need it explicitly enabled to exercise the
        // publish/attach/detach behavior independent of the gate default.
        _gate.SetEnabled("fake-panel", true);
        _store = new StreamedPanelStore(_storePath);
    }

    public void Dispose()
    {
        // StopAsync closes sessions (joins writer threads); Dispose alone doesn't.
        _coordinator?.StopAsync(default).GetAwaiter().GetResult();
        _coordinator?.Dispose();
        _config.Dispose();
        try { File.Delete(_configPath); } catch { }
        try { File.Delete(_storePath); } catch { }
    }

    private StreamedPanelCoordinator Coordinator()
    {
        _coordinator ??= new StreamedPanelCoordinator(
            new[] { _discovery },
            _store,
            _registry,
            _gate,
            notifyOverlay: null,
            nowMs: () => _nowMs,
            notifyPanelChanged: _panelChanges.Add,
            lockState: _lock);
        _coordinator.FadeRunner.AutoRun = false;
        return _coordinator;
    }

    private static StreamedPanelProfile Profile(string kind = "fake-fs", int fps = 30, bool monitor = false, bool dimmable = false) => new()
    {
        SupportsBrightness = dimmable,
        SupportsSecondaryMonitor = monitor,
        Kind = kind,
        DisplayName = "Fake Panel",
        Surface = "monitor",
        CssWidth = 640,
        CssHeight = 480,
        Fps = fps,
    };

    private static StreamedPanelDeviceInfo Device(string serial = "d211_fake", string kind = "fake-fs", int fps = 30, bool monitor = false, bool dimmable = false)
        => new() { Serial = serial, Profile = Profile(kind, fps, monitor, dimmable) };

    private sealed class FakeTransport : IStreamedPanelTransport
    {
        public bool IsOpen { get; private set; }
        public string Serial { get; }
        public bool FailOpen { get; set; }
        public FakeTransport(string serial) { Serial = serial; }
        public void Open()
        {
            if (FailOpen) throw new IOException("open refused");
            IsOpen = true;
        }
        public void StartPlayer() { }
        public void Write(ReadOnlySpan<byte> annexBAccessUnit) { }
        public void Dispose() { IsOpen = false; }
    }

    private sealed class FakeBrightnessTransport : IStreamedPanelTransport, IBrightnessPanelTransport
    {
        public bool IsOpen { get; private set; }
        public string Serial { get; }
        public int ApplyCalls { get; private set; }
        public int? LastApplied { get; private set; }
        private Func<int?>? _source;
        private readonly object _gate = new();

        // Mirrors the real transports: what was last put on the glass, and what it replaced.
        public int? Applied { get; set; }
        public int? Previous { get; set; }
        public bool FailWrites { get; set; }
        public bool ThrowOnNextApply { get; set; }
        public int? CurrentBrightness => Applied;
        public int? PreviousBrightness => Previous;

        private bool Step()
        {
            var wanted = _source?.Invoke();
            LastApplied = wanted;
            if (wanted is not int percent || percent == Applied)
            {
                return true;
            }
            if (FailWrites)
            {
                return false;
            }
            Previous = Applied;
            Applied = percent;
            return true;
        }

        /// <summary>A frame thread: reads the bound source while holding the transport's own brightness lock.</summary>
        public void FrameThreadRead()
        {
            lock (_gate) { Step(); }
        }

        public FakeBrightnessTransport(string serial) { Serial = serial; }
        public void Open() => IsOpen = true;
        public void StartPlayer() { }
        public void Write(ReadOnlySpan<byte> annexBAccessUnit) { }
        public void Dispose() => IsOpen = false;
        public void BindBrightness(Func<int?> source) => _source = source;
        public bool ApplyBrightness()
        {
            lock (_gate)
            {
                ApplyCalls++;
                if (ThrowOnNextApply)
                {
                    ThrowOnNextApply = false;
                    throw new InvalidOperationException("transport fault");
                }
                return Step();
            }
        }
    }

    private sealed class FakeDiscovery : IStreamedPanelDiscovery
    {
        public string HandlerId => "fake-panel";
        public List<StreamedPanelDeviceInfo> Devices { get; } = new();
        public List<FakeTransport> Transports { get; } = new();
        public List<FakeBrightnessTransport> BrightnessTransports { get; } = new();
        public bool UseBrightnessTransport { get; set; }
        public bool FailOpen { get; set; }
        public bool Withheld { get; set; }
        public bool ListedWhileWithheld { get; set; }
        public string? WithheldSerial => Withheld ? Devices.FirstOrDefault()?.Serial : null;

        public IReadOnlyList<StreamedPanelDeviceInfo> Discover() => Devices.ToList();

        public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info)
        {
            if (UseBrightnessTransport)
            {
                var brightness = new FakeBrightnessTransport(info.Serial);
                BrightnessTransports.Add(brightness);
                return brightness;
            }
            var t = new FakeTransport(info.Serial) { FailOpen = FailOpen };
            Transports.Add(t);
            return t;
        }
    }

    [Fact]
    public void A_new_strip_panel_is_seeded_with_a_page_that_spans_the_strip()
    {
        _discovery.Devices.Add(new StreamedPanelDeviceInfo
        {
            Serial = "strip",
            Profile = new StreamedPanelProfile { Kind = "strip", DisplayName = "Strip", Surface = "monitor", CssWidth = 1920, CssHeight = 480 },
        });
        var coordinator = Coordinator();

        coordinator.TickOnce();

        var layout = _registry.Get(Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId)!.Layout;
        Assert.NotNull(layout);
        Assert.Equal("monitor", layout!.Surface);
        var widgets = Assert.Single(layout.Pages).Widgets;
        Assert.Equal(4, widgets.Count);
        Assert.All(widgets, w => Assert.Equal("4x4", w.Size));
        Assert.Equal(new[] { 0, 4, 8, 12 }, widgets.Select(w => w.Col).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void A_known_strip_panel_with_no_stored_layout_gets_the_strip_seed_at_session_start()
    {
        var caps = new PanelDeviceCapabilities { Surface = "monitor", CssWidth = 1920, CssHeight = 480 };
        var record = _registry.Allocate("Strip", caps);
        _store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal) { ["strip"] = new() { PanelDeviceId = record.Id } });
        _discovery.Devices.Add(new StreamedPanelDeviceInfo
        {
            Serial = "strip",
            Profile = new StreamedPanelProfile { Kind = "strip", DisplayName = "Strip", Surface = "monitor", CssWidth = 1920, CssHeight = 480 },
        });

        Coordinator().TickOnce();

        var widgets = Assert.Single(_registry.Get(record.Id)!.Layout!.Pages).Widgets;
        Assert.Contains(widgets, w => w.Type == "weather" && w.Size == "4x4");
    }

    private static StreamedPanelDeviceInfo KrakenScreen(string serial) => new()
    {
        Serial = serial,
        Profile = new StreamedPanelProfile
        {
            Kind = "kraken-round", DisplayName = "Kraken LCD", Surface = PanelSurfaces.LegacyKraken, Family = "nzxt-kraken",
            CssWidth = 640, CssHeight = 640, Dpi = 240,
        },
    };

    [Fact]
    public void A_kraken_record_keeps_its_look_and_its_legacy_surface_across_an_attach()
    {
        var record = _registry.Allocate("Kraken", new PanelDeviceCapabilities { Surface = PanelSurfaces.LegacyKraken, Family = "nzxt-kraken", CssWidth = 640, CssHeight = 640 });
        _registry.Patch(record.Id, new PanelDevicePatch { AccentColor = "#abc" });
        _store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal) { ["k-serial"] = new() { PanelDeviceId = record.Id } });
        _discovery.Devices.Add(KrakenScreen("k-serial"));

        Coordinator().TickOnce();

        var after = _registry.Get(record.Id)!;
        Assert.Equal(PanelSurfaces.LegacyKraken, after.Capabilities!.Surface);
        Assert.Equal(240, after.Capabilities.Dpi);
        Assert.Equal("#abc", after.AccentColor);
        Assert.Single(_registry.List());
    }

    [Fact]
    public void A_round_cooler_without_a_stored_entry_does_not_take_the_krakens_record()
    {
        var kraken = _registry.Allocate("Kraken", new PanelDeviceCapabilities { Surface = PanelSurfaces.LegacyKraken });
        _discovery.Devices.Add(FanScreen("fan", multiInstance: false));

        var coordinator = Coordinator();
        coordinator.TickOnce();

        var id = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        Assert.NotEqual(kraken.Id, id);
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void The_kraken_without_a_stored_entry_does_not_take_another_round_coolers_record()
    {
        var other = _registry.Allocate("Cooler", new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdRound });
        _discovery.Devices.Add(KrakenScreen("k"));

        var coordinator = Coordinator();
        coordinator.TickOnce();

        var id = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        Assert.NotEqual(other.Id, id);
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void The_kraken_without_a_stored_entry_reuses_its_own_record_even_when_that_has_no_family()
    {
        _config.Update(st =>
        {
            st.PanelDevices["cooler"] = new PanelDeviceRecord { Id = "cooler", LastSeenAt = 2, Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdRound } };
            st.PanelDevices["legacy"] = new PanelDeviceRecord { Id = "legacy", LastSeenAt = 1, Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LegacyKraken } };
        });
        _discovery.Devices.Add(KrakenScreen("k"));

        var coordinator = Coordinator();
        coordinator.TickOnce();

        Assert.Equal("legacy", Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId);
    }

    [Fact]
    public void A_new_panel_of_ordinary_shape_keeps_the_client_seed()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();

        coordinator.TickOnce();

        Assert.Null(_registry.Get(Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId)!.Layout);
    }

    private static StreamedPanelDeviceInfo FanScreen(string serial, bool multiInstance = true) => new()
    {
        Serial = serial,
        Profile = new StreamedPanelProfile
        {
            Kind = "fan-lcd", DisplayName = serial, Surface = PanelSurfaces.LcdRound, CssWidth = 400, CssHeight = 400,
            MultiInstance = multiInstance,
        },
    };

    [Fact]
    public void Screens_of_one_multi_screen_device_each_get_their_own_record()
    {
        _discovery.Devices.AddRange(new[] { FanScreen("a"), FanScreen("b"), FanScreen("c") });
        var coordinator = Coordinator();

        coordinator.TickOnce();

        var ids = coordinator.GetAssignments().Assignments.Select(a => a.PanelDeviceId).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void Screens_left_sharing_one_stored_record_are_split_with_the_first_keeping_it()
    {
        var shared = _registry.Allocate("shared", new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdRound });
        _store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
        {
            ["a"] = new() { PanelDeviceId = shared.Id },
            ["b"] = new() { PanelDeviceId = shared.Id },
        });
        _discovery.Devices.AddRange(new[] { FanScreen("a"), FanScreen("b") });
        var coordinator = Coordinator();

        coordinator.TickOnce();

        var records = _store.Load();
        Assert.Equal(shared.Id, records["a"].PanelDeviceId);
        Assert.NotEqual(shared.Id, records["b"].PanelDeviceId);
    }

    [Fact]
    public void Another_cooler_on_the_same_surface_never_takes_a_multi_screen_record()
    {
        _discovery.Devices.Add(FanScreen("a"));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var screen = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;

        _discovery.Devices.Add(FanScreen("cooler", multiInstance: false));
        coordinator.TickOnce();

        Assert.NotEqual(screen, _store.Load()["cooler"].PanelDeviceId);
        Assert.True(_store.Load()["a"].MultiInstance);
    }

    [Fact]
    public void Single_screen_cooler_lcds_still_share_the_surface_record()
    {
        _discovery.Devices.AddRange(new[] { FanScreen("a", multiInstance: false), FanScreen("b", multiInstance: false) });
        var coordinator = Coordinator();

        coordinator.TickOnce();

        Assert.Single(coordinator.GetAssignments().Assignments.Select(a => a.PanelDeviceId).Distinct());
    }

    [Fact]
    public void Attach_mints_session_and_publishes_assignment()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();

        coordinator.TickOnce();

        var assignment = Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.NotEmpty(assignment.SessionId);
        Assert.Equal(640, assignment.CssWidth);
        Assert.Equal(480, assignment.CssHeight);
        Assert.Equal(30, assignment.Fps);
        Assert.NotNull(_registry.Get(assignment.PanelDeviceId));
        Assert.Equal(assignment.PanelDeviceId, _store.Load()["d211_fake"].PanelDeviceId);
    }

    [Fact]
    public void Reattach_reuses_panel_record_with_fresh_session_id()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var first = Assert.Single(coordinator.GetAssignments().Assignments);

        _discovery.Devices.Clear();
        _nowMs += 61_000;
        coordinator.TickOnce();
        Assert.Empty(coordinator.GetAssignments().Assignments);

        _discovery.Devices.Add(Device());
        coordinator.TickOnce();
        var second = Assert.Single(coordinator.GetAssignments().Assignments);

        Assert.Equal(first.PanelDeviceId, second.PanelDeviceId);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public void Detach_lingers_before_unpublishing()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();

        _discovery.Devices.Clear();
        _nowMs += 10_000;
        coordinator.TickOnce();
        Assert.Single(coordinator.GetAssignments().Assignments);

        _nowMs += 55_000;
        coordinator.TickOnce();
        Assert.Empty(coordinator.GetAssignments().Assignments);
    }

    [Fact]
    public void A_withheld_screen_closes_immediately_and_returns_when_released()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        Assert.Single(coordinator.GetAssignments().Assignments);

        _discovery.Withheld = true;
        coordinator.TickOnce();
        Assert.Empty(coordinator.GetAssignments().Assignments);

        _discovery.Withheld = false;
        coordinator.TickOnce();
        Assert.Single(coordinator.GetAssignments().Assignments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_withheld_panel_stays_listed_only_when_its_discovery_asks(bool listed)
    {
        _discovery.Devices.Add(Device());
        _discovery.ListedWhileWithheld = listed;
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.LivePanelDeviceIds());

        _discovery.Withheld = true;
        coordinator.TickOnce();
        Assert.Empty(coordinator.GetAssignments().Assignments);
        Assert.Equal(listed, coordinator.LivePanelDeviceIds().Contains(panelId));

        _discovery.Withheld = false;
        _discovery.Devices.Clear();
        coordinator.TickOnce();
        Assert.Empty(coordinator.LivePanelDeviceIds());
    }

    [Fact]
    public void A_panel_withheld_before_any_session_after_a_restart_is_still_listed()
    {
        _discovery.Devices.Add(Device());
        _discovery.ListedWhileWithheld = true;
        Coordinator().TickOnce();
        var panelId = Assert.Single(_coordinator!.LivePanelDeviceIds());

        _discovery.Withheld = true;
        var restarted = new StreamedPanelCoordinator(
            new[] { _discovery }, _store, _registry, _gate,
            notifyOverlay: null, nowMs: () => _nowMs, notifyPanelChanged: _panelChanges.Add);
        _panelChanges.Clear();
        restarted.TickOnce();

        Assert.Empty(restarted.GetAssignments().Assignments);
        Assert.Contains(panelId, restarted.LivePanelDeviceIds());
        Assert.Contains(panelId, _panelChanges);
    }

    private static StreamedPanelDeviceInfo SingleUnit(string serial) => new()
    {
        Serial = serial,
        AdoptsFamilyRecords = true,
        Profile = new StreamedPanelProfile { Kind = "fake-fs", Family = "fake-fs", DisplayName = "Fake Panel", Surface = "monitor", CssWidth = 640, CssHeight = 480, Fps = 30 },
    };

    private PanelDeviceRecord FamilyRecord(string name, long lastSeenAt)
    {
        var record = _registry.Allocate(name, new PanelDeviceCapabilities { Surface = "monitor", Family = "fake-fs" });
        _config.Update(s => s.PanelDevices[record.Id].LastSeenAt = lastSeenAt);
        return record;
    }

    [Fact]
    public void A_single_unit_panel_keeps_the_most_recently_seen_family_record_and_removes_the_rest_through_the_shared_cleanup()
    {
        var older = FamilyRecord("older", 1_000);
        var newer = FamilyRecord("newer", 2_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["365233833333"] = new() { PanelDeviceId = older.Id },
            ["AsusTek Computer Inc."] = new() { PanelDeviceId = newer.Id },
        });
        var removed = new List<string>();
        _coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, _store, _registry, _gate, notifyOverlay: null, nowMs: () => _nowMs,
            notifyPanelChanged: _panelChanges.Add, removePanel: id => { removed.Add(id); return _registry.Remove(id); });
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        _coordinator.TickOnce();

        Assert.Equal(newer.Id, Assert.Single(_coordinator.GetAssignments().Assignments).PanelDeviceId);
        Assert.Equal(new[] { older.Id }, removed);
        Assert.Equal(new[] { "fixed-key" }, _store.Load().Keys);
    }

    private void Touch(string id) => _config.Update(s => s.PanelDevices[id].Flip180 = false);

    [Fact]
    public void The_customers_two_record_shape_keeps_the_record_with_a_stored_setting()
    {
        // Seen in the alpha.2 bundle: the older record carries an explicit setting and the later-seen one is bare.
        var touched = FamilyRecord("touched", 2_000);
        Touch(touched.Id);
        var bare = FamilyRecord("bare", 1_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["365233833333"] = new() { PanelDeviceId = touched.Id },
            ["AsusTek Computer Inc."] = new() { PanelDeviceId = bare.Id },
        });
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        Coordinator().TickOnce();

        Assert.Equal(touched.Id, Assert.Single(_coordinator!.GetAssignments().Assignments).PanelDeviceId);
        Assert.Null(_registry.Get(bare.Id));
    }

    [Fact]
    public void A_customized_record_outranks_a_bare_one_that_was_streamed_to_last()
    {
        var customized = FamilyRecord("customized", 1_000);
        _config.Update(s => s.PanelDevices[customized.Id].ThemeMode = "dark");
        var bareLater = FamilyRecord("bare", 9_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["a"] = new() { PanelDeviceId = customized.Id },
            ["b"] = new() { PanelDeviceId = bareLater.Id },
        });
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        Coordinator().TickOnce();

        Assert.Equal(customized.Id, Assert.Single(_coordinator!.GetAssignments().Assignments).PanelDeviceId);
        Assert.Null(_registry.Get(bareLater.Id));
    }

    [Fact]
    public void A_record_whose_device_folder_holds_uploaded_media_counts_as_customized()
    {
        // Media files live outside the record, so the check is injected the way the service wires it.
        var withMedia = FamilyRecord("media", 1_000);
        var bareLater = FamilyRecord("bare", 9_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["a"] = new() { PanelDeviceId = withMedia.Id },
            ["b"] = new() { PanelDeviceId = bareLater.Id },
        });
        _coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, _store, _registry, _gate, notifyOverlay: null, nowMs: () => _nowMs,
            notifyPanelChanged: _panelChanges.Add,
            isCustomized: id => id == withMedia.Id || _registry.IsCustomized(id));
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        _coordinator.TickOnce();

        Assert.Equal(withMedia.Id, Assert.Single(_coordinator.GetAssignments().Assignments).PanelDeviceId);
        Assert.Null(_registry.Get(bareLater.Id));
    }

    [Fact]
    public void Two_customized_records_are_both_kept_and_only_one_takes_the_fixed_key()
    {
        var first = FamilyRecord("first", 1_000);
        Touch(first.Id);
        var second = FamilyRecord("second", 2_000);
        _config.Update(s => s.PanelDevices[second.Id].ThemeMode = "dark");
        var bare = FamilyRecord("bare", 3_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["fixed-key"] = new() { PanelDeviceId = first.Id },
            ["serial-b"] = new() { PanelDeviceId = second.Id },
            ["serial-c"] = new() { PanelDeviceId = bare.Id },
        });
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        Coordinator().TickOnce();

        // The most recently seen customized record streams; nothing the user changed is deleted.
        Assert.Equal(second.Id, Assert.Single(_coordinator!.GetAssignments().Assignments).PanelDeviceId);
        Assert.NotNull(_registry.Get(first.Id));
        Assert.NotNull(_registry.Get(second.Id));
        Assert.Null(_registry.Get(bare.Id));
        var stored = _store.Load();
        Assert.Equal(second.Id, stored["fixed-key"].PanelDeviceId);
        Assert.Contains(stored.Values, r => r.PanelDeviceId == first.Id);
        Assert.DoesNotContain(stored.Values, r => r.PanelDeviceId == bare.Id);
    }

    [Fact]
    public void A_customized_check_that_fails_keeps_every_record_and_does_not_escape()
    {
        var a = FamilyRecord("a", 1_000);
        var b = FamilyRecord("b", 2_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["a"] = new() { PanelDeviceId = a.Id },
            ["b"] = new() { PanelDeviceId = b.Id },
        });
        _coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, _store, _registry, _gate, notifyOverlay: null, nowMs: () => _nowMs,
            notifyPanelChanged: _panelChanges.Add, isCustomized: _ => throw new IOException("listing failed"));
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        _coordinator.TickOnce();

        Assert.Single(_coordinator.GetAssignments().Assignments);
        Assert.NotNull(_registry.Get(a.Id));
        Assert.NotNull(_registry.Get(b.Id));
    }

    [Fact]
    public void The_customized_check_looks_at_the_stored_record_including_presets_a_clone_leaves_out()
    {
        var record = FamilyRecord("presets", 1_000);
        _config.Update(s => s.PanelDevices[record.Id].ActivePresetId = "preset-1");

        Assert.True(_registry.IsCustomized(record.Id));
        Assert.False(PanelDeviceRegistry.IsCustomized(_registry.Get(record.Id)!));
    }

    [Fact]
    public void A_stored_layout_makes_a_record_customized()
    {
        var record = FamilyRecord("calendar", 1_000);
        _config.Update(s => s.PanelDevices[record.Id].Layout = new PanelLayoutDto { Surface = "lcd-square" });

        Assert.True(PanelDeviceRegistry.IsCustomized(_registry.Get(record.Id)!));
    }

    [Fact]
    public void A_fresh_record_is_not_customized_and_each_stored_field_makes_it_so()
    {
        var record = FamilyRecord("fresh", 1_000);
        Assert.False(PanelDeviceRegistry.IsCustomized(_registry.Get(record.Id)!));

        _config.Update(s => s.PanelDevices[record.Id].AccentColor = "#ff0000");

        Assert.True(PanelDeviceRegistry.IsCustomized(_registry.Get(record.Id)!));
    }

    [Fact]
    public void Adoption_runs_every_time_so_a_mix_of_fixed_and_serial_keys_is_merged()
    {
        var onFixedKey = FamilyRecord("fixed", 1_000);
        var onSerial = FamilyRecord("serial", 3_000);
        _store.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["fixed-key"] = new() { PanelDeviceId = onFixedKey.Id },
            ["365233833333"] = new() { PanelDeviceId = onSerial.Id },
        });
        _discovery.Devices.Add(SingleUnit("fixed-key"));

        Coordinator().TickOnce();

        Assert.Equal(onSerial.Id, Assert.Single(_coordinator!.GetAssignments().Assignments).PanelDeviceId);
        Assert.Null(_registry.Get(onFixedKey.Id));
        Assert.Equal(new[] { "fixed-key" }, _store.Load().Keys);
    }

    [Fact]
    public void Gate_off_closes_immediately_without_linger()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        Assert.Single(coordinator.GetAssignments().Assignments);

        _gate.SetEnabled("fake-panel", false);
        coordinator.TickOnce();

        Assert.Empty(coordinator.GetAssignments().Assignments);
    }

    [Fact]
    public void Session_start_and_close_tell_clients_the_panel_list_changed()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var id = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        Assert.Equal(new[] { id }, _panelChanges);

        _gate.SetEnabled("fake-panel", false);
        coordinator.TickOnce();

        Assert.Equal(new[] { id, id }, _panelChanges);
    }

    [Fact]
    public void Profile_change_remints_session_and_keeps_record()
    {
        _discovery.Devices.Add(Device(fps: 30));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var first = Assert.Single(coordinator.GetAssignments().Assignments);

        _discovery.Devices.Clear();
        _discovery.Devices.Add(Device(fps: 60));
        coordinator.TickOnce();
        var second = Assert.Single(coordinator.GetAssignments().Assignments);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(60, second.Fps);
        Assert.Equal(first.PanelDeviceId, second.PanelDeviceId);
    }

    private static StreamedPanelDeviceInfo Sized(int width, int height, StreamCodec codec = StreamCodec.RawBgra) => new()
    {
        Serial = "sized",
        Profile = new StreamedPanelProfile { Kind = "sized", DisplayName = "Sized", Surface = "lcd-wide", CssWidth = width, CssHeight = height, Codec = codec },
    };

    [Fact]
    public void A_raw_panel_over_1000_px_renders_lower_until_its_record_asks_for_high_resolution()
    {
        _discovery.Devices.Add(Sized(2288, 1080));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var half = Assert.Single(coordinator.GetAssignments().Assignments);

        Assert.Equal(0.5, half.Dpr);
        Assert.Equal(2288, half.CssWidth);
        Assert.Equal(1080, half.CssHeight);
        var caps = _registry.Get(half.PanelDeviceId)!.Capabilities!;
        Assert.True(caps.SupportsRenderScale);
        Assert.Equal(1.0, caps.Dpr);

        _registry.Patch(half.PanelDeviceId, new PanelDevicePatch { HighResolution = true });
        coordinator.TickOnce();
        var full = Assert.Single(coordinator.GetAssignments().Assignments);

        Assert.Equal(1.0, full.Dpr);
        Assert.NotEqual(half.SessionId, full.SessionId);
        Assert.Equal(half.PanelDeviceId, full.PanelDeviceId);

        _registry.Patch(half.PanelDeviceId, new PanelDevicePatch { HighResolution = false });
        coordinator.TickOnce();

        Assert.Equal(0.5, Assert.Single(coordinator.GetAssignments().Assignments).Dpr);
    }

    [Fact]
    public void A_record_that_asks_for_high_resolution_renders_native_from_the_first_session()
    {
        _discovery.Devices.Add(Sized(1920, 480));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        Assert.Equal(2.0 / 3, Assert.Single(coordinator.GetAssignments().Assignments).Dpr);
        _registry.Patch(panelId, new PanelDevicePatch { HighResolution = true });
        _discovery.Devices.Clear();
        _nowMs += 120_000;
        coordinator.TickOnce();
        Assert.Empty(coordinator.GetAssignments().Assignments);

        _discovery.Devices.Add(Sized(1920, 480));
        coordinator.TickOnce();

        Assert.Equal(1.0, Assert.Single(coordinator.GetAssignments().Assignments).Dpr);
    }

    [Theory]
    [InlineData(1000, 1000, StreamCodec.RawBgra)]
    [InlineData(640, 480, StreamCodec.RawBgra)]
    [InlineData(1024, 600, StreamCodec.H264)]
    public void Other_panels_always_render_at_native(int width, int height, StreamCodec codec)
    {
        _discovery.Devices.Add(Sized(width, height, codec));
        var coordinator = Coordinator();
        coordinator.TickOnce();

        var assignment = Assert.Single(coordinator.GetAssignments().Assignments);

        Assert.Equal(1.0, assignment.Dpr);
        Assert.Null(_registry.Get(assignment.PanelDeviceId)!.Capabilities!.SupportsRenderScale);
    }

    [Fact]
    public void A_profile_change_tells_clients_only_once_the_new_session_is_live()
    {
        var liveAtNotify = new List<bool>();
        StreamedPanelCoordinator? coordinator = null;
        _coordinator = coordinator = new StreamedPanelCoordinator(
            new[] { _discovery }, _store, _registry, _gate,
            notifyOverlay: null,
            nowMs: () => _nowMs,
            notifyPanelChanged: id => liveAtNotify.Add(coordinator!.LivePanelDeviceIds().Contains(id)));
        _discovery.Devices.Add(Device(fps: 30));
        coordinator.TickOnce();
        liveAtNotify.Clear();

        _discovery.Devices.Clear();
        _discovery.Devices.Add(Device(fps: 60));
        coordinator.TickOnce();

        Assert.Equal(60, Assert.Single(coordinator.GetAssignments().Assignments).Fps);
        Assert.Equal(new[] { true }, liveAtNotify);
    }

    [Fact]
    public void Failed_transport_open_still_publishes_and_retries_next_tick()
    {
        _discovery.Devices.Add(Device());
        _discovery.FailOpen = true;
        var coordinator = Coordinator();

        coordinator.TickOnce();
        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.All(_discovery.Transports, t => Assert.False(t.IsOpen));

        _discovery.FailOpen = false;
        coordinator.TickOnce();
        Assert.Contains(_discovery.Transports, t => t.IsOpen);
    }

    [Fact]
    public void Ingest_bind_supersedes_previous_connection()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var sessionId = coordinator.GetAssignments().Assignments[0].SessionId;

        var ctx1 = new DefaultHttpContext();
        var ctx2 = new DefaultHttpContext();
        var bound1 = coordinator.TryBindIngest(sessionId, ctx1);
        var bound2 = coordinator.TryBindIngest(sessionId, ctx2);
        Assert.NotNull(bound1);
        Assert.Same(bound1, bound2);

        // The superseded connection's close must not unbind the live one.
        coordinator.OnIngestClosed(sessionId, ctx1);
        bound2!.SetTransportUp(true);
        Assert.Equal(StreamSessionState.Live, bound2.State);

        coordinator.OnIngestClosed(sessionId, ctx2);
        Assert.Equal(StreamSessionState.WaitingForIngest, bound2.State);
    }

    [Fact]
    public void Unknown_session_id_does_not_bind()
    {
        var coordinator = Coordinator();
        Assert.Null(coordinator.TryBindIngest("nope", new DefaultHttpContext()));
    }

    [Fact]
    public void Brightness_change_can_apply_without_an_incoming_frame()
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var assignment = Assert.Single(coordinator.GetAssignments().Assignments);

        _registry.Patch(assignment.PanelDeviceId, new PanelDevicePatch { LcdBrightness = 35 });

        coordinator.ApplyBrightness(assignment.PanelDeviceId);
        var transport = Assert.Single(_discovery.BrightnessTransports);
        Assert.Equal(1, transport.ApplyCalls);
        Assert.Equal(35, transport.LastApplied);
    }

    // ---- Sleep when locked -------------------------------------------------

    private void Advance(StreamedPanelCoordinator coordinator, int ms)
    {
        _nowMs += ms;
        coordinator.StepFades();
    }

    private (StreamedPanelCoordinator Coordinator, string PanelId, FakeBrightnessTransport Transport) DimmablePanel(int? stored)
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device(dimmable: true));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        if (stored is int value)
        {
            _registry.Patch(panelId, new PanelDevicePatch { LcdBrightness = value });
        }
        return (coordinator, panelId, Assert.Single(_discovery.BrightnessTransports));
    }

    [Fact]
    public void Lock_darkens_a_dimmable_panel_and_unlock_restores_the_stored_backlight()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: 35);

        _lock.Set(true);
        Advance(coordinator, 5000);

        Assert.Equal(0, transport.LastApplied);
        Assert.Empty(coordinator.GetAssignments().Assignments);
        Assert.Equal(35, _registry.Get(panelId)!.LcdBrightness);

        _lock.Set(false);
        Advance(coordinator, 5000);

        Assert.Equal(35, transport.LastApplied);
        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.Equal(35, _registry.Get(panelId)!.LcdBrightness);
    }

    [Fact]
    public void Unlock_wakes_a_panel_whose_setting_was_turned_off_mid_lock()
    {
        var (_, panelId, transport) = DimmablePanel(stored: 60);
        _lock.Set(true);

        _registry.Patch(panelId, new PanelDevicePatch { SleepWhenLocked = false });
        _lock.Set(false);

        Assert.Equal(60, transport.LastApplied);
    }

    [Fact]
    public void A_setting_turned_on_mid_lock_waits_for_the_next_lock()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: 60);
        _registry.Patch(panelId, new PanelDevicePatch { SleepWhenLocked = false });
        _lock.Set(true);

        _registry.Patch(panelId, new PanelDevicePatch { SleepWhenLocked = true });

        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.NotEqual(0, transport.LastApplied);
    }

    [Fact]
    public void A_panel_with_the_setting_off_stays_lit_through_a_lock()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: 60);
        _registry.Patch(panelId, new PanelDevicePatch { SleepWhenLocked = false });

        _lock.Set(true);

        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.NotEqual(0, transport.LastApplied);
    }

    [Fact]
    public void A_panel_that_cannot_dim_is_left_alone_by_the_lock()
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device(dimmable: false));
        var coordinator = Coordinator();
        coordinator.TickOnce();

        _lock.Set(true);

        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.All(_discovery.BrightnessTransports, t => Assert.NotEqual(0, t.LastApplied));
    }

    [Fact]
    public void A_screen_the_user_turned_off_stays_off_after_unlock()
    {
        var (_, _, transport) = DimmablePanel(stored: 0);

        _lock.Set(true);
        _lock.Set(false);

        Assert.Equal(0, transport.LastApplied);
    }

    [Fact]
    public void A_record_with_no_stored_backlight_returns_to_full_after_unlock()
    {
        var (coordinator, _, transport) = DimmablePanel(stored: null);

        _lock.Set(true);
        Advance(coordinator, 5000);
        Assert.Equal(0, transport.LastApplied);
        _lock.Set(false);
        Advance(coordinator, 5000);

        Assert.Equal(StreamedPanelCoordinator.UnlockDefaultBrightness, transport.LastApplied);
    }

    [Fact]
    public void A_panel_that_attaches_while_locked_joins_the_sleep()
    {
        var coordinator = Coordinator();
        _discovery.UseBrightnessTransport = true;
        _lock.Set(true);

        _discovery.Devices.Add(Device(dimmable: true));
        coordinator.TickOnce();

        var transport = Assert.Single(_discovery.BrightnessTransports);
        Assert.Equal(0, transport.LastApplied);
        Assert.Empty(coordinator.GetAssignments().Assignments);

        _lock.Set(false);
        Advance(coordinator, 5000);

        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.NotEqual(0, transport.LastApplied);
    }

    [Fact]
    public void A_reconnect_while_locked_gets_the_dark_value_not_the_stored_one()
    {
        var (coordinator, _, first) = DimmablePanel(stored: 80);
        _lock.Set(true);

        // The transport faults and the next poll opens a fresh one.
        first.Dispose();
        coordinator.TickOnce();

        var reopened = _discovery.BrightnessTransports.Last();
        Assert.NotSame(first, reopened);
        Assert.Equal(0, reopened.LastApplied);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_frame_thread_asking_first_never_deadlocks_with_the_lock_handler()
    {
        var (_, _, transport) = DimmablePanel(stored: 50);

        // Each round is a fresh lock epoch, so the first ask for the decision comes from whichever
        // thread gets there first: the lock handler (coordinator lock, then the transport's) or a
        // frame thread (the transport's lock, then the getter).
        for (var round = 0; round < 400; round++)
        {
            _lock.Set(false);
            using var barrier = new Barrier(2);
            var handler = System.Threading.Tasks.Task.Run(() => { barrier.SignalAndWait(); _lock.Set(true); });
            var frames = System.Threading.Tasks.Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < 5; i++) transport.FrameThreadRead();
            });
            var both = System.Threading.Tasks.Task.WhenAll(handler, frames);
            var finished = await System.Threading.Tasks.Task.WhenAny(both, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20))) == both;
            Assert.True(finished, $"deadlocked in round {round}");
        }
    }

    [Fact]
    public void A_panel_with_no_stored_backlight_returns_to_the_level_it_had_before_the_lock()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: null);
        transport.Applied = 42;

        _lock.Set(true);
        Advance(coordinator, 5000);
        Assert.Equal(0, transport.LastApplied);
        Assert.Equal(42, _registry.Get(panelId)!.LockRestoreBrightness);
        Assert.Null(_registry.Get(panelId)!.LcdBrightness);

        _lock.Set(false);
        Advance(coordinator, 5000);
        Assert.Equal(42, transport.LastApplied);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
    }

    [Fact]
    public void A_lock_whose_unlock_ran_before_its_marker_write_leaves_no_marker_and_no_dark_glass()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: null);
        transport.Applied = 42;
        // The unlock runs completely between the lock's dark apply and its marker write.
        coordinator.TestHookBeforeMarkerWrite = () =>
        {
            coordinator.TestHookBeforeMarkerWrite = null;
            _lock.Set(false);
        };

        _lock.Set(true);

        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
        Assert.Equal(42, transport.Applied);
    }

    [Fact]
    public void A_failed_restoring_write_keeps_the_marker_for_the_next_connect_to_retry()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: null);
        transport.Applied = 42;
        _lock.Set(true);
        Advance(coordinator, 5000);
        transport.FailWrites = true;

        _lock.Set(false);
        Advance(coordinator, 5000);

        Assert.Equal(42, _registry.Get(panelId)!.LockRestoreBrightness);
        Assert.Equal(0, transport.Applied);

        transport.Dispose();
        coordinator.TickOnce();
        var reopened = _discovery.BrightnessTransports.Last();
        Assert.Equal(42, reopened.LastApplied);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
    }

    [Fact]
    public void Lock_fades_down_while_still_rendering_then_goes_dark_at_zero()
    {
        var (coordinator, _, transport) = DimmablePanel(stored: 60);

        _lock.Set(true);
        Assert.Equal(60, transport.Applied);
        Assert.Single(coordinator.GetAssignments().Assignments);

        Advance(coordinator, 750);
        Assert.InRange(transport.Applied!.Value, 1, 59);
        Assert.Single(coordinator.GetAssignments().Assignments);

        Advance(coordinator, 2000);
        Assert.Equal(0, transport.Applied);
        Assert.Empty(coordinator.GetAssignments().Assignments);
    }

    [Fact]
    public void Unlock_fades_up_from_zero_to_exactly_the_level_the_lock_started_at()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: 60);
        _lock.Set(true);
        Advance(coordinator, 5000);

        _lock.Set(false);
        Assert.Equal(0, transport.Applied);
        Advance(coordinator, 450);
        Assert.InRange(transport.Applied!.Value, 1, 59);
        Advance(coordinator, 2000);

        Assert.Equal(60, transport.Applied);
        Assert.Equal(60, _registry.Get(panelId)!.LcdBrightness);
    }

    [Fact]
    public void An_unlock_mid_fade_down_reverses_to_the_start_level_not_the_step_it_caught()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: null);
        transport.Applied = 42;
        _lock.Set(true);
        Advance(coordinator, 750);
        Assert.InRange(transport.Applied!.Value, 1, 41);

        _lock.Set(false);
        Advance(coordinator, 5000);

        Assert.Equal(42, transport.Applied);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
        Assert.Null(_registry.Get(panelId)!.LcdBrightness);
    }

    [Fact]
    public void A_lock_mid_fade_up_reverses_down_and_the_next_unlock_still_restores_the_start_level()
    {
        var (coordinator, _, transport) = DimmablePanel(stored: 60);
        _lock.Set(true);
        Advance(coordinator, 5000);
        _lock.Set(false);
        Advance(coordinator, 450);
        Assert.InRange(transport.Applied!.Value, 1, 59);

        _lock.Set(true);
        Advance(coordinator, 5000);
        Assert.Equal(0, transport.Applied);

        _lock.Set(false);
        Advance(coordinator, 5000);
        Assert.Equal(60, transport.Applied);
    }

    [Fact]
    public void A_fade_never_persists_an_intermediate_level()
    {
        var (coordinator, panelId, _) = DimmablePanel(stored: 60);
        _lock.Set(true);
        Advance(coordinator, 750);
        Assert.Equal(60, _registry.Get(panelId)!.LcdBrightness);
        _lock.Set(false);
        Advance(coordinator, 200);

        Assert.Equal(60, _registry.Get(panelId)!.LcdBrightness);
    }

    [Fact]
    public void A_reconnect_during_the_unlock_ramp_does_not_leave_the_panel_on_the_step_it_caught()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: null);
        transport.Applied = 42;
        _lock.Set(true);
        Advance(coordinator, 5000);
        _lock.Set(false);
        Advance(coordinator, 450);

        transport.Dispose();
        coordinator.TickOnce();
        var reopened = _discovery.BrightnessTransports.Last();
        Advance(coordinator, 5000);

        Assert.Equal(42, reopened.Applied);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
    }

    [Fact]
    public void A_panel_the_lock_never_slept_is_not_faded_from_zero_on_unlock()
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device(serial: "a", dimmable: true));
        _discovery.Devices.Add(Device(serial: "b", dimmable: true));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var records = _store.Load();
        var slept = records["a"].PanelDeviceId;
        var spared = records["b"].PanelDeviceId;
        _registry.Patch(slept, new PanelDevicePatch { LcdBrightness = 60 });
        _registry.Patch(spared, new PanelDevicePatch { LcdBrightness = 50, SleepWhenLocked = false });
        var sparedTransport = _discovery.BrightnessTransports.First(t => t.Serial == "b");

        _lock.Set(true);
        Advance(coordinator, 5000);
        _lock.Set(false);
        Advance(coordinator, 40);

        Assert.Null(sparedTransport.Applied);
    }

    [Fact]
    public void A_panel_whose_fade_step_throws_settles_alone_and_the_other_panel_keeps_ramping()
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device(serial: "a", dimmable: true));
        _discovery.Devices.Add(Device(serial: "b", dimmable: true));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var records = _store.Load();
        _registry.Patch(records["a"].PanelDeviceId, new PanelDevicePatch { LcdBrightness = 60 });
        _registry.Patch(records["b"].PanelDeviceId, new PanelDevicePatch { LcdBrightness = 60 });
        var a = _discovery.BrightnessTransports.First(t => t.Serial == "a");
        var b = _discovery.BrightnessTransports.First(t => t.Serial == "b");
        _lock.Set(true);

        a.ThrowOnNextApply = true;
        Advance(coordinator, 750);

        // A is settled dark on its own; B is still partway down its own ramp.
        Assert.Equal(0, a.Applied);
        Assert.InRange(b.Applied!.Value, 1, 59);
        Advance(coordinator, 5000);
        Assert.Equal(0, b.Applied);
    }

    [Fact]
    public void A_restore_marker_left_by_a_restart_mid_lock_is_applied_and_cleared_on_connect()
    {
        _discovery.UseBrightnessTransport = true;
        _discovery.Devices.Add(Device(dimmable: true));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;
        // The state a previous run left: dark glass, a marker, and no lock in progress.
        _registry.SetLockRestoreBrightness(panelId, 64);
        var first = Assert.Single(_discovery.BrightnessTransports);
        first.Dispose();

        coordinator.TickOnce();

        var reopened = _discovery.BrightnessTransports.Last();
        Assert.Equal(64, reopened.LastApplied);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
    }

    [Fact]
    public void A_lock_handler_that_runs_after_the_unlock_does_not_dim_the_panel()
    {
        var (coordinator, panelId, transport) = DimmablePanel(stored: 70);

        // The handler for a lock whose unlock already happened.
        coordinator.OnLockChanged(true);

        Assert.NotEqual(0, transport.LastApplied);
        Assert.Single(coordinator.GetAssignments().Assignments);
        Assert.Null(_registry.Get(panelId)!.LockRestoreBrightness);
    }

    [Fact]
    public void A_panel_showing_the_desktop_gets_no_render_host_but_stays_listed()
    {
        _discovery.Devices.Add(Device(monitor: true));
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;

        _registry.Patch(panelId, new PanelDevicePatch { SecondaryMonitor = true });

        Assert.Empty(coordinator.GetAssignments().Assignments);
        Assert.Contains(panelId, coordinator.LivePanelDeviceIds());

        _registry.Patch(panelId, new PanelDevicePatch { SecondaryMonitor = false });

        Assert.Single(coordinator.GetAssignments().Assignments);
    }

    [Fact]
    public void The_monitor_setting_is_ignored_where_the_panel_cannot_show_one()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();
        coordinator.TickOnce();
        var panelId = Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId;

        _registry.Patch(panelId, new PanelDevicePatch { SecondaryMonitor = true });

        Assert.Single(coordinator.GetAssignments().Assignments);
    }
}
