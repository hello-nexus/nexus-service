using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class DesktopModeExitTests
{
    private const string Mi01 = @"\\?\hid#vid_1a86&pid_ad23&mi_01#7&1&0&0000#{4d1e55b2}";

    private static UsbDeviceEntry Usb(int vid, int pid, string name = "") =>
        new() { VendorId = vid, ProductId = pid, Name = name };

    private static readonly UsbDeviceEntry Desktop = Usb(0x1A86, 0xAD23, "USB Display");
    private static readonly UsbDeviceEntry Board = Usb(0x0416, 0x8051);
    private static readonly UsbDeviceEntry Glass = Usb(0x1CBE, 0xA068);

    private sealed class Rig
    {
        public List<UsbDeviceEntry> Bus = new() { Desktop, Board };
        public List<HidDeviceInfo> HidInfos = new() { Info(Mi01, 0xFF00, 513, 513) };
        public bool? AppRunning = false;
        public bool ControlOn = true;
        public bool WriteOk = true;
        public long Now;
        public int Writes;
        public byte[]? LastReport;
        public Action? OnDelay;
        public DesktopModeExit Exit;

        public Rig()
        {
            Exit = new DesktopModeExit(
                new Hid(this), new HardwarePresence(new Usb(this)), () => AppRunning, _ => ControlOn,
                now: () => Now,
                delay: (ms, _) =>
                {
                    Now += ms;
                    OnDelay?.Invoke();
                    return Task.CompletedTask;
                });
        }

        public Task Poll() => Exit.PollAsync(CancellationToken.None);

        private sealed class Usb(Rig rig) : IUsbEnumerator
        {
            public List<UsbDeviceEntry> Enumerate() => rig.Bus.ToList();
        }

        private sealed class Hid(Rig rig) : IHidEnumerator
        {
            public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
                rig.HidInfos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

            public IReadOnlyList<HidDeviceInfo> FindAll() => rig.HidInfos;

            public IHidDevice? Open(string path, bool forInput = false) => new Device(rig);
        }

        private sealed class Device(Rig rig) : IHidDevice
        {
            public int VendorId => 0x1A86;
            public int ProductId => 0xAD23;
            public string Path => Mi01;
            public string? Serial => null;
            public int UsagePage => 0xFF00;
            public int Usage => 0;
            public bool SetFeature(ReadOnlySpan<byte> report) => false;
            public bool GetFeature(Span<byte> buffer) => false;
            public bool GetInputReport(Span<byte> buffer) => false;
            public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
            public int Read(Span<byte> buffer, int timeoutMs) => 0;
            public void Dispose() { }

            public bool Write(ReadOnlySpan<byte> report)
            {
                rig.Writes++;
                rig.LastReport = report.ToArray();
                return rig.WriteOk;
            }
        }
    }

    private static HidDeviceInfo Info(string path, int usagePage, int input, int output) => new()
    {
        VendorId = 0x1A86,
        ProductId = 0xAD23,
        Path = path,
        UsagePage = usagePage,
        InputReportByteLength = input,
        OutputReportByteLength = output,
    };

    [Fact]
    public void The_table_holds_the_curved_pair_only()
    {
        var row = Assert.Single(DesktopModeTable.Rows);
        Assert.Equal((0x1A86, 0xAD23), (row.DesktopVendorId, row.DesktopProductId));
        Assert.Equal((0x1CBE, 0xA068), (row.NormalVendorId, row.NormalProductId));
        Assert.Equal((0x0416, 0x8051), (row.CompanionVendorId, row.CompanionProductId));
        Assert.Equal((1, 0xFF00, 513), (row.InterfaceNumber, row.UsagePage, row.ReportLength));
    }

    [Fact]
    public void The_exit_report_is_id_zero_then_the_ascii_text_padded_to_the_report_length()
    {
        var report = DesktopModeTable.ExitReport(513);
        Assert.Equal(513, report.Length);
        Assert.Equal(0, report[0]);
        Assert.Equal("5f3759df", System.Text.Encoding.ASCII.GetString(report, 1, 8));
        Assert.All(report.Skip(9), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_1a86&pid_ad23&mi_01#x", 1)]
    [InlineData(@"\\?\HID#VID_1A86&PID_AD23&MI_00#x", 0)]
    [InlineData(@"\\?\hid#vid_1a86&pid_ad23#x", -1)]
    public void The_interface_number_comes_from_the_hid_path(string path, int expected) =>
        Assert.Equal(expected, DesktopModeExit.InterfaceOf(path));

    [Fact]
    public async Task A_matching_device_gets_one_write_and_the_exit_waits_for_the_normal_identity()
    {
        var rig = new Rig();
        var polls = 0;
        rig.OnDelay = () =>
        {
            if (++polls == 4)
            {
                rig.Bus = new() { Board, Glass };
            }
        };

        await rig.Poll();

        Assert.Equal(1, rig.Writes);
        Assert.Equal(DesktopModeTable.ExitReport(513), rig.LastReport);
        Assert.Equal(4, polls);
    }

    [Fact]
    public async Task The_same_arrival_is_never_attempted_twice()
    {
        var rig = new Rig();
        await rig.Poll();
        rig.Now += 60_000;
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(1, rig.Writes);
        Assert.Equal(1, rig.Exit.Attempts);
    }

    [Fact]
    public async Task A_timeout_is_bounded_and_does_not_retry()
    {
        var rig = new Rig();
        await rig.Poll();

        Assert.InRange(rig.Now, DesktopModeExit.ArrivalWaitMs, DesktopModeExit.ArrivalWaitMs + DesktopModeExit.ArrivalPollMs);
        rig.Now += 60_000;
        await rig.Poll();
        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task A_device_that_leaves_and_arrives_again_is_attempted_again_after_the_spacing()
    {
        var rig = new Rig();
        await rig.Poll();
        rig.Bus = new() { Board };
        await rig.Poll();
        rig.Now += 60_000;
        rig.Bus = new() { Desktop, Board };
        await rig.Poll();

        Assert.Equal(2, rig.Writes);
    }

    [Fact]
    public async Task A_rearrival_inside_the_spacing_is_left_alone()
    {
        var rig = new Rig();
        rig.OnDelay = () => rig.Bus = new() { Board, Glass };
        await rig.Poll();
        rig.OnDelay = null;
        rig.Bus = new() { Board };
        await rig.Poll();
        rig.Bus = new() { Desktop, Board };
        await rig.Poll();

        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task A_running_vendor_app_blocks_the_write_until_it_closes()
    {
        var rig = new Rig { AppRunning = true };
        await rig.Poll();
        await rig.Poll();
        Assert.Equal(0, rig.Writes);
        Assert.Equal(0, rig.Exit.Attempts);

        rig.AppRunning = false;
        await rig.Poll();
        await rig.Poll();
        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task A_vendor_app_close_after_an_earlier_attempt_allows_one_more()
    {
        var rig = new Rig();
        await rig.Poll();
        rig.Now += 60_000;
        rig.AppRunning = true;
        await rig.Poll();
        rig.AppRunning = false;
        await rig.Poll();

        Assert.Equal(2, rig.Writes);
    }

    [Fact]
    public async Task An_unscanned_vendor_app_state_waits()
    {
        var rig = new Rig { AppRunning = null };
        await rig.Poll();
        Assert.Equal(0, rig.Writes);

        rig.AppRunning = false;
        await rig.Poll();
        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task Nothing_is_sent_without_the_desktop_identity()
    {
        var rig = new Rig();
        rig.Bus = new() { Board, Glass };
        await rig.Poll();
        Assert.Equal(0, rig.Writes);
    }

    [Theory]
    [InlineData("control")]
    [InlineData("board")]
    [InlineData("interface")]
    [InlineData("usage-page")]
    [InlineData("input-length")]
    [InlineData("output-length")]
    [InlineData("no-hid")]
    public async Task Each_failed_gate_refuses_the_write_and_consumes_the_attempt(string failing)
    {
        var rig = new Rig();
        switch (failing)
        {
            case "control": rig.ControlOn = false; break;
            case "board": rig.Bus = new() { Desktop }; break;
            case "interface": rig.HidInfos = new() { Info(Mi01.Replace("mi_01", "mi_00"), 0xFF00, 513, 513) }; break;
            case "usage-page": rig.HidInfos = new() { Info(Mi01, 0xFF01, 513, 513) }; break;
            case "input-length": rig.HidInfos = new() { Info(Mi01, 0xFF00, 65, 513) }; break;
            case "output-length": rig.HidInfos = new() { Info(Mi01, 0xFF00, 513, 65) }; break;
            case "no-hid": rig.HidInfos = new(); break;
        }

        await rig.Poll();
        rig.Now += 60_000;
        await rig.Poll();

        Assert.Equal(0, rig.Writes);
        Assert.Equal(1, rig.Exit.Attempts);
    }

    [Fact]
    public async Task A_hid_interface_that_shows_up_late_is_waited_for_within_the_settle_window()
    {
        var rig = new Rig();
        var late = rig.HidInfos;
        rig.HidInfos = new();
        rig.OnDelay = () => rig.HidInfos = late;

        await rig.Poll();

        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task A_failed_write_does_not_wait_for_the_normal_identity()
    {
        var rig = new Rig { WriteOk = false };
        await rig.Poll();

        Assert.Equal(1, rig.Writes);
        Assert.True(rig.Now < DesktopModeExit.ArrivalWaitMs);
    }

    // ── the LED board keeps the row listed while the glass is on another identity ──

    private static BulkPanelHandler CurveHandler(BulkPanelHub hub) => new(hub, new[] { new UsbId(0x0416, 0x8051) });

    [Fact]
    public void The_board_alone_lists_the_curve_row_without_a_display()
    {
        using var hub = new BulkPanelHub(new HydroShift2CurveLcdDriver());
        var handler = CurveHandler(hub);
        var bus = new[] { Board };

        Assert.True(handler.IsConnected(bus));
        Assert.False(handler.DrivesDisplay(bus));
        Assert.Contains(handler.Identifiers, i => i.VendorId == 0x0416 && i.ProductId == 0x8051);
    }

    [Fact]
    public void The_glass_still_lists_the_row_with_its_display()
    {
        using var hub = new BulkPanelHub(new HydroShift2CurveLcdDriver());
        var handler = CurveHandler(hub);

        Assert.True(handler.IsConnected(new[] { Glass }));
        Assert.True(handler.DrivesDisplay(new[] { Glass }));
    }

    [Fact]
    public void A_desktop_mode_glass_alone_does_not_list_the_row()
    {
        using var hub = new BulkPanelHub(new HydroShift2CurveLcdDriver());
        Assert.False(CurveHandler(hub).IsConnected(new[] { Desktop }));
    }

    [Fact]
    public void A_handler_without_companions_ignores_the_board_id()
    {
        using var hub = new BulkPanelHub(new HydroShift2CurveLcdDriver());
        var handler = new BulkPanelHandler(hub);

        Assert.False(handler.IsConnected(new[] { Board }));
        Assert.DoesNotContain(handler.Identifiers, i => i.ProductId == 0x8051);
    }
}
