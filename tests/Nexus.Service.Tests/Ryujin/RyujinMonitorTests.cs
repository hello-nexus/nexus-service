using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Ryujin;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Ryujin;

public class RyujinMonitorTests
{
    private static byte[] Frame(string hex)
    {
        var report = new byte[65];
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        bytes.CopyTo(report, 0);
        return report;
    }

    // ---- exact bytes from the Armoury Crate captures ----

    private static byte[] Sensor(int slot, byte[] name, byte[] value)
    {
        var report = new byte[65];
        report[0] = 0xEC;
        report[1] = 0x5F;
        report[3] = (byte)slot;
        name.CopyTo(report, 4);
        value.CopyTo(report, 22);
        return report;
    }

    [Fact]
    public void Sensor_text_matches_the_captured_5f_frames()
    {
        // CPU "53.0" + U+2103 and GPU "40.5" + U+2103 from the startup capture, AIO Pump "3506" + U+218C, Liquid "32.0" + U+2103.
        Assert.Equal(
            Sensor(0, new byte[] { 0x43, 0x50, 0x55 }, new byte[] { 0x35, 0x33, 0x2E, 0x30, 0xE2, 0x84, 0x83 }),
            RyujinMonitorProtocol.EncodeSensorText(0, "CPU", RyujinMonitorProtocol.FormatTemperature(53.0)));
        Assert.Equal(
            Sensor(1, new byte[] { 0x47, 0x50, 0x55 }, new byte[] { 0x34, 0x30, 0x2E, 0x35, 0xE2, 0x84, 0x83 }),
            RyujinMonitorProtocol.EncodeSensorText(1, "GPU", RyujinMonitorProtocol.FormatTemperature(40.5)));
        Assert.Equal(
            Sensor(2, new byte[] { 0x41, 0x49, 0x4F, 0x20, 0x50, 0x75, 0x6D, 0x70 }, new byte[] { 0x33, 0x35, 0x30, 0x36, 0xE2, 0x86, 0x8C }),
            RyujinMonitorProtocol.EncodeSensorText(2, "AIO Pump", RyujinMonitorProtocol.FormatRpm(3506)));
        Assert.Equal(
            Sensor(2, new byte[] { 0x4C, 0x69, 0x71, 0x75, 0x69, 0x64 }, new byte[] { 0x33, 0x32, 0x2E, 0x30, 0xE2, 0x84, 0x83 }),
            RyujinMonitorProtocol.EncodeSensorText(2, "Liquid", RyujinMonitorProtocol.FormatTemperature(32.0)));
    }

    [Fact]
    public void Display_report_matches_the_captured_5c_frames()
    {
        Assert.Equal(Frame("EC 5C 01 00 00 28 00 00 00 28 14 00 00 00 00 28"), RyujinMonitorProtocol.EncodeDisplay(0, 40));
        Assert.Equal(Frame("EC 5C 01 5A 00 28 00 00 00 28 14 00 00 00 00 28"), RyujinMonitorProtocol.EncodeDisplay(90, 40));
        Assert.Equal(Frame("EC 5C 01 5A 00 64 00 00 00 64 14 00 00 00 00 64"), RyujinMonitorProtocol.EncodeDisplay(90, 100));
    }

    [Fact]
    public void Brightness_burst_is_the_captured_three_packets_each_behind_a_dc_report()
    {
        var burst = RyujinMonitorProtocol.EncodeBrightnessBurst(90, 0x28, 0x64);

        Assert.Equal(6, burst.Length);
        Assert.Equal(Frame("EC DC"), burst[0]);
        Assert.Equal(Frame("EC 5C 01 5A 00 28 00 00 00 28 14 00 00 00 00 28 14"), burst[1]);
        Assert.Equal(Frame("EC DC"), burst[2]);
        Assert.Equal(Frame("EC 5C 01 5A 00 28 00 00 00 28 14"), burst[3]);
        Assert.Equal(Frame("EC DC"), burst[4]);
        Assert.Equal(Frame("EC 5C 01 5A 00 64 00 00 00 64 14 00 00 00 00 64"), burst[5]);
    }

