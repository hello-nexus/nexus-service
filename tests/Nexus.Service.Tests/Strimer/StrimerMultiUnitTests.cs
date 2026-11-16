using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Strimer;

public class StrimerMultiUnitTests
{
    private sealed class FakeDevice : IHidDevice
    {
        public FakeDevice(string path, string? serial) { Path = path; Serial = serial; }
        public int WriteCount;
        public bool Disposed;
        public bool WriteOk = true;
        public int VendorId => StrimerProtocol.VendorId;
        public int ProductId => StrimerProtocol.ProductId;
        public string Path { get; }
        public string? Serial { get; }
        public int UsagePage => StrimerProtocol.VendorUsagePage;
        public int Usage => StrimerProtocol.VendorUsage;
        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool Write(ReadOnlySpan<byte> report) { WriteCount++; return WriteOk; }
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> rpt) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHid : IHidEnumerator
    {
        public List<HidDeviceInfo> Present { get; } = new();
        public Dictionary<string, FakeDevice> Opened { get; } = new();
        public HashSet<string> Busy { get; } = new();
        public int FindCalls;

        public void Plug(string path, string? serial) => Present.Add(Info(path, serial));
        public void Unplug(string path) => Present.RemoveAll(i => i.Path == path);

        private static HidDeviceInfo Info(string path, string? serial) => new()
        {
            VendorId = StrimerProtocol.VendorId,
            ProductId = StrimerProtocol.ProductId,
            Path = path,
            Serial = serial,
            UsagePage = StrimerProtocol.VendorUsagePage,
            Usage = StrimerProtocol.VendorUsage,
        };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) { FindCalls++; return Present.ToList(); }
        public IReadOnlyList<HidDeviceInfo> FindAll() => Present.ToList();

