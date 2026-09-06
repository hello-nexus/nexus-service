using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Ibp;
using Xunit;

namespace Nexus.Service.Tests.Ibp;

/// <summary>
/// Hub mechanics on a scripted HID layer: collection selection by feature
/// length, the software-mode handshake before the first frame, the firmware
/// handback on release / gate-off, and the drop-after-N-failures rule.
/// </summary>
public class IbpPeripheralHubTests
{
    private sealed class ScriptedHidDevice : IHidDevice
    {
        public ScriptedHidDevice(HidDeviceInfo info) { Info = info; }
        public HidDeviceInfo Info { get; }
        public List<byte[]> Features { get; } = new();
        public bool FailSetFeature { get; set; }
        public bool Disposed { get; private set; }

        public int VendorId => Info.VendorId;
        public int ProductId => Info.ProductId;
        public string Path => Info.Path;
        public string? Serial => Info.Serial;
        public int UsagePage => Info.UsagePage;
        public int Usage => Info.Usage;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            if (FailSetFeature) return false;
            Features.Add(report.ToArray());
            return true;
        }
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptedEnumerator : IHidEnumerator
    {
        public List<HidDeviceInfo> Infos { get; } = new();
        public Dictionary<string, ScriptedHidDevice> Opened { get; } = new();
        public HashSet<string> RefuseOpen { get; } = new();
        public int FindAllCalls { get; private set; }

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            Infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

        public IReadOnlyList<HidDeviceInfo> FindAll()
        {
            FindAllCalls++;
            return Infos.ToList();
        }

        public IHidDevice? Open(string path, bool forInput = false)
        {
            if (RefuseOpen.Contains(path)) return null;
            var info = Infos.First(i => i.Path == path);
            var dev = new ScriptedHidDevice(info);
            Opened[path] = dev;
            return dev;
        }

        /// <summary>A composite unit: boot collection (no feature report) + the vendor collection.</summary>
        public void AddUnit(IbpPeripheralModel model, string serial)
        {
            Infos.Add(new HidDeviceInfo
            {
                VendorId = IbpPeripheralProtocol.VendorId, ProductId = model.ProductId,
                Path = $"{model.Id}-{serial}-boot", Serial = serial, UsagePage = 0x01, Usage = 0x06,
                InputReportByteLength = 9, FeatureReportByteLength = 0,
            });
            Infos.Add(new HidDeviceInfo
            {
                VendorId = IbpPeripheralProtocol.VendorId, ProductId = model.ProductId,
                Path = $"{model.Id}-{serial}-vendor", Serial = serial, UsagePage = 0xFF00, Usage = 0x01,
                FeatureReportByteLength = model.ReportLength,
            });
        }
    }

    private static readonly IReadOnlySet<IbpPeripheralModel> AllowAll = new HashSet<IbpPeripheralModel>(IbpPeripheralProtocol.Models);
    private static readonly IReadOnlySet<IbpPeripheralModel> MiceOnly =
        new HashSet<IbpPeripheralModel>(IbpPeripheralProtocol.Models.Where(m => m.Kind == IbpPeripheralKind.Mouse));

    [Fact]
    public void Reconcile_opens_only_the_collection_whose_feature_length_matches()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        hid.AddUnit(IbpPeripheralProtocol.Km7Mouse, "MS1");
        // Keeb TKL on the same VID: not ours.
        hid.Infos.Add(new HidDeviceInfo { VendorId = 0x3402, ProductId = 0x0300, Path = "keeb", FeatureReportByteLength = 9 });
        using var hub = new IbpPeripheralHub(hid);

        Assert.True(hub.Reconcile(AllowAll));
        Assert.Equal(2, hub.Attached.Count);
        Assert.True(hub.IsConnected);
        Assert.Equal(new[] { "km7-keyboard-KB1-vendor", "km7-mouse-MS1-vendor" }, hid.Opened.Keys.OrderBy(k => k));
        var kb = hub.Attached.Single(a => a.Model == IbpPeripheralProtocol.Km7Keyboard);
        Assert.Equal("ibp:km7-keyboard:KB1", kb.DeviceId);
        Assert.Equal("KB1", kb.Serial);

