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
            nowMs: () => _nowMs);
        return _coordinator;
    }

    private static StreamedPanelProfile Profile(string kind = "fake-fs", int fps = 30) => new()
    {
        Kind = kind,
        DisplayName = "Fake Panel",
        Surface = "monitor",
        CssWidth = 640,
        CssHeight = 480,
        Fps = fps,
    };

    private static StreamedPanelDeviceInfo Device(string serial = "d211_fake", string kind = "fake-fs", int fps = 30)
        => new() { Serial = serial, Profile = Profile(kind, fps) };

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
}