    [Fact]
    public void Layout_select_is_the_captured_5d_then_51()
    {
        var select = RyujinMonitorProtocol.EncodeSelectMonitorLayout();

        Assert.Equal(Frame("EC 5D 00 01 21 00 01 00 00 05"), select[0]);
        Assert.Equal(Frame("EC 51 1F 00 00 00"), select[1]);
    }

    [Fact]
    public void Colors_report_is_the_captured_5e()
    {
        Assert.Equal(
            Frame("EC 5E 00 00 02 00 01 00 FF FF FF 00 FF FF FF 00 FF FF FF 00 FF FF FF 00 FF FF FF 00 FF FF FF"),
            RyujinMonitorProtocol.EncodeColors());
    }

    [Fact]
    public void Clock_is_bcd_in_the_captured_order()
    {
        // Friday 13 November, 17:43:07 local.
        Assert.Equal(Frame("EC 11 C6 11 13 05 00 17 43 07 01"), RyujinMonitorProtocol.EncodeClock(new DateTime(2026, 11, 13, 17, 43, 7)));
        // Sunday has no capture; Monday to Sunday is 1 to 7.
        Assert.Equal(7, RyujinMonitorProtocol.EncodeClock(new DateTime(2026, 11, 15, 8, 5, 59))[5]);
    }

    [Fact]
    public void Startup_is_reset_dc_dc_clock_colors_layout_the_stored_display_then_the_firmware_query()
    {
        var startup = RyujinMonitorProtocol.EncodeStartup(new DateTime(2026, 11, 13, 17, 43, 7), 90, 70);

        Assert.Equal(new byte[] { 0xD0, 0xDC, 0xDC, 0xDC, 0x5C, 0xDC, 0xDC, 0x11, 0x5E, 0x5D, 0x51, 0x82 }, startup.Select(f => f[1]).ToArray());
        Assert.Equal(RyujinMonitorProtocol.EncodeDisplay(90, 70), startup[4]);
        Assert.All(startup, f => Assert.Equal(0xEC, f[0]));
        Assert.Equal(RyujinMonitorProtocol.ResetGapMs, RyujinMonitorProtocol.GapAfterMs(0xD0));
    }

    [Fact]
    public void A_rotation_change_resends_the_settings_then_the_clock_and_layout()
    {
        var frames = RyujinMonitorProtocol.EncodeRotationChange(new DateTime(2026, 11, 13, 17, 43, 7), 90, 40);

        Assert.Equal(new byte[] { 0xDC, 0x5C, 0xDC, 0xDC, 0x11, 0x5E, 0x5D, 0x51 }, frames.Select(f => f[1]).ToArray());
        Assert.Equal(0x5A, frames[1][3]);
    }

    private static readonly RyujinReadings Full = new(45.5f, 40.0f, 31.2f, 3500, 1200);

    [Fact]
    public void Missing_readings_leave_their_slot_unsent_and_values_are_not_invented()
    {
        var all = RyujinMonitorFrames.Build(3, RyujinMonitorSensors.DefaultSlots, Full);
        Assert.Equal(new byte[] { 0, 1, 2 }, all.Select(f => f[3]).ToArray());

        var some = RyujinMonitorFrames.Build(3, RyujinMonitorSensors.DefaultSlots, new RyujinReadings(null, float.NaN, null, 3500, null));
        Assert.Equal(new byte[] { 2 }, some.Select(f => f[3]).ToArray());
        Assert.Empty(RyujinMonitorFrames.Build(3, RyujinMonitorSensors.DefaultSlots, default));
    }

    [Theory]
    [InlineData("liquid-temp", "Liquid", "31.2℃")]
    [InlineData("fan-rpm", "AIO Fan", "1200↌")]
    [InlineData("pump-rpm", "AIO Pump", "3500↌")]
    [InlineData("cpu-temp", "CPU", "45.5℃")]
    [InlineData("gpu-temp", "GPU", "40.0℃")]
    public void Each_sensor_id_is_sent_with_its_name_and_unit_glyph(string id, string name, string value)
    {
        var frames = RyujinMonitorFrames.Build(3, new[] { "cpu-temp", "gpu-temp", id }, Full);

        Assert.Equal(Sensor(2, System.Text.Encoding.UTF8.GetBytes(name), System.Text.Encoding.UTF8.GetBytes(value)), frames[2]);
    }