        public IHidDevice? Open(string path, bool forInput = false)
        {
            if (Busy.Contains(path)) return null;
            var info = Present.First(i => i.Path == path);
            var d = new FakeDevice(path, info.Serial);
            Opened[path] = d;
            return d;
        }
    }

    private readonly FakeHid _hid = new();
    private readonly StrimerUnits _units = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly StrimerLightingDeviceProvider _provider;
    private readonly StrimerConnectionWorker _worker;

    public StrimerMultiUnitTests()
    {
        _provider = new StrimerLightingDeviceProvider(_units, _store, new Np50IdentifyTracker());
        _worker = new StrimerConnectionWorker(_hid, _units, _provider, new DeviceControlGate(_store), new HardwarePresence(null!));
    }

    private long _clock;

    // Each call is past the rescan interval, so it always enumerates.
    private void PollNow() => _worker.Poll(_hid.Present.Count, _clock += 31_000);

    [Fact]
    public void Both_controllers_open_in_serial_order_regardless_of_enumeration_order()
    {
        _hid.Plug("path-b", "B-SERIAL");
        _hid.Plug("path-a", "A-SERIAL");

        PollNow();

        Assert.Equal("path-a", _units.All[0].AttachedPath);
        Assert.Equal("path-b", _units.All[1].AttachedPath);
        Assert.Equal(2, _units.Connected().Count);
    }

    [Fact]
    public void Missing_or_duplicate_serials_order_by_path()
    {
        _hid.Plug("path-b", "SAME");
        _hid.Plug("path-a", "SAME");

        PollNow();

        Assert.Equal("path-a", _units.All[0].AttachedPath);
        Assert.Equal("path-b", _units.All[1].AttachedPath);
    }

    [Fact]
    public void First_unit_keeps_the_original_ids_and_second_gets_suffixed_ids()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        PollNow();

        var structures = _provider.GetStructures();
        var ids = structures.Select(s => s.DeviceId).ToArray();
        Assert.Equal(new[] { "strimer:atx", "strimer:gpu", "strimer:2:atx", "strimer:2:gpu" }, ids);

        var cards = _provider.GetAll().Devices;
        Assert.Equal(24, cards.Count);
        Assert.Equal(24, cards.Select(c => c.Id).Distinct().Count());
        Assert.Equal(12, cards.Count(c => c.ParentDeviceId == "strimer"));
        Assert.Equal(12, cards.Count(c => c.ParentDeviceId == "strimer:2"));
        Assert.Contains(cards, c => c.Id == "strimer:atx:z0");
        Assert.Contains(cards, c => c.Id == "strimer:2:gpu:z5");
        Assert.NotEqual(structures[0].DeviceKey, structures[2].DeviceKey);
        Assert.Equal(DeviceKeyComputer.ForFirstParty(StrimerProtocol.VendorId, StrimerProtocol.ProductId, "atx"), structures[0].DeviceKey);
        Assert.Equal("strimer", _units.All[0].HealthKey);
        Assert.Equal("strimer:2", _units.All[1].HealthKey);
    }

    [Fact]
    public void Frames_are_built_per_unit_with_distinct_ids()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        PollNow();

        var frames = _provider.BuildFrames(0);

        Assert.Equal(24, frames.Count);
        Assert.Equal(24, frames.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void Writes_route_to_each_units_own_handle()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        PollNow();
        var writer = new StrimerLightingFrameWriter(new LightingEngine(), _units, _store, new Np50IdentifyTracker(), _provider);

        writer.TickCustom(_store.Load(), Array.Empty<DeviceFrame>(), new MasterLevel(1f, false));

        Assert.Equal(25, _hid.Opened["path-a"].WriteCount);
        Assert.Equal(25, _hid.Opened["path-b"].WriteCount);
    }

    [Fact]
    public void A_failing_unit_detaches_and_leaves_the_other_connected()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        PollNow();
        var b = _hid.Opened["path-b"];
        b.WriteOk = false;
        for (var i = 0; i < 3; i++) _units.All[1].SendApplyLatch();
        _hid.Unplug("path-b");

        PollNow();

        Assert.True(_units.All[0].IsConnected);
        Assert.False(_units.All[1].IsConnected);
        Assert.True(b.Disposed);
        Assert.False(_hid.Opened["path-a"].Disposed);
        Assert.Equal(12, _provider.GetAll().Devices.Count);
        Assert.All(_provider.GetAll().Devices, d => Assert.DoesNotContain("strimer:2", d.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Replugged_unit_reclaims_its_slot_and_ids_when_the_first_unit_is_gone()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        PollNow();
        var aOld = _hid.Opened["path-a"];
        aOld.WriteOk = false;
        for (var i = 0; i < 3; i++) _units.All[0].SendApplyLatch();
        _hid.Unplug("path-a");
        PollNow();

        // B keeps its ids while A is away, and A returns to slot 0.
        Assert.False(_units.All[0].IsConnected);
        Assert.Equal("path-b", _units.All[1].AttachedPath);
        Assert.Equal("strimer:2:atx", _provider.GetStructures()[0].DeviceId);

        _hid.Plug("path-a", "A");
        PollNow();

        Assert.Equal("path-a", _units.All[0].AttachedPath);
        Assert.Equal("path-b", _units.All[1].AttachedPath);
    }

    [Fact]
    public void Single_controller_keeps_legacy_ids_only()
    {
        _hid.Plug("path-a", "A");
        PollNow();

        Assert.Equal(new[] { "strimer:atx", "strimer:gpu" }, _provider.GetStructures().Select(s => s.DeviceId).ToArray());
        Assert.Equal(12, _provider.GetAll().Devices.Count);
    }

    [Fact]
    public void Usb_presence_gone_detaches_every_unit()
    {
        _hid.Plug("path-a", "A");
        PollNow();

        _worker.Poll(0, _clock += 1);

        Assert.False(_units.All[0].IsConnected);
        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public void A_vanished_path_detaches_and_the_lone_replugged_unit_reclaims_slot_one()
    {
        _hid.Plug("iokit:1", null);
        PollNow();
        _hid.Unplug("iokit:1");
        _hid.Plug("iokit:2", null);

        PollNow();

        Assert.Equal("iokit:2", _units.All[0].AttachedPath);
        Assert.False(_units.All[1].IsConnected);
        Assert.Equal(new[] { "strimer:atx", "strimer:gpu" }, _provider.GetStructures().Select(s => s.DeviceId).ToArray());
    }

    [Fact]
    public void Steady_state_skips_hid_enumeration_until_the_count_changes_or_the_rescan_is_due()
    {
        _hid.Plug("path-a", "A");
        _worker.Poll(1, 100_000);
        var calls = _hid.FindCalls;

        _worker.Poll(1, 105_000);
        Assert.Equal(calls, _hid.FindCalls);

        _hid.Plug("path-b", "B");
        _worker.Poll(2, 110_000);
        Assert.Equal(calls + 1, _hid.FindCalls);
        Assert.Equal(2, _units.Connected().Count);

        _worker.Poll(2, 141_000);
        Assert.Equal(calls + 2, _hid.FindCalls);
    }

    [Fact]
    public void A_controller_that_opens_late_keeps_the_slot_its_serial_sorts_to()
    {
        _hid.Plug("path-a", "A");
        _hid.Plug("path-b", "B");
        _hid.Busy.Add("path-a");
        PollNow();
        Assert.Equal("path-b", _units.All[1].AttachedPath);
        Assert.False(_units.All[0].IsConnected);

        _hid.Busy.Clear();
        PollNow();

        Assert.Equal("path-a", _units.All[0].AttachedPath);
        Assert.Equal("path-b", _units.All[1].AttachedPath);
    }

    [Fact]
    public void Write_health_is_judged_per_unit()
    {
        var health = new LightingWriteHealth(() => _healthNow);
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure("strimer", "strimer");
            _healthNow += TimeSpan.FromSeconds(1);
        }

        Assert.True(health.IsFailing("strimer:atx:z0", "strimer:atx", "strimer", "strimer"));
        Assert.False(health.IsFailing("strimer:2:atx:z0", "strimer:2:atx", "strimer:2", "strimer"));
    }

    private TimeSpan _healthNow = TimeSpan.Zero;

    [Fact]
    public void Write_health_of_a_later_unit_flags_only_its_own_cards()
    {
        var health = new LightingWriteHealth(() => _healthNow);
        for (var i = 0; i < 17; i++)
        {
            health.NoteFailure("strimer:2", "strimer");
            _healthNow += TimeSpan.FromSeconds(1);
        }

        Assert.True(health.IsFailing("strimer:2:atx:z0", "strimer:2:atx", "strimer:2", "strimer"));
        Assert.False(health.IsFailing("strimer:atx:z0", "strimer:atx", "strimer", "strimer"));
    }

    [Fact]
    public void Reattaching_a_slot_makes_the_writer_resend_the_sync_switch()
    {
        var spy1 = new StrimerTransportSpy();
        _units.All[0].Attach(spy1);
        _store.Update(s => s.Devices.StrimerLighting.ArgbSync = true);
        var writer = new StrimerLightingFrameWriter(new LightingEngine(), _units, _store, new Np50IdentifyTracker(), _provider);
        writer.Tick();
        Assert.Single(spy1.Calls, c => c.Bytes[2] == 0x40);

        var spy2 = new StrimerTransportSpy();
        _units.All[0].Detach();
        _units.All[0].Attach(spy2);
        writer.Tick();

        Assert.Single(spy2.Calls, c => c.Bytes[2] == 0x40);
    }
}
