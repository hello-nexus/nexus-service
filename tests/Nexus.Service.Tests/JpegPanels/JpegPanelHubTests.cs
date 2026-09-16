using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

public class JpegPanelHubTests
{
    [Fact]
    public void Attach_runs_the_models_init_sequence_padded_to_the_report_length()
    {
        var model = JpegPanelModel.IdCoolingFx;
        using var hub = new JpegPanelHub(model);
        var device = new RecordingHidDevice();

        Assert.True(hub.Attach(device));

        Assert.Equal(2, device.Writes.Count);
        Assert.All(device.Writes, w => Assert.Equal(model.ReportLength, w.Length));
        // "CRT" + "DIS" turns the panel on; "LIG" sets the backlight to 0x19.
        Assert.Equal(new byte[] { 0x00, 0x43, 0x52, 0x54, 0x00, 0x00, 0x44, 0x49, 0x53 }, device.Writes[0][..9]);
        Assert.Equal(0x19, device.Writes[1][11]);
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void A_model_with_no_init_sequence_attaches_without_writing()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.CorsairXc7);
        var device = new RecordingHidDevice();

        Assert.True(hub.Attach(device));

        Assert.Empty(device.Writes);
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void A_rejected_init_report_drops_the_handle_instead_of_reporting_connected()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.IdCoolingFx);
        var device = new RecordingHidDevice { FailWritesFrom = 0 };

        Assert.False(hub.Attach(device));

        Assert.False(hub.IsConnected);
        Assert.True(device.Disposed);
    }

    [Fact]
    public void Detach_sends_the_shutdown_sequence_and_disposes_the_handle()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.IdCoolingFx);
        var device = new RecordingHidDevice();
        hub.Attach(device);
        device.Writes.Clear();

        hub.Detach();

        Assert.Equal(2, device.Writes.Count);
        // "CLE" clears, "HAN" hands the panel back to the cooler's own firmware.
        Assert.Equal(new byte[] { 0x43, 0x4C, 0x45 }, device.Writes[0][6..9]);
        Assert.Equal(new byte[] { 0x48, 0x41, 0x4E }, device.Writes[1][6..9]);
        Assert.False(hub.IsConnected);
        Assert.True(device.Disposed);
    }

    [Fact]
    public void A_frame_goes_out_as_whole_reports_that_reassemble_to_the_jpeg()
    {
        var model = JpegPanelModel.GalahadIiLcd;
        using var hub = new JpegPanelHub(model);
        var device = new RecordingHidDevice();
        hub.Attach(device);
        device.Writes.Clear();
        var jpeg = Enumerable.Range(0, 2500).Select(i => (byte)(i % 251)).ToArray();

        Assert.True(hub.SendFrame(jpeg));

        Assert.Equal(3, device.Writes.Count);
        Assert.All(device.Writes, w => Assert.Equal(model.ReportLength, w.Length));

        var reassembled = new List<byte>();
        for (int i = 0; i < device.Writes.Count; i++)
        {
            var header = JpegPanelProtocol.HeaderLength(model.HeaderStyle, isFirstChunk: i == 0);
            var declared = (device.Writes[i][9] << 8) | device.Writes[i][10];
            reassembled.AddRange(device.Writes[i].Skip(header).Take(declared));
        }
        Assert.Equal(jpeg, reassembled);
    }

    [Fact]
    public void A_rejected_chunk_stops_the_frame_rather_than_writing_the_rest()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.GalahadIiLcd);
        var device = new RecordingHidDevice();
        hub.Attach(device);
        device.Writes.Clear();
        device.FailWritesFrom = 1;

        Assert.False(hub.SendFrame(Enumerable.Repeat((byte)0xAB, 4000).ToArray()));

        // The first chunk landed, the second was refused, and nothing after it was tried.
        Assert.Equal(2, device.Writes.Count);
    }

    [Fact]
    public void Sending_without_a_device_fails_instead_of_throwing()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.CorsairXc7);

        Assert.False(hub.SendFrame(new byte[128]));
    }

    [Fact]
    public void An_empty_frame_is_refused()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.CorsairXc7);
        hub.Attach(new RecordingHidDevice());

        Assert.False(hub.SendFrame(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Both_lian_li_aio_panels_report_a_settable_backlight()
    {
        Assert.True(JpegPanelModel.GalahadIiLcd.SupportsBrightness);
        Assert.True(JpegPanelModel.HydroShiftLcd.SupportsBrightness);
        Assert.False(JpegPanelModel.CorsairXc7.SupportsBrightness);
    }

    // The model table's handshakes are process-wide singletons, so a test that sets a
    // backlight takes its own instance rather than leaving one dimmed for the whole suite.
    private static JpegPanelModel Dimmable() =>
        JpegPanelModel.GalahadIiLcd with { Handshake = new LianLiAioHandshake("test-lcd", 24) };

    [Fact]
    public void SetBrightness_reaches_an_attached_panel_in_lcd_setting_mode()
    {
        using var hub = new JpegPanelHub(Dimmable());
        var device = new RecordingHidDevice();
        hub.Attach(device);
        device.Writes.Clear();

        Assert.True(hub.SetBrightness(35));

        var control = Assert.Single(device.Writes);
        Assert.Equal(0x0C, control[1]);
        Assert.Equal(LianLiAioHandshake.LcdSettingMode, control[11]);
        Assert.Equal(35, control[12]);
    }

    [Fact]
    public void An_out_of_range_backlight_clamps_to_the_ends_of_the_scale()
    {
        using var hub = new JpegPanelHub(Dimmable());
        var device = new RecordingHidDevice();
        hub.Attach(device);
        device.Writes.Clear();

        hub.SetBrightness(-5);
        Assert.Equal(0, device.Writes[^1][12]);

        hub.SetBrightness(140);
        Assert.Equal(100, device.Writes[^1][12]);
    }

    [Fact]
    public void A_brightness_set_while_detached_is_recorded_and_re_asserted_on_attach()
    {
        using var hub = new JpegPanelHub(Dimmable());

        Assert.False(hub.SetBrightness(20));

        var device = new RecordingHidDevice();
        Assert.True(hub.Attach(device));
        // Writes are the firmware read then the application-mode claim carrying the value.
        Assert.Equal(20, device.Writes[1][12]);
        Assert.Equal(20, hub.Brightness);
    }

    [Fact]
    public void A_panel_with_no_backlight_command_refuses_the_setting()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.CorsairXc7);
        var device = new RecordingHidDevice();
        hub.Attach(device);

        Assert.False(hub.SetBrightness(40));

        Assert.Empty(device.Writes);
    }

    // ── model table + control policy ──

    [Fact]
    public void Every_model_has_a_distinct_handler_id_and_a_real_panel_size()
    {
        var ids = JpegPanelModel.All.Select(m => m.HandlerId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(JpegPanelModel.All, m =>
        {
            Assert.True(m.Width > 0 && m.Height > 0);
            Assert.True(m.ReportLength > 64);
            Assert.NotEmpty(m.ProductIds);
        });
    }

    [Theory]
    [InlineData(0x0416, 0x7395, "lianli-galahad2-lcd")]
    [InlineData(0x0416, 0x7398, "lianli-hydroshift-lcd")]
    [InlineData(0x0416, 0x7399, "lianli-hydroshift-lcd")]
    [InlineData(0x0416, 0x739A, "lianli-hydroshift-lcd")]
    [InlineData(0x1B1C, 0x0C42, "corsair-xc7-lcd")]
    [InlineData(0x1B1C, 0x0C39, "corsair-capellix-lcd")]
    [InlineData(0x1B1C, 0x0C33, "corsair-capellix-lcd")]
    [InlineData(0x2000, 0x3000, "idcooling-fx-lcd")]
    public void Find_resolves_each_supported_usb_id(int vid, int pid, string handlerId)
    {
        Assert.Equal(handlerId, JpegPanelModel.Find(vid, pid)?.HandlerId);
    }

    [Fact]
    public void The_corsair_link_lcd_is_not_claimed_here_because_another_driver_owns_it()
    {
        // 0x0C4E and 0x0C43 belong to CorsairLinkLcd; a second claim on the same HID
        // interface would fight it.
        Assert.Null(JpegPanelModel.Find(0x1B1C, 0x0C4E));
        Assert.Null(JpegPanelModel.Find(0x1B1C, 0x0C43));
    }

    /// <summary>
    /// A build must never claim one of these coolers on its own, hardware-verified or not.
    /// This is the guard on that promise.
    /// </summary>
    [Fact]
    public void Every_model_defaults_to_nexus_control_off_and_reads_as_experimental()
    {
        Assert.All(JpegPanelModel.All, m =>
        {
            Assert.False(DeviceControlPolicy.DefaultOn(m.HandlerId), $"{m.HandlerId} must default off");
            Assert.True(DeviceControlPolicy.IsExperimental(m.HandlerId), $"{m.HandlerId} must be experimental");
        });
    }

    private sealed class RecordingHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int? FailWritesFrom { get; set; }
        public bool Disposed { get; private set; }

        public int VendorId => 0x0416;
        public int ProductId => 0x7395;
        public string Path => "/dev/fake-jpeg-panel";
        public string? Serial => "FAKE-SERIAL";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            if (FailWritesFrom is { } from && Writes.Count >= from)
            {
                Writes.Add(report.ToArray());
                return false;
            }
            Writes.Add(report.ToArray());
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() => Disposed = true;
    }
}
