using System.Linq;
using System.Text.Json;
using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Devices;

public class DeviceInventoryMapperTests
{
    private static Slv3FanInfo Chain(string mac, int fanType, int fanCount, int devType = 0, bool bound = true) => new()
    {
        Mac = mac,
        BoundToUs = bound,
        DevType = devType,
        FanType = fanType,
        FanCount = fanCount,
    };

    [Theory]
    [InlineData(20, "lianli-wl:slv3-led")]
    [InlineData(25, "lianli-wl:slv3-lcd")]
    [InlineData(27, "lianli-wl:tl-v2-lcd")]
    [InlineData(34, "lianli-wl:tl-v2-lcd")]
    [InlineData(29, "lianli-wl:tl-v2-led")]
    [InlineData(37, "lianli-wl:sl-inf")]
    [InlineData(40, "lianli-wl:rl120")]
    [InlineData(41, "lianli-wl:cl")]
    [InlineData(45, "lianli-wl:sl-inf-flex")]
    [InlineData(43, "lianli-wl:sl-inf-flex-lcd")]
    [InlineData(55, "lianli-wl:tl-flex-lcd")]
    [InlineData(53, "lianli-wl:tl-flex")]
    [InlineData(60, "lianli-wl:sl-v4")]
    [InlineData(63, "lianli-wl:p28-v2")]
    [InlineData(126, "lianli-wl:cl-v2")]
    public void Wireless_chain_maps_fan_type_to_family(int fanType, string modelKey)
    {
        var rows = DeviceInventoryMapper.FromWireless(new Slv3State { Fans = new[] { Chain("AA:BB", fanType, 3) } });

        var row = Assert.Single(rows);
        Assert.Equal(modelKey, row.ModelKey);
        Assert.Equal("fan", row.Category);
        Assert.Equal(3, row.Count);
        Assert.Equal("detected", row.CountSource);
        Assert.Equal("lianli-wireless:AA:BB", row.Key);
    }

    [Fact]
    public void Wireless_skips_unbound_wired_owned_strimer_unknown_and_empty_chains()
    {
        var state = new Slv3State
        {
            Fans = new[]
            {
                Chain("01", 20, 3, bound: false),
                new Slv3FanInfo { Mac = "02", BoundToUs = true, WiredOwned = true, FanType = 20, FanCount = 3 },
                Chain("03", 0, 0, devType: 5),
                Chain("04", 99, 3),
                Chain("05", 20, 0),
                Chain("06", 20, 4),
            },
        };

        var row = Assert.Single(DeviceInventoryMapper.FromWireless(state));
        Assert.Equal("lianli-wireless:06", row.Key);
    }

    [Theory]
    [InlineData(10, "lianli:hydroshift2-lcd-c")]
    [InlineData(11, "lianli:hydroshift2-lcd-s")]
    public void Wireless_hydroshift_is_a_cooler_without_a_count(int devType, string modelKey)
    {
        var row = Assert.Single(DeviceInventoryMapper.FromWireless(new Slv3State { Fans = new[] { Chain("AA", 0, 0, devType) } }));

        Assert.Equal(modelKey, row.ModelKey);
        Assert.Equal("cooler", row.Category);
        Assert.Null(row.Count);
        Assert.Null(row.CountSource);
    }