    [Fact]
    public void Two_sensors_send_only_slots_zero_and_one_from_the_chosen_sources()
    {
        var frames = RyujinMonitorFrames.Build(2, new[] { "fan-rpm", "liquid-temp", "cpu-temp" }, Full);

        Assert.Equal(new byte[] { 0, 1 }, frames.Select(f => f[3]).ToArray());
        Assert.Equal(Sensor(0, System.Text.Encoding.UTF8.GetBytes("AIO Fan"), System.Text.Encoding.UTF8.GetBytes("1200↌")), frames[0]);
        Assert.Equal(Sensor(1, System.Text.Encoding.UTF8.GetBytes("Liquid"), System.Text.Encoding.UTF8.GetBytes("31.2℃")), frames[1]);
    }

    [Fact]
    public void Colors_report_count_byte_is_the_sensor_count_less_one()
    {
        Assert.Equal(2, RyujinMonitorProtocol.EncodeColors(3)[4]);
        Assert.Equal(1, RyujinMonitorProtocol.EncodeColors(2)[4]);
        Assert.Equal(2, RyujinMonitorProtocol.EncodeColors()[4]);
        Assert.Equal(1, RyujinMonitorProtocol.EncodeRotationChange(DateTime.Now, 0, 40, 2).Single(f => f[1] == 0x5E)[4]);
        Assert.Equal(1, RyujinMonitorProtocol.EncodeStartup(DateTime.Now, 0, 40, 2).Single(f => f[1] == 0x5E)[4]);
    }

    // ---- display route: validation, partial update, persistence ----

    [Fact]
    public void Display_state_defaults_to_three_sensors_and_lists_the_available_ids()
    {
        var state = Nexus.Service.Routes.DevicesRoutes.BuildRyujinDisplayState(new Nexus.Service.Persistence.RyujinDisplaySettings(), true);

        Assert.Equal(3, state.SensorCount);
        Assert.Equal(new[] { "cpu-temp", "gpu-temp", "pump-rpm" }, state.Slots);
        Assert.Equal(new[] { "cpu-temp", "gpu-temp", "liquid-temp", "pump-rpm", "fan-rpm" }, state.AvailableSensors);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(null, "cpu-temp,gpu-temp")]
    [InlineData(null, "cpu-temp,gpu-temp,pump-rpm,fan-rpm")]
    [InlineData(null, "cpu-temp,gpu-temp,vram-temp")]
    [InlineData(null, "cpu-temp,gpu-temp,")]
    public void Bad_sensor_values_are_rejected(int? count, string? slots)
    {
        var body = new Nexus.Service.Routes.RyujinDisplayRequest { SensorCount = count, Slots = slots?.Split(',').ToList() };

        Assert.NotNull(Nexus.Service.Routes.DevicesRoutes.ValidateRyujinDisplay(body));
    }

