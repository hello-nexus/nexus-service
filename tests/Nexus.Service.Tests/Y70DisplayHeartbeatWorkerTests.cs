using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Cloud;
using Nexus.Service.Tests.Cloud;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Detection-edge re-orient behavior: a monitor-only Y70 (no serial, no USB
/// enumeration) must still trip the effective-orientation apply exactly once
/// per newly-detected edge, since the heartbeat's serial poll below is gated
/// on USB presence and never sees it.
/// </summary>
public class Y70DisplayHeartbeatWorkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-y70case-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    private static Y70DisplayHeartbeatWorker Build(FakeY70Provider y70)
    {
        var hub = new Y70DisplayHub(
            new StubY70DisplayPortDiscovery(),
            _ => throw new InvalidOperationException("Y70 transport is not expected in these tests"));
        var presence = new HardwarePresence(new EmptyUsbEnumerator());
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        return new Y70DisplayHeartbeatWorker(hub, presence, gate, y70);
    }

    private const string OtherCase = "not-a-y70-case";
    private const string Y70Variant = "ef06ada1-e96d-42ee-8726-05942881d65b";

    private (Y70DisplayHeartbeatWorker Worker, FakeY70Provider Y70, MachineCaseStore Store) BuildWithCase(string? stored, string origin = MachineCaseOrigin.Manual, bool pending = false) =>
        BuildWithCase(stored, origin, pending, out _, out _);

    private (Y70DisplayHeartbeatWorker Worker, FakeY70Provider Y70, MachineCaseStore Store) BuildWithCase(string? stored, string origin, bool pending, out FakeCloudApiClient api, out List<string?> changes)
    {
        var store = new MachineCaseStore(Path.Combine(_dir, "system-case.json"));
        if (stored is not null)
        {
            store.Save(stored, pending, origin);
        }
        var config = new InMemoryConfigStore();
        api = new FakeCloudApiClient();
        var cases = new SystemCaseService(store, new CloudAccountService(api, config), api);
        var seen = new List<string?>();
        cases.Changed += seen.Add;
        changes = seen;
        var y70 = new FakeY70Provider { Connected = true };
        var hub = new Y70DisplayHub(
            new StubY70DisplayPortDiscovery(),
            _ => throw new InvalidOperationException("Y70 transport is not expected in these tests"));
        var worker = new Y70DisplayHeartbeatWorker(
            hub, new HardwarePresence(new EmptyUsbEnumerator()), new DeviceControlGate(config), y70, new Y70CaseAssigner(cases));
        return (worker, y70, store);
    }

    [Fact]
    public void Detected_with_no_case_assigns_the_y70()
    {
        var (worker, _, store) = BuildWithCase(null);

        worker.Tick();

        Assert.Equal(Y70CaseAssigner.DefaultCaseId, store.Load().CaseId);
        Assert.True(Y70CaseAssigner.IsY70Case(store.Load().CaseId));
    }

    [Fact]
    public void Auto_assign_makes_no_account_call_and_notifies_clients()
    {
        var (worker, _, store) = BuildWithCase(null, MachineCaseOrigin.Manual, false, out var api, out var changes);

        worker.Tick();

        Assert.Equal(0, api.SetDeviceCaseCalls);
        Assert.Equal(0, api.ListDevicesCalls);
        Assert.False(store.Load().Pending);
        Assert.Equal(MachineCaseOrigin.Auto, store.Load().Origin);
        Assert.Equal(new[] { Y70CaseAssigner.DefaultCaseId }, changes);
    }

    [Fact]
    public void Detected_with_an_earlier_auto_non_y70_case_assigns_the_y70()
    {
        var (worker, _, store) = BuildWithCase(OtherCase, MachineCaseOrigin.Auto);

        worker.Tick();

        Assert.Equal(Y70CaseAssigner.DefaultCaseId, store.Load().CaseId);
    }

    [Fact]
    public void Restart_with_a_manual_non_y70_pick_changes_nothing()
    {
        var (worker, _, store) = BuildWithCase(OtherCase);

        worker.Tick();

        Assert.Equal(OtherCase, store.Load().CaseId);
        Assert.Equal(MachineCaseOrigin.Manual, store.Load().Origin);
    }

    [Fact]
    public void Legacy_stored_case_without_an_origin_counts_as_manual()
    {
        var (worker, _, store) = BuildWithCase(OtherCase, origin: null!);

        worker.Tick();

        Assert.Equal(OtherCase, store.Load().CaseId);
    }

    [Fact]
    public void A_pending_manual_push_survives_detection()
    {
        var (worker, _, store) = BuildWithCase(OtherCase, MachineCaseOrigin.Manual, pending: true);

        worker.Tick();

        Assert.Equal(OtherCase, store.Load().CaseId);
        Assert.True(store.Load().Pending);
    }

    [Fact]
    public void Detected_with_a_y70_variant_already_set_leaves_it()
    {
        var (worker, _, store) = BuildWithCase(Y70Variant);

        worker.Tick();

        Assert.Equal(Y70Variant, store.Load().CaseId);
    }

    [Fact]
    public void Not_detected_changes_nothing()
    {
        var (worker, y70, store) = BuildWithCase(OtherCase);
        y70.Connected = false;

        worker.Tick();
        worker.Tick();

        Assert.Equal(OtherCase, store.Load().CaseId);
    }

    [Fact]
    public void Repeated_detection_does_not_write_again()
    {
        var (worker, _, store) = BuildWithCase(null);
        worker.Tick();
        // The user picks another case after the automatic assignment.
        store.Save(OtherCase, pending: false, MachineCaseOrigin.Manual);

        worker.Tick();
        worker.Tick();

        Assert.Equal(OtherCase, store.Load().CaseId);
    }

    [Fact]
    public void Detection_after_a_sustained_disconnect_is_a_new_event()
    {
        var (worker, y70, store) = BuildWithCase(null);
        worker.Tick();
        store.Save(OtherCase, pending: false, MachineCaseOrigin.Auto);
        y70.Connected = false;
        worker.Tick();
        worker.Tick();
        worker.Tick();
        y70.Connected = true;

        worker.Tick();

        Assert.Equal(Y70CaseAssigner.DefaultCaseId, store.Load().CaseId);
    }

    [Fact]
    public void Monitor_only_detection_applies_orientation_once_per_edge()
    {
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        Assert.Equal(1, y70.ApplyCount);
    }

    [Fact]
    public void Sustained_disconnect_then_reconnect_reapplies_orientation()
    {
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();               // detected -> apply (1)
        y70.Connected = false;
        for (var i = 0; i < 5; i++) worker.Tick();   // sustained absence past the debounce
        y70.Connected = true;
        worker.Tick();               // genuine re-detect -> apply (2)

        Assert.Equal(2, y70.ApplyCount);
    }

    [Fact]
    public void Transient_absence_does_not_reapply_orientation()
    {
        // A single-tick absence models the Y70 dropping out of enumeration
        // mid-rotation. It must NOT re-arm the edge, or the apply loops.
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();               // detected -> apply (1)
        y70.Connected = false;
        worker.Tick();               // one transient miss (within debounce)
        y70.Connected = true;
        worker.Tick();               // back -> must NOT re-apply

        Assert.Equal(1, y70.ApplyCount);
    }

    [Fact]
    public void Never_detected_never_applies_orientation()
    {
        var y70 = new FakeY70Provider { Connected = false };
        var worker = Build(y70);

        worker.Tick();
        worker.Tick();

        Assert.Equal(0, y70.ApplyCount);
    }

    private sealed class EmptyUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class FakeY70Provider : IY70Provider
    {
        public bool Connected;
        public int ApplyCount;

        public bool IsConnected() => Connected;
        public string GetOrientation() => "PortraitFlipped";
        public void SetOrientation(string orientation) { }
        public bool GetForceOrientation() => true;
        public void SetForceOrientation(bool forceOrientation) { }
        public void ApplyEffectiveOrientation() => ApplyCount++;
        public int GetBrightness() => 0;
        public void SetBrightness(int brightness) { }
        public bool GetToggle() => false;
        public void SetToggle(bool toggle) { }
        public void SetGameModeScreenOff(bool screenOff) { }
        public bool IsRotated() => true;
    }

    /// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
