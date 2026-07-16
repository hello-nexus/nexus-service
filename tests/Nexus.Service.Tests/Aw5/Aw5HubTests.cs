using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Aw5;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.Aw5;

/// <summary>
/// Pins which interface each variant renders from. The Levelplay presents eight HID
/// collections (six of them keyboard/consumer - the cooler enumerates as a keyboard),
/// so picking one by position or by "the first match" lands on a collection that
/// accepts writes and renders nothing.
/// </summary>
public class Aw5HubTests
{
    private const int Vid = 0x3402;
    private const int Levelplay = 0x0406;
    private const int CoolerMaster = 0x0407;

    private static HidDeviceInfo Iface(int pid, int usagePage, int outLen, string path)
        => new() { VendorId = Vid, ProductId = pid, UsagePage = usagePage, OutputReportByteLength = outLen, Path = path };

    [Fact]
    public void Discover_picks_the_levelplay_vendor_collection_out_of_its_keyboard_ones()
    {
        var hid = new FakeHid(
            Iface(Levelplay, 0x0001, 0, "kbd-col04"),
            Iface(Levelplay, 0x0001, 2, "kbd-mi00"),
            Iface(Levelplay, 0x000C, 0, "consumer-col01"),
            Iface(Levelplay, 0xFF00, 0, "vendor-col03"),
            Iface(Levelplay, 0xFF01, 64, "panel-col07"));

        var found = new Aw5Hub(hid).Discover();

        var panel = Assert.Single(found);
        Assert.Equal(Aw5Variant.Levelplay, panel.Variant);
        Assert.Equal("panel-col07", panel.Path);
    }

    [Fact]
    public void Discover_picks_the_coolermaster_panel_by_its_output_report_length()
    {
        var hid = new FakeHid(
            Iface(CoolerMaster, 0x00FF, 9, "not-the-panel"),
            Iface(CoolerMaster, 0x00FF, 64, "panel"));

        var found = new Aw5Hub(hid).Discover();

        var panel = Assert.Single(found);
        Assert.Equal(Aw5Variant.CoolerMaster, panel.Variant);
        Assert.Equal("panel", panel.Path);
    }

    [Fact]
    public void Discover_returns_both_coolers_when_both_are_attached()
    {
        var hid = new FakeHid(
            Iface(Levelplay, 0xFF01, 64, "lp"),
            Iface(CoolerMaster, 0x00FF, 64, "cm"));

        var found = new Aw5Hub(hid).Discover();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.Variant == Aw5Variant.Levelplay);
        Assert.Contains(found, p => p.Variant == Aw5Variant.CoolerMaster);
    }

    [Fact]
    public async Task Render_sends_levelplay_over_feature_reports_and_coolermaster_over_interrupt_out()
    {
        // The transports are not interchangeable: an output report on the Levelplay's
        // FF01 collection is accepted and renders nothing.
        var hid = new FakeHid(
            Iface(Levelplay, 0xFF01, 64, "lp"),
            Iface(CoolerMaster, 0x00FF, 64, "cm"));
        var hub = new Aw5Hub(hid);
        var reading = new Aw5PanelReading(45, 30, 3600);

        foreach (var p in hub.Discover()) await hub.RenderAsync(p, reading, CancellationToken.None);

        Assert.Equal(5, hid.Device("lp")!.Features.Count);
        Assert.Empty(hid.Device("lp")!.Writes);
        Assert.Single(hid.Device("cm")!.Writes);
        Assert.Empty(hid.Device("cm")!.Features);
    }

    [Fact]
    public async Task Render_drops_the_handle_when_a_panel_refuses_so_the_next_tick_reopens()
    {
        var hid = new FakeHid(Iface(CoolerMaster, 0x00FF, 64, "cm")) { WriteResult = false };
        var hub = new Aw5Hub(hid);
        var target = hub.Discover().Single();

        Assert.False(await hub.RenderAsync(target, default, CancellationToken.None));
        await hub.RenderAsync(target, default, CancellationToken.None);

        Assert.Equal(2, hid.Opens);
    }

    [Fact]
    public void Blank_leaves_levelplay_alone()
    {
        // No blanking frame is known for 0406; it reverts to standalone on its own.
        var hid = new FakeHid(Iface(Levelplay, 0xFF01, 64, "lp"));
        var hub = new Aw5Hub(hid);

        hub.Blank(hub.Discover().Single());

        Assert.Empty(hid.Device("lp")?.Writes ?? new List<byte[]>());
    }

    [Fact]
    public async Task CloseAbsent_releases_an_unplugged_panel()
    {
        var hid = new FakeHid(Iface(CoolerMaster, 0x00FF, 64, "cm"));
        var hub = new Aw5Hub(hid);
        var target = hub.Discover().Single();
        await hub.RenderAsync(target, default, CancellationToken.None);

        hub.CloseAbsent(Array.Empty<Aw5PanelTarget>());
        await hub.RenderAsync(target, default, CancellationToken.None);

        Assert.Equal(2, hid.Opens);
    }

    private sealed class FakeHid : IHidEnumerator
    {
        private readonly HidDeviceInfo[] _ifaces;
        private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

        public FakeHid(params HidDeviceInfo[] ifaces) { _ifaces = ifaces; }

        public bool WriteResult { get; init; } = true;
        public int Opens { get; private set; }
        public FakeDevice? Device(string path) => _devices.TryGetValue(path, out var d) ? d : null;

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
            => _ifaces.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToArray();

        public IReadOnlyList<HidDeviceInfo> FindAll() => _ifaces;

        public IHidDevice? Open(string path, bool forInput = false)
        {
            Opens++;
            var dev = new FakeDevice(path, WriteResult);
            _devices[path] = dev;
            return dev;
        }

        internal sealed class FakeDevice : IHidDevice
        {
            private readonly bool _writeResult;
            public FakeDevice(string path, bool writeResult) { Path = path; _writeResult = writeResult; }
            public List<byte[]> Writes { get; } = new();
            public List<byte[]> Features { get; } = new();
            public int VendorId => Vid;
            public int ProductId => 0;
            public string Path { get; }
            public string? Serial => null;
            public int UsagePage => 0;
            public int Usage => 0;
            public bool Write(ReadOnlySpan<byte> report) { Writes.Add(report.ToArray()); return _writeResult; }
            public bool SetFeature(ReadOnlySpan<byte> report) { Features.Add(report.ToArray()); return _writeResult; }
            public bool GetFeature(Span<byte> buffer) => throw new NotSupportedException();
            public bool GetInputReport(Span<byte> buffer) => throw new NotSupportedException();
            public bool SetOutputReport(ReadOnlySpan<byte> report) => throw new NotSupportedException();
            public int Read(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
