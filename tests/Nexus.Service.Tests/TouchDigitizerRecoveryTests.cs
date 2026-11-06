using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class TouchDigitizerRecoveryTests
{
    private const string Y70MonitorPath =
        @"\\?\DISPLAY#RTK0004#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string EdgeMonitorPath =
        @"\\?\DISPLAY#CRXED00#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string PointerPath =
        @"\\?\HID#VID_27C0&PID_0859&Col01#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string HidPath =
        @"\\?\hid#vid_27c0&pid_0859&col01#a&14bf8c3d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public DateTime Now = T0;
        public List<HidDeviceInfo> Hid = new() { TouchCollection() };
        public int Restarts;
        public bool RestartResult = true;
        public DateTime? Stamp;
        public bool StampWrites = true;
        public int? Problem = 0;
        public readonly TouchDigitizerRecovery Recovery;

        public Rig()
        {
            Recovery = new TouchDigitizerRecovery(
                (v, p) => Hid.FindAll(h => h.VendorId == v && h.ProductId == p),
                _ => Problem, new Restarter(this), new Stamps(this), () => Now);
        }

        public TimeSpan? Pass(TouchMapSnapshot snapshot, TimeSpan advance)
        {
            Now += advance;
            var (outcome, _) = TouchMappingDecision.Decide(snapshot);
            return Recovery.Observe(snapshot, outcome);
        }

        private sealed class Restarter(Rig r) : ITouchDigitizerDevnodeRestarter
        {
            public bool Restart(string digitizerInterfacePath)
            {
                r.Restarts++;
                return r.RestartResult;
            }
        }

        private sealed class Stamps(Rig r) : ITouchRecoveryStampStore
        {
            public DateTime? LastRestartUtc() => r.Stamp;
            public bool Record(DateTime utc)
            {
                if (!r.StampWrites) return false;
                r.Stamp = utc;
                return true;
            }
        }
    }

    private static HidDeviceInfo TouchCollection(int usagePage = 0x0D, int usage = 0x04) => new()
    {
        VendorId = 0x27C0,
        ProductId = 0x0859,
        Path = HidPath,
        UsagePage = usagePage,
        Usage = usage,
    };

    private static TouchMapSnapshot Absent(bool queryOk = true) => new()
    {
        PointerQueryOk = queryOk,
        TouchInputEnabled = true,
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
    };

    private static TouchMapSnapshot Working() => new()
    {
        PointerQueryOk = true,
        TouchInputEnabled = true,
        Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        Digitizers = { new TouchMapDigitizerInfo { InterfacePath = PointerPath, AssociatedDisplayId = "y70-1" } },
    };

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    [Fact]
    public void Working_system_never_restarts_across_many_checks()
    {
        var rig = new Rig();
        for (var i = 0; i < 200; i++) Assert.Null(rig.Pass(Working(), Tick));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Mismatched_but_present_digitizer_never_restarts()
    {
        var rig = new Rig();
        var s = Working();
        s.Digitizers[0].AssociatedDisplayId = "primary";
        for (var i = 0; i < 20; i++) rig.Pass(s, Tick);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Query_error_never_restarts()
    {
        var rig = new Rig();
        for (var i = 0; i < 20; i++) rig.Pass(Absent(queryOk: false), Tick);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Unnamed_touch_pointer_never_restarts()
    {
        var rig = new Rig();
        var s = Absent();
        s.UnnamedTouchPointers = 1;
        for (var i = 0; i < 20; i++) rig.Pass(s, Tick);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Absent_once_or_inside_settle_window_does_not_restart()
    {
        var rig = new Rig();
        Assert.NotNull(rig.Pass(Absent(), TimeSpan.Zero));
        rig.Pass(Absent(), TimeSpan.FromSeconds(15));
        rig.Pass(Absent(), TimeSpan.FromSeconds(15));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Absent_then_present_resets_the_window()
    {
        var rig = new Rig();
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(40));
        Assert.Null(rig.Pass(Working(), TimeSpan.FromSeconds(1)));
        rig.Pass(Absent(), TimeSpan.FromSeconds(1));
        rig.Pass(Absent(), TimeSpan.FromSeconds(40));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Query_error_in_the_middle_resets_the_window()
    {
        var rig = new Rig();
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(queryOk: false), TimeSpan.FromSeconds(30));
        rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Confirmed_absence_restarts_exactly_once()
    {
        var rig = new Rig();
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
        rig.Pass(Absent(), TimeSpan.FromSeconds(20));
        Assert.Equal(1, rig.Restarts);
        Assert.Equal(rig.Now, rig.Stamp);
        for (var i = 0; i < 50; i++) rig.Pass(Absent(), Tick);
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Still_absent_after_restart_gives_up_and_stops_following_up()
    {
        var rig = new Rig();
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(50));
        Assert.Equal(1, rig.Restarts);
        Assert.NotNull(rig.Pass(Absent(), TimeSpan.FromSeconds(5)));
        Assert.Null(rig.Pass(Absent(), TimeSpan.FromSeconds(20)));
        Assert.Null(rig.Pass(Absent(), Tick));
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Present_after_restart_ends_follow_up_without_a_second_restart()
    {
        var rig = new Rig();
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(50));
        Assert.Null(rig.Pass(Working(), TimeSpan.FromSeconds(10)));
        for (var i = 0; i < 20; i++) rig.Pass(Absent(), TimeSpan.FromSeconds(60));
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Failed_restart_is_not_retried()
    {
        var rig = new Rig { RestartResult = false };
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(50));
        for (var i = 0; i < 20; i++) rig.Pass(Absent(), Tick);
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Missing_hid_collection_never_restarts()
    {
        var rig = new Rig { Hid = new List<HidDeviceInfo>() };
        for (var i = 0; i < 20; i++) rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Theory]
    [InlineData(0x0001, 0x0002)]
    [InlineData(0xFF0A, 0x00FF)]
    [InlineData(0x000D, 0x000E)]
    public void Non_touchscreen_collections_do_not_count(int usagePage, int usage)
    {
        var rig = new Rig { Hid = new List<HidDeviceInfo> { TouchCollection(usagePage, usage) } };
        for (var i = 0; i < 20; i++) rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Hid_collection_appearing_late_waits_its_own_settle_window()
    {
        var rig = new Rig { Hid = new List<HidDeviceInfo>() };
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(300));
        rig.Hid = new List<HidDeviceInfo> { TouchCollection() };
        rig.Pass(Absent(), TimeSpan.FromSeconds(1));
        rig.Pass(Absent(), TimeSpan.FromSeconds(20));
        Assert.Equal(0, rig.Restarts);
        rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Persisted_stamp_inside_cap_blocks_the_restart()
    {
        var rig = new Rig { Stamp = T0 - TimeSpan.FromMinutes(30) };
        rig.Pass(Absent(), TimeSpan.Zero);
        for (var i = 0; i < 20; i++) rig.Pass(Absent(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Old_persisted_stamp_does_not_block()
    {
        var rig = new Rig { Stamp = T0 - TimeSpan.FromHours(5) };
        rig.Pass(Absent(), TimeSpan.Zero);
        rig.Pass(Absent(), TimeSpan.FromSeconds(50));
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Companion_scoped_panel_present_never_restarts()
    {
        var rig = new Rig();
        var s = Absent();
        s.Displays.Add(new TouchMapDisplayInfo { Id = "edge-1", MonitorInterfacePath = EdgeMonitorPath });
        for (var i = 0; i < 20; i++) rig.Pass(s, TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void No_panel_never_restarts()
    {
        var rig = new Rig();
        var s = new TouchMapSnapshot { PointerQueryOk = true, TouchInputEnabled = true };
        for (var i = 0; i < 20; i++) rig.Pass(s, TimeSpan.FromSeconds(30));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Follow_up_is_scheduled_only_while_pending()
    {
        var rig = new Rig();
        Assert.Null(rig.Pass(Working(), Tick));
        Assert.NotNull(rig.Pass(Absent(), Tick));
        Assert.Null(rig.Pass(Working(), Tick));
    }

    private static void ConfirmedAbsence(Rig rig, TouchMapSnapshot? s = null)
    {
        s ??= Absent();
        rig.Pass(s, TimeSpan.Zero);
        rig.Pass(s, TimeSpan.FromSeconds(50));
        for (var i = 0; i < 10; i++) rig.Pass(s, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Edge_pointer_device_claimed_by_companion_blocks_the_restart()
    {
        var rig = new Rig();
        var s = Absent();
        s.Digitizers.Add(new TouchMapDigitizerInfo
        {
            InterfacePath = PointerPath,
            CompanionHardwareIds = { @"USB\VID_1B1C&PID_1D0D\SERIAL" },
        });
        ConfirmedAbsence(rig, s);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Companion_endpoint_on_the_bus_blocks_the_restart()
    {
        var rig = new Rig();
        rig.Hid.Add(new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x1D0D, Path = "x", UsagePage = 0xFF1B, Usage = 1 });
        ConfirmedAbsence(rig);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Two_touch_collections_are_ambiguous_and_never_restart()
    {
        var rig = new Rig();
        rig.Hid.Add(new HidDeviceInfo
        {
            VendorId = 0x27C0, ProductId = 0x0859, Path = HidPath.Replace("14bf8c3d", "99999999"), UsagePage = 0x0D, Usage = 0x04,
        });
        ConfirmedAbsence(rig);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Collection_with_a_different_vid_pid_is_never_restarted()
    {
        var rig = new Rig { Hid = new List<HidDeviceInfo> { new() { VendorId = 0x1234, ProductId = 0x0859, Path = HidPath, UsagePage = 0x0D, Usage = 0x04 } } };
        ConfirmedAbsence(rig);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Touch_turned_off_or_unreadable_never_restarts()
    {
        var rig = new Rig();
        var s = Absent();
        s.TouchInputEnabled = false;
        ConfirmedAbsence(rig, s);
        Assert.Equal(0, rig.Restarts);
        Assert.False(new TouchMapSnapshot().TouchInputEnabled);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(null)]
    public void Disabled_or_unreadable_devnode_never_restarts(int? problem)
    {
        var rig = new Rig { Problem = problem };
        ConfirmedAbsence(rig);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Failed_start_devnode_is_still_restarted()
    {
        var rig = new Rig { Problem = 10 };
        ConfirmedAbsence(rig);
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void Unwritable_stamp_means_no_restart()
    {
        var rig = new Rig { StampWrites = false };
        ConfirmedAbsence(rig);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public void Instance_id_is_derived_from_the_interface_path()
    {
        Assert.Equal(@"HID\VID_27C0&PID_0859&COL01\A&14BF8C3D&0&0000", TouchDigitizerRecoveryGate.InstanceIdFromInterfacePath(HidPath));
        Assert.Null(TouchDigitizerRecoveryGate.InstanceIdFromInterfacePath("garbage"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Guard_pass_outcome_carries_the_follow_up_delay_only_while_pending()
    {
        var rig = new Rig();
        var absent = Absent();
        var guard = new TouchMappingGuard(
            new Src(absent), new Reg(), new Rest(), recovery: rig.Recovery);
        var first = await guard.RunPassAsync();
        Assert.NotNull(first.RecheckAfter);
        var healthyGuard = new TouchMappingGuard(
            new Src(Working()), new Reg(), new Rest(), recovery: rig.Recovery);
        Assert.Null((await healthyGuard.RunPassAsync()).RecheckAfter);
    }

    private sealed class Src(TouchMapSnapshot s) : ITouchMapSnapshotSource
    {
        public System.Threading.Tasks.Task<TouchMapSnapshot?> GetSnapshotAsync(System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult<TouchMapSnapshot?>(s);
    }

    private sealed class Reg : IDigimonRegistryWriter
    {
        public void Write(string a, string b) { }
    }

    private sealed class Rest : ITouchDigitizerDevnodeRestarter
    {
        public bool Restart(string p) => true;
    }
}