    [Fact]
    public void A_partial_update_keeps_the_other_fields_and_persists_through_a_round_trip()
    {
        var settings = new Nexus.Service.Persistence.NexusSettings();
        var d = settings.Devices.RyujinDisplay;
        d.Brightness = 70;
        d.Rotation = 90;
        var body = new Nexus.Service.Routes.RyujinDisplayRequest { SensorCount = 2, Slots = new() { "liquid-temp", "fan-rpm", "cpu-temp" } };
        Assert.Null(Nexus.Service.Routes.DevicesRoutes.ValidateRyujinDisplay(body));
        Nexus.Service.Routes.DevicesRoutes.ApplyRyujinDisplay(d, body);

        var json = System.Text.Json.JsonSerializer.Serialize(settings, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!.Devices.RyujinDisplay;

        Assert.Equal(70, back.Brightness);
        Assert.Equal(90, back.Rotation);
        Assert.Equal(2, back.SensorCount);
        Assert.Equal(new[] { "liquid-temp", "fan-rpm", "cpu-temp" }, back.Slots);

        Nexus.Service.Routes.DevicesRoutes.ApplyRyujinDisplay(back, new Nexus.Service.Routes.RyujinDisplayRequest { Brightness = 50 });
        Assert.Equal(2, back.SensorCount);
        Assert.Equal(new[] { "liquid-temp", "fan-rpm", "cpu-temp" }, back.Slots);
    }

    [Fact]
    public void A_stored_slot_list_that_is_short_or_unknown_falls_back_per_slot()
    {
        Assert.Equal(new[] { "fan-rpm", "gpu-temp", "pump-rpm" }, RyujinMonitorSensors.NormalizeSlots(new[] { "fan-rpm", "bogus" }));
        Assert.Equal(3, RyujinMonitorSensors.NormalizeCount(7));
    }

    [Fact]
    public async Task Changing_the_sensors_resends_colors_and_layout_and_the_next_cycle_sends_only_the_chosen_slots()
    {
        var fake = new RyujinDeviceFake();
        var store = new InMemoryConfigStore();
        var (worker, gate, hub) = BuildWorker(new FakeEnumerator(fake), store);
        gate.SetEnabled("asus-ryujin-extreme", true);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Any(w => w[1] == 0x82), 8000));
        Assert.Equal(2, fake.Writes.ToArray().Single(w => w[1] == 0x5E)[4]);