    [Theory]
    [InlineData(0x7750, "lianli-uni:sl")]
    [InlineData(0xA100, "lianli-uni:sl")]
    [InlineData(0xA101, "lianli-uni:al")]
    [InlineData(0xA102, "lianli-uni:sl-inf")]
    [InlineData(0xA103, "lianli-uni:sl-v2")]
    [InlineData(0xA104, "lianli-uni:al-v2")]
    public void Uni_hub_maps_pid_to_family_with_user_fan_count(int pid, string modelKey)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var profile));
        var fans = new LianLiSettings { Port0Fans = 3, Port1Fans = 2, Port2Fans = 0, Port3Fans = 1 };

        var row = DeviceInventoryMapper.FromUniHub("lianli", true, profile, fans);

        Assert.NotNull(row);
        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal(6, row.Count);
        Assert.Equal("user", row.CountSource);
        Assert.Equal(0x0CF2, row.Vid);
        Assert.Equal(pid, row.Pid);
        Assert.Equal("fan", row.Category);
    }

    [Fact]
    public void Uni_hub_count_is_capped_per_port_and_disconnected_hub_is_absent()
    {
        Assert.True(LianLiFanProfiles.TryGet(0xA100, out var profile));
        var fans = new LianLiSettings { Port0Fans = 99, Port1Fans = 0, Port2Fans = 0, Port3Fans = 0 };

        Assert.Equal(profile.MaxFans, DeviceInventoryMapper.FromUniHub("lianli", true, profile, fans)!.Count);
        Assert.Null(DeviceInventoryMapper.FromUniHub("lianli", false, profile, fans));
    }

    [Fact]
    public void Uni_hub_with_untouched_fan_settings_sends_no_count()
    {
        Assert.True(LianLiFanProfiles.TryGet(0xA102, out var profile));

        var row = DeviceInventoryMapper.FromUniHub("lianli", true, profile, new LianLiSettings());

        Assert.NotNull(row);
        Assert.Null(row!.Count);
        Assert.Null(row.CountSource);
    }

    [Theory]
    [InlineData(true, "lianli:hydroshift2-lcd-c")]
    [InlineData(false, "lianli:hydroshift2-lcd-s")]
    public void Hydroshift2_over_usb_maps_to_the_ii_keys(bool round, string modelKey)
    {
        var row = DeviceInventoryMapper.FromHydroShift2Usb(true, round, 0x1CBE, "S1");

        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal("cooler", row.Category);
        Assert.Null(DeviceInventoryMapper.FromHydroShift2Usb(false, round, 0x1CBE, "S1"));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, true)]
    [InlineData(0, true, false)]
    [InlineData(2, true, false)]
    public void Radio_covers_the_usb_hydroshift_only_when_it_lists_exactly_one(int bound, bool usbConnected, bool covers)
    {
        var fans = Enumerable.Range(0, bound).Select(i => { var f = Chain("0" + i, 0, 0, 10); f.UsbConnected = usbConnected; return f; }).ToList();
        var unbound = Chain("FF", 0, 0, 10, bound: false);
        unbound.UsbConnected = true;
        fans.Add(unbound);

        Assert.Equal(covers, DeviceInventoryMapper.RadioCoversUsbHydroShift2(new Slv3State { Fans = fans.ToArray() }));
    }

    [Fact]
    public void Tl_hub_counts_channels()
    {
        var snapshot = new TlHubSnapshot(new[] { 0, 0, 1 }, new[] { 0, 1, 0 }, new System.Collections.Generic.Dictionary<int, int>());

        var row = DeviceInventoryMapper.FromTlHub(true, snapshot);

        Assert.Equal("lianli-tl:tl-fan", row!.ModelKey);
        Assert.Equal(3, row.Count);
        Assert.Null(DeviceInventoryMapper.FromTlHub(true, TlHubSnapshot.Empty));
        Assert.Null(DeviceInventoryMapper.FromTlHub(false, snapshot));
    }

    [Theory]
    [InlineData(0x7373, "lianli:galahad2")]
    [InlineData(0x7371, "lianli:galahad2-perf")]
    public void Galahad2_maps_pid(int pid, string modelKey)
    {
        var row = DeviceInventoryMapper.FromGalahad2(true, pid);

        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal("cooler", row.Category);
        Assert.Null(row.RadiatorMm);
        Assert.Null(DeviceInventoryMapper.FromGalahad2(false, pid));
    }

    [Theory]
    [InlineData(0x3012, "nzxt:kraken-elite-v2")]
    [InlineData(0x3014, "nzxt:kraken-plus-v2")]
    [InlineData(0x300C, "nzxt:kraken-elite-2023")]
    [InlineData(0x300E, "nzxt:kraken-2023")]
    [InlineData(0x3008, "nzxt:kraken-z3")]
    [InlineData(0x2007, "nzxt:kraken-x3")]
    [InlineData(0x2014, "nzxt:kraken-x3")]
    public void Kraken_maps_every_model_row(int pid, string modelKey)
    {
        var model = KrakenModel.Find(pid)!;

        var row = DeviceInventoryMapper.FromKraken(true, model, "SN1");

        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal("nzxt-kraken:SN1", row.Key);
        Assert.Null(row.RadiatorMm);
        Assert.Equal(pid, row.Pid);
    }

    [Fact]
    public void Kraken_table_has_no_unmapped_model()
    {
        foreach (var model in KrakenModel.All)
        {
            Assert.NotNull(DeviceInventoryMapper.FromKraken(true, model, null));
        }
    }

    [Fact]
    public void Nzxt_fan_controller_counts_plugged_headers()
    {
        var fans = new[]
        {
            new NzxtFanReading(NzxtFanControllerProtocol.ModePwm, 900, 50),
            new NzxtFanReading(NzxtFanControllerProtocol.ModeNone, 0, 0),
            new NzxtFanReading(NzxtFanControllerProtocol.ModeDc, 700, 40),
        };

        var row = DeviceInventoryMapper.FromNzxtFanController(true, fans);

        Assert.Equal("nzxt:rgb-fan-controller", row!.ModelKey);
        Assert.Equal("controller", row.Category);
        Assert.Equal(2, row.Count);
        Assert.Null(DeviceInventoryMapper.FromNzxtFanController(false, fans));
    }

    private static CorsairLinkDevice Link(int channel, int type, int model, string serial = "")
    {
        var meta = CorsairLinkModels.Lookup(type, model);
        return new CorsairLinkDevice { Channel = channel, Type = type, Model = model, Name = meta.Name, Class = meta.Class, Serial = serial };
    }

    [Fact]
    public void Corsair_link_maps_classes_and_radiator_size()
    {
        var state = new CorsairLinkState
        {
            IsConnected = true,
            Devices = new[]
            {
                Link(1, 1, 0, "FAN1"),
                Link(2, 17, 2, "AIO1"),
                Link(3, 6, 0, "CAP1"),
                Link(4, 5, 0, "AD1"),
                Link(5, 7, 0, "H100"),
                Link(6, 26, 0, "RX360"),
                Link(7, 200, 200, "UNK"),
            },
        };

        var rows = DeviceInventoryMapper.FromCorsairLink("corsair", state).ToDictionary(r => r.Key);

        Assert.Equal("fan", rows["corsair-link:FAN1"].Category);
        Assert.Equal("corsair-link:1:0", rows["corsair-link:FAN1"].ModelKey);
        Assert.Equal("cooler", rows["corsair-link:AIO1"].Category);
        Assert.Equal(360, rows["corsair-link:AIO1"].RadiatorMm);
        Assert.Equal("corsair-link:17:2", rows["corsair-link:AIO1"].ModelKey);
        Assert.Equal("lighting", rows["corsair-link:CAP1"].Category);
        Assert.Equal("controller", rows["corsair-link:AD1"].Category);
        Assert.Null(rows["corsair-link:H100"].RadiatorMm);
        Assert.Equal(3, rows["corsair-link:RX360"].Count);
        Assert.Equal(1, rows["corsair-link:FAN1"].Count);
        Assert.Equal("detected", rows["corsair-link:FAN1"].CountSource);
        Assert.Null(rows["corsair-link:AIO1"].Count);
        Assert.All(rows.Values, r => Assert.Null(r.PartOf));
        Assert.DoesNotContain("corsair-link:UNK", rows.Keys);
    }

    [Fact]
    public void Corsair_link_disconnected_hub_and_missing_serial()
    {
        var devices = new[] { Link(4, 1, 0) };

        Assert.Empty(DeviceInventoryMapper.FromCorsairLink("corsair", new CorsairLinkState { IsConnected = false, Devices = devices }));
        var row = Assert.Single(DeviceInventoryMapper.FromCorsairLink("corsair", new CorsairLinkState { IsConnected = true, Devices = devices }));
        Assert.Equal("corsair-link:corsair:ch4", row.Key);
    }

    [Theory]
    [InlineData("lianli-galahad2-lcd", 0x7395, "lianli:galahad2-lcd", "cooler")]
    [InlineData("lianli-hydroshift-lcd", 0x7398, "lianli:hydroshift-lcd-360s", "cooler")]
    [InlineData("lianli-hydroshift-lcd", 0x7399, "lianli:hydroshift-lcd-360r", "cooler")]
    [InlineData("lianli-hydroshift-lcd", 0x739A, "lianli:hydroshift-lcd-360tl", "cooler")]
    [InlineData("corsair-xc7-lcd", 0x0C42, "corsair:xc7-elite-lcd", "cooler")]
    public void Jpeg_panel_maps_handler_and_pid(string handlerId, int pid, string modelKey, string category)
    {
        var model = JpegPanelModel.ByHandlerId(handlerId)!;

        var row = DeviceInventoryMapper.FromJpegPanel(model, true, pid, "S1");

        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal(category, row.Category);
        Assert.Equal(handlerId + ":S1", row.Key);
        Assert.Null(DeviceInventoryMapper.FromJpegPanel(model, false, pid, "S1"));
    }

    [Fact]
    public void Jpeg_panel_table_has_no_unmapped_model_when_attached()
    {
        foreach (var model in JpegPanelModel.All)
        {
            Assert.NotNull(DeviceInventoryMapper.FromJpegPanel(model, true, model.ProductIds[0], null));
        }
    }

    [Theory]
    [InlineData("q60", "hyte:thicc-q60")]
    [InlineData("q80", "hyte:thicc-q80")]
    public void QSeries_maps_variant(string variant, string modelKey)
    {
        var row = DeviceInventoryMapper.FromQSeries(true, variant, "");

        Assert.Equal(modelKey, row!.ModelKey);
        Assert.Equal("qseries:" + variant, row.Key);
        Assert.Null(DeviceInventoryMapper.FromQSeries(true, "", ""));
        Assert.Null(DeviceInventoryMapper.FromQSeries(false, variant, ""));
    }

    [Fact]
    public void Ryujin_maps_only_its_handler()
    {
        Assert.Equal("asus:ryujin-lcd", DeviceInventoryMapper.FromRyujin("asus-ryujin-lcd", true, 0x0B05, 0x1AA2, null)!.ModelKey);
        Assert.Null(DeviceInventoryMapper.FromRyujin("other", true, 0x0B05, 0x1AA2, null));
    }

    [Fact]
    public void Response_serializes_camel_case_and_omits_unknown_fields()
    {
        var json = JsonSerializer.Serialize(
            new SystemDevicesResponse { Devices = new[] { DeviceInventoryMapper.FromGalahad2(true, 0x7373)! } },
            AppJsonContext.Default.SystemDevicesResponse);

        Assert.Contains("\"modelKey\":\"lianli:galahad2\"", json);
        Assert.DoesNotContain("count", json);
        Assert.DoesNotContain("radiatorMm", json);
        Assert.DoesNotContain("partOf", json);
    }
}
