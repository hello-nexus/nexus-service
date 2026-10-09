using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Devices;
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
            notifyPanelChanged: _panelChanges.Add);
        return _coordinator;
    }

    private static StreamedPanelProfile Profile(string kind = "fake-fs", int fps = 30, bool monitor = false) => new()
    {
        SupportsSecondaryMonitor = monitor,
        Kind = kind,
        DisplayName = "Fake Panel",
        Surface = "monitor",
        CssWidth = 640,
        CssHeight = 480,
        Fps = fps,
    };

    private static StreamedPanelDeviceInfo Device(string serial = "d211_fake", string kind = "fake-fs", int fps = 30, bool monitor = false)
        => new() { Serial = serial, Profile = Profile(kind, fps, monitor) };

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

        public FakeBrightnessTransport(string serial) { Serial = serial; }
        public void Open() => IsOpen = true;
        public void StartPlayer() { }
        public void Write(ReadOnlySpan<byte> annexBAccessUnit) { }
        public void Dispose() => IsOpen = false;
        public void BindBrightness(Func<int?> source) => _source = source;
        public void ApplyBrightness()
        {
            ApplyCalls++;
            LastApplied = _source?.Invoke();
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

    [Fact]
    public void A_new_panel_of_ordinary_shape_keeps_the_client_seed()
    {
        _discovery.Devices.Add(Device());
        var coordinator = Coordinator();

        coordinator.TickOnce();

        Assert.Null(_registry.Get(Assert.Single(coordinator.GetAssignments().Assignments).PanelDeviceId)!.Layout);
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