        store.Update(s =>
        {
            s.Devices.RyujinDisplay.SensorCount = 2;
            s.Devices.RyujinDisplay.Slots = new() { "liquid-temp", "fan-rpm", "cpu-temp" };
        });
        hub.Wake.Release();
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Count(w => w[1] == 0x5E) == 2, 5000));
        await worker.StopAsync(CancellationToken.None);

        var writes = fake.Writes.ToArray();
        Assert.Equal(1, writes.Last(w => w[1] == 0x5E)[4]);
        Assert.Equal(2, writes.Count(w => w[1] == 0x5D));
        Assert.Equal(2, writes.Count(w => w[1] == 0x51 && w[2] == 0x1F));
        Assert.Equal(2, writes.Count(w => w[1] == 0x5C));
    }

    // ---- no live stream to the Extreme, the 360 unchanged ----

    [Fact]
    public void The_panel_driver_binds_only_the_360_so_the_extreme_gets_no_bulk_traffic()
    {
        var driver = new RyujinPanelDriver();

        Assert.Equal(new[] { 0x1AA2 }, driver.ProductIds);
        Assert.Equal("asus-ryujin-lcd", driver.HandlerId);
        Assert.Equal(320, RyujinProtocol.Width);
        Assert.Equal(new byte[] { 0xEC, 0x7F, 0x03, 0x00, 0x84, 0x03 }, RyujinProtocol.EncodeCommit()[0..6]);
        Assert.Equal(3, RyujinProtocol.EncodeLiveStart().Length);
    }

    // ---- Nexus Control gates every write ----

    [Fact]
    public async Task Nexus_control_off_opens_nothing_and_sends_nothing()
    {
        var fake = new RyujinDeviceFake();
        var enumerator = new FakeEnumerator(fake);
        var (worker, gate, _) = BuildWorker(enumerator);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => worker.GateChecks >= 1, 8000));
        await worker.StopAsync(CancellationToken.None);

        Assert.False(gate.IsEnabled("asus-ryujin-extreme"));
        Assert.Equal(0, enumerator.Opens);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task Turning_nexus_control_off_stops_all_writes_at_once()
    {
        var fake = new RyujinDeviceFake();
        var enumerator = new FakeEnumerator(fake);
        var (worker, gate, hub) = BuildWorker(enumerator);
        gate.SetEnabled("asus-ryujin-extreme", true);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Any(w => w[1] == 0x82), 8000));
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Any(w => w[1] == 0x99), 8000));

        gate.SetEnabled("asus-ryujin-extreme", false);
        // The session detaches last, after any release write, so nothing can follow it.
        Assert.True(SpinWait.SpinUntil(() => !hub.IsConnected, 8000));
        int after = fake.Writes.Count;
        // The worker loops back to its gate check once the session is over; a write after that would show.
        int checks = worker.GateChecks;
        Assert.True(SpinWait.SpinUntil(() => worker.GateChecks > checks, 8000));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(after, fake.Writes.Count);
        var order = fake.Writes.ToArray().Select(w => w[1]).ToArray();
        Assert.Equal(0xD0, order[0]);
        Assert.All(order, h => Assert.Contains(h, new byte[] { 0xD0, 0xDC, 0x11, 0x5E, 0x5D, 0x51, 0x5C, 0x82, 0x99, 0x5F, 0x9A, 0x1A }));
    }

    [Fact]
    public async Task The_stored_display_settings_are_sent_at_startup_even_when_changed_while_away()
    {
        var fake = new RyujinDeviceFake();
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.RyujinDisplay.Brightness = 70;
            s.Devices.RyujinDisplay.Rotation = 90;
        });
        var (worker, gate, _) = BuildWorker(new FakeEnumerator(fake), store);
        gate.SetEnabled("asus-ryujin-extreme", true);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Any(w => w[1] == 0x82), 8000));
        await worker.StopAsync(CancellationToken.None);

        var display = fake.Writes.ToArray().Single(w => w[1] == 0x5C);
        Assert.Equal(RyujinMonitorProtocol.EncodeDisplay(90, 70), display);
    }

    [Fact]
    public async Task A_brightness_change_sends_the_burst_once_and_a_rotation_change_reselects_the_layout()
    {
        var fake = new RyujinDeviceFake();
        var store = new InMemoryConfigStore();
        var (worker, gate, hub) = BuildWorker(new FakeEnumerator(fake), store);
        gate.SetEnabled("asus-ryujin-extreme", true);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Any(w => w[1] == 0x82), 8000));
        Assert.Single(fake.Writes.ToArray(), w => w[1] == 0x5C);

        store.Update(s => s.Devices.RyujinDisplay.Brightness = 100);
        hub.Wake.Release();
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Count(w => w[1] == 0x5C) == 4, 5000));
        Assert.Equal(0x64, fake.Writes.ToArray().Last(w => w[1] == 0x5C)[5]);
        Assert.Equal(0x28, fake.Writes.ToArray().First(w => w[1] == 0x5C)[5]);

        store.Update(s => s.Devices.RyujinDisplay.Rotation = 90);
        hub.Wake.Release();
        Assert.True(SpinWait.SpinUntil(() => fake.Writes.ToArray().Count(w => w[1] == 0x5C) == 5, 5000));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0x5A, fake.Writes.ToArray().Last(w => w[1] == 0x5C)[3]);
        Assert.Equal(4, fake.Writes.ToArray().Count(w => w[1] == 0x5C && w[3] == 0x00));
    }

    private static (RyujinCoolingWorker worker, DeviceControlGate gate, RyujinCoolingHub hub) BuildWorker(FakeEnumerator enumerator, InMemoryConfigStore? existing = null)
    {
        var store = existing ?? new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var hub = new RyujinCoolingHub();
        var worker = new RyujinCoolingWorker(
            enumerator, hub, new RyujinCoolingProvider(hub), gate,
            new HardwarePresence(new PresentUsb()), store, new StubSensorProvider());
        return (worker, gate, hub);
    }

    private sealed class PresentUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new() { new UsbDeviceEntry { VendorId = 0x0B05, ProductId = 0x1BCB } };
    }

    private sealed class FakeEnumerator : IHidEnumerator
    {
        private readonly IHidDevice _device;

        public FakeEnumerator(IHidDevice device)
        {
            _device = device;
        }

        public int Opens { get; private set; }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => new[]
        {
            new HidDeviceInfo { VendorId = vendorId, ProductId = productId, Path = "fake", InputReportByteLength = 65, OutputReportByteLength = 65 },
        };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Find(0x0B05, 0x1BCB);

        public IHidDevice? Open(string path, bool forInput = false)
        {
            Opens++;
            return _device;
        }
    }
}