        // Steady state: nothing new, no change reported, handles kept.
        Assert.False(hub.Reconcile(AllowAll));
        Assert.Equal(2, hub.Attached.Count);
        Assert.Equal(2, hid.FindAllCalls);
    }

    [Fact]
    public void Reconcile_opens_one_slot_per_unit_when_two_collections_match()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        // A second collection on the same unit that happens to carry a 33-byte feature report.
        hid.Infos.Add(new HidDeviceInfo
        {
            VendorId = 0x3402, ProductId = 0x0301, Path = "km7-keyboard-KB1-other", Serial = "KB1",
            UsagePage = 0xFF01, Usage = 0x02, FeatureReportByteLength = 33,
        });
        using var hub = new IbpPeripheralHub(hid);
        Assert.True(hub.Reconcile(AllowAll));
        Assert.Single(hub.Attached);
        Assert.Single(hid.Opened);
        Assert.False(hub.Reconcile(AllowAll));
        Assert.Single(hid.Opened);
    }

    [Fact]
    public void Reconcile_without_a_serial_derives_a_stable_id_from_the_path()
    {
        var hid = new ScriptedEnumerator();
        hid.Infos.Add(new HidDeviceInfo
        {
            VendorId = 0x3402, ProductId = 0x0303, Path = @"\\?\hid#vid_3402&pid_0303&mi_01#8&abc#{guid}",
            FeatureReportByteLength = 520,
        });
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var id1 = hub.Attached.Single().DeviceId;
        Assert.StartsWith("ibp:mk9-keyboard:p-", id1);
        hub.Disconnect();
        hub.Reconcile(AllowAll);
        Assert.Equal(id1, hub.Attached.Single().DeviceId);
    }

    [Fact]
    public void First_frame_takes_the_leds_then_streams_every_report()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var id = hub.Attached.Single().DeviceId;
        var dev = hid.Opened["km7-keyboard-KB1-vendor"];

        var leds = new RgbColor[24];
        leds[0] = new RgbColor(10, 20, 30);
        Assert.True(hub.WriteFrame(id, leds));
        // software-mode report + 3 frame reports
        Assert.Equal(4, dev.Features.Count);
        Assert.Equal(new byte[] { 0x08, 0x09, 0xF9, 0x00 }, dev.Features[0].AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x08, 0x08, 0xF8, 0x01, 0, 0, 10, 20, 30 }, dev.Features[1].AsSpan(0, 9).ToArray());
        Assert.Equal(0x02, dev.Features[2][3]);
        Assert.Equal(0x03, dev.Features[3][3]);
        Assert.All(dev.Features, f => Assert.Equal(33, f.Length));

        // Second frame: no handshake again.
        Assert.True(hub.WriteFrame(id, leds));
        Assert.Equal(7, dev.Features.Count);

        // Unknown id is a clean false.
        Assert.False(hub.WriteFrame("ibp:km7-keyboard:NOPE", leds));
    }

    [Fact]
    public void Release_hands_back_to_firmware_once_and_the_next_frame_retakes()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Mouse, "MS1");
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var id = hub.Attached.Single().DeviceId;
        var dev = hid.Opened["km7-mouse-MS1-vendor"];

        // Nothing to release before the host ever took the LEDs.
        hub.ReleaseToFirmware(id);
        hub.ReleaseAllToFirmware();
        Assert.Empty(dev.Features);

        hub.WriteFrame(id, new RgbColor[6]);
        Assert.Equal(2, dev.Features.Count);
        hub.ReleaseToFirmware(id);
        hub.ReleaseToFirmware(id);
        Assert.Equal(3, dev.Features.Count);
        Assert.Equal(new byte[] { 0x08, 0x2D, 0x01 }, dev.Features[2].AsSpan(0, 3).ToArray());

        hub.WriteFrame(id, new RgbColor[6]);
        Assert.Equal(new byte[] { 0x08, 0x2D, 0x00 }, dev.Features[3].AsSpan(0, 3).ToArray());
        Assert.Equal(5, dev.Features.Count);
    }

    [Fact]
    public void Mk9_has_no_handshake_so_release_sends_nothing()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Mk9Keyboard, "MK1");
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var id = hub.Attached.Single().DeviceId;
        var dev = hid.Opened["mk9-keyboard-MK1-vendor"];
        hub.WriteFrame(id, new RgbColor[103]);
        Assert.Single(dev.Features);
        Assert.Equal(520, dev.Features[0].Length);
        hub.ReleaseAllToFirmware();
        Assert.Single(dev.Features);
    }

    [Fact]
    public void Disallowed_units_are_released_and_closed_by_reconcile()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        hid.AddUnit(IbpPeripheralProtocol.Km7Mouse, "MS1");
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var kbId = hub.Attached.Single(a => a.Model.Kind == IbpPeripheralKind.Keyboard).DeviceId;
        hub.WriteFrame(kbId, new RgbColor[24]);
        var kb = hid.Opened["km7-keyboard-KB1-vendor"];
        var before = hub.Version;

        // Keyboard gate off: keyboard handed back + closed, mouse untouched.
        Assert.True(hub.Reconcile(MiceOnly));
        Assert.Single(hub.Attached);
        Assert.Equal(IbpPeripheralKind.Mouse, hub.Attached[0].Model.Kind);
        Assert.True(kb.Disposed);
        Assert.Equal(new byte[] { 0x08, 0x09, 0xF9, 0x01 }, kb.Features[^1].AsSpan(0, 4).ToArray());
        Assert.NotEqual(before, hub.Version);
        Assert.False(hid.Opened["km7-mouse-MS1-vendor"].Disposed);

        // Gate back on: reopened on a fresh handle.
        Assert.True(hub.Reconcile(AllowAll));
        Assert.Equal(2, hub.Attached.Count);
    }

    [Fact]
    public void Five_consecutive_write_failures_drop_the_unit()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km10Mouse, "MS1");
        using var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        var id = hub.Attached.Single().DeviceId;
        var dev = hid.Opened["km10-mouse-MS1-vendor"];
        dev.FailSetFeature = true;
        for (var i = 0; i < 4; i++)
        {
            Assert.False(hub.WriteFrame(id, new RgbColor[3]));
            Assert.True(hub.IsConnected);
        }
        Assert.False(hub.WriteFrame(id, new RgbColor[3]));
        Assert.False(hub.IsConnected);
        Assert.True(dev.Disposed);
        // One success in between resets the count.
        hub.Reconcile(AllowAll);
        var dev2 = hid.Opened["km10-mouse-MS1-vendor"];
        dev2.FailSetFeature = true;
        for (var i = 0; i < 4; i++) hub.WriteFrame(id, new RgbColor[3]);
        dev2.FailSetFeature = false;
        Assert.True(hub.WriteFrame(id, new RgbColor[3]));
        dev2.FailSetFeature = true;
        for (var i = 0; i < 4; i++) hub.WriteFrame(id, new RgbColor[3]);
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void Refused_open_is_retried_on_the_next_reconcile()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        hid.RefuseOpen.Add("km7-keyboard-KB1-vendor");
        using var hub = new IbpPeripheralHub(hid);
        Assert.False(hub.Reconcile(AllowAll));
        Assert.False(hub.IsConnected);
        hid.RefuseOpen.Clear();
        Assert.True(hub.Reconcile(AllowAll));
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void Dispose_hands_every_unit_back_and_closes_it()
    {
        var hid = new ScriptedEnumerator();
        hid.AddUnit(IbpPeripheralProtocol.Km7Keyboard, "KB1");
        hid.AddUnit(IbpPeripheralProtocol.Mek4Keyboard, "MEK1");
        var hub = new IbpPeripheralHub(hid);
        hub.Reconcile(AllowAll);
        foreach (var a in hub.Attached) hub.WriteFrame(a.DeviceId, new RgbColor[a.Model.LedCount]);
        hub.Dispose();
        Assert.Empty(hub.Attached);
        var kb = hid.Opened["km7-keyboard-KB1-vendor"];
        var mek = hid.Opened["mek4-keyboard-MEK1-vendor"];
        Assert.True(kb.Disposed);
        Assert.True(mek.Disposed);
        Assert.Equal(new byte[] { 0x08, 0x09, 0xF9, 0x01 }, kb.Features[^1].AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x08, 0xF9, 0x01 }, mek.Features[^1].AsSpan(0, 3).ToArray());
        Assert.Equal(382, mek.Features[^1].Length);
        Assert.False(hub.Reconcile(AllowAll));
    }
}
