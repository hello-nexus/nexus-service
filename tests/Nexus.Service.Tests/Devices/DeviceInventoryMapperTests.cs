using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Peripherals;
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
    public void Wireless_skips_unbound_wired_owned_and_empty_chains()
    {
        var state = new Slv3State
        {
            Fans = new[]
            {
                Chain("01", 20, 3, bound: false),
                new Slv3FanInfo { Mac = "02", BoundToUs = true, WiredOwned = true, FanType = 20, FanCount = 3 },
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

    [Fact]
    public void Wireless_strimer_is_lighting_and_unknown_fan_type_is_a_fan()
    {
        var rows = DeviceInventoryMapper.FromWireless(new Slv3State { Fans = new[] { Chain("S1", 0, 0, devType: 2), Chain("U1", 99, 3) } });

        var strimer = rows.Single(r => r.Key.EndsWith("S1"));
        Assert.Equal("lianli-wl:strimer-2", strimer.ModelKey);
        Assert.Equal("lighting", strimer.Category);
        var unknown = rows.Single(r => r.Key.EndsWith("U1"));
        Assert.Equal("lianli-wl:unknown-99", unknown.ModelKey);
        Assert.Equal("fan", unknown.Category);
        Assert.Equal(3, unknown.Count);
    }

    [Fact]
    public void Tl_lcd_counts_attached_screens()
    {
        var row = DeviceInventoryMapper.FromTlLcd(4);

        Assert.Equal("lianli:uni-fan-tl-lcd", row!.ModelKey);
        Assert.Equal(4, row.Count);
        Assert.Null(DeviceInventoryMapper.FromTlLcd(0));
    }

    private sealed class FakeHandler : Nexus.Service.Devices.IDeviceHandler
    {
        public FakeHandler(string id, string category, params (int Vid, int Pid)[] ids)
        {
            Id = id;
            Category = category;
            Identifiers = ids.Select(i => new Nexus.Service.Devices.UsbId(i.Vid, i.Pid)).ToArray();
        }

        public string Id { get; }
        public string Name => Id;
        public string Category { get; }
        public IReadOnlyList<Nexus.Service.Devices.UsbId> Identifiers { get; }
        public bool IsConnected(IReadOnlyList<Nexus.Service.Devices.UsbDeviceEntry> detectedDevices) => false;
        public string GetFirmwareVersion() => "";
    }

    private static Nexus.Service.Devices.UsbDeviceEntry Usb(int vid, int pid, string name = "", string serial = "") =>
        new() { VendorId = vid, ProductId = pid, Name = name, Serial = serial };

    private static SupportedDeviceDto Cat(string vendor, string model, string category, int vid, int pid, string source = "openrgb") => new()
    {
        Vendor = vendor,
        Model = model,
        Category = category,
        VendorId = "0x" + vid.ToString("X4"),
        ProductId = "0x" + pid.ToString("X4"),
        Source = source,
    };

    [Fact]
    public void Usb_first_party_catalog_row_keys_by_vendor_and_model()
    {
        var catalog = new[] { Cat("HYTE", "Keeb TKL", "keyboard", 0x3402, 0x0300, "nexus") };

        var row = Assert.Single(DeviceInventoryMapper.FromUsb(new[] { Usb(0x3402, 0x0300) }, new Nexus.Service.Devices.IDeviceHandler[0], new HashSet<string>(), new HashSet<(int, int)>(), catalog));

        Assert.Equal("hyte:keeb-tkl", row.ModelKey);
        Assert.Equal("keyboard", row.Category);
        Assert.Equal("usb:3402:0300", row.Key);
    }

    [Theory]
    [InlineData("Stream Deck +", "stream-deck-plus")]
    [InlineData("Stream Deck", "stream-deck")]
    [InlineData("NZXT Hue+", "nzxt-hue-plus")]
    [InlineData("Acme & Co. #1", "acme-co-1")]
    [InlineData("A+B", "a-plus-b")]
    [InlineData("++", "plus-plus")]
    [InlineData("+ Deck", "plus-deck")]
    public void Slug_keeps_plus_as_a_word_and_drops_other_symbols(string text, string slug)
    {
        Assert.Equal(slug, DeviceInventoryMapper.Slug(text));
    }

    [Fact]
    public void OpenRgb_keys_keep_openrgb_vendor_slug_and_plus()
    {
        var rows = DeviceInventoryMapper.FromOpenRgb(new[]
        {
            new RgbDevice { Type = 4, Name = "Strimer", Vendor = "Lian Li", Serial = "1" },
            new RgbDevice { Type = 4, Name = "NZXT Hue+", Vendor = "NZXT", Serial = "2" },
        });

        Assert.Equal(new[] { "openrgb:lian-li:strimer", "openrgb:nzxt:nzxt-hue-plus" }, rows.Select(r => r.ModelKey).ToArray());
    }

    [Fact]
    public void Usb_lian_li_catalog_rows_share_the_lianli_prefix_and_tl_lcds_stay_distinct()
    {
        var catalog = new[]
        {
            Cat("Lian Li", "TL-LCD", "light", 0x1CBE, 0x0006, "nexus"),
            Cat("Lian Li", "Uni Fan TL LCD", "light", 0x04FC, 0x7393, "nexus"),
            Cat("Lian Li", "SL-LCD", "light", 0x1CBE, 0x0005, "nexus"),
        };
        var usb = new[] { Usb(0x1CBE, 0x0006), Usb(0x04FC, 0x7393), Usb(0x1CBE, 0x0005) };

        var keys = DeviceInventoryMapper.FromUsb(usb, new Nexus.Service.Devices.IDeviceHandler[0], new HashSet<string>(), new HashSet<(int, int)>(), catalog).Select(r => r.ModelKey).ToList();

        Assert.Equal(new[] { "lianli:tl-lcd-wireless", "lianli:uni-fan-tl-lcd", "lianli:sl-lcd" }, keys);
        Assert.NotEqual(DeviceInventoryMapper.FromTlLcd(1)!.ModelKey, keys[0]);
    }

    [Fact]
    public void Usb_catalog_only_and_handler_only_devices_key_by_vid_pid_under_their_category()
    {
        var catalog = new[] { Cat("Corsair", "M65 PRO", "mouse", 0x1B1C, 0x1B2E) };
        var handlers = new Nexus.Service.Devices.IDeviceHandler[]
        {
            new FakeHandler("lianli-wireless", "cooler", (0x0416, 0x8040)),
            new FakeHandler("strimer", "controller", (0x0CF2, 0xA200)),
        };
        var usb = new[] { Usb(0x1B1C, 0x1B2E), Usb(0x0416, 0x8040, "Dongle"), Usb(0x0CF2, 0xA200), Usb(0x046D, 0xC52B, "Receiver") };

        var rows = DeviceInventoryMapper.FromUsb(usb, handlers, new HashSet<string>(), new HashSet<(int, int)>(), catalog).ToDictionary(r => r.ModelKey);

        Assert.Equal("mouse", rows["usb:1b1c:1b2e"].Category);
        Assert.Equal("controller", rows["usb:0416:8040"].Category);
        Assert.Equal("controller", rows["usb:0cf2:a200"].Category);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Usb_skips_claimed_handlers_duplicates_and_parts_build_lists()
    {
        var catalog = new[]
        {
            Cat("ASUS", "Some Board", "motherboard", 0x0B05, 0x1111),
            Cat("Lian Li", "Fan Hub", "fan", 0x0CF2, 0x7750),
        };
        var handlers = new Nexus.Service.Devices.IDeviceHandler[] { new FakeHandler("lianli", "hub", (0x0CF2, 0x7750)) };
        var usb = new[] { Usb(0x0B05, 0x1111), Usb(0x0CF2, 0x7750), Usb(0x0CF2, 0x7750) };

        Assert.Empty(DeviceInventoryMapper.FromUsb(usb, handlers, new HashSet<string> { "lianli" }, new HashSet<(int, int)>(), catalog));

        var rows = DeviceInventoryMapper.FromUsb(usb, handlers, new HashSet<string>(), new HashSet<(int, int)>(), catalog);
        Assert.Equal("fan", Assert.Single(rows).Category);
    }

    [Theory]
    [InlineData(5u, "keyboard")]
    [InlineData(6u, "mouse")]
    [InlineData(8u, "headset")]
    [InlineData(4u, "lighting")]
    [InlineData(11u, "lighting")]
    [InlineData(10u, "peripheral")]
    [InlineData(16u, "headset")]
    [InlineData(17u, "peripheral")]
    [InlineData(21u, "controller")]
    [InlineData(99u, "controller")]
    public void OpenRgb_maps_device_type_to_category(uint type, string category)
    {
        var row = Assert.Single(DeviceInventoryMapper.FromOpenRgb(new[] { new RgbDevice { Type = type, Name = "Thing 1", Vendor = "Acme Co", Serial = "SN" } }));

        Assert.Equal(category, row.Category);
        Assert.Equal("openrgb:acme-co:thing-1", row.ModelKey);
        Assert.Equal("openrgb:openrgb-s-SN", row.Key);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(13u)]
    [InlineData(14u)]
    [InlineData(15u)]
    [InlineData(18u)]
    [InlineData(19u)]
    [InlineData(20u)]
    public void OpenRgb_skips_parts_build_lists_and_virtual_devices(uint type)
    {
        Assert.Empty(DeviceInventoryMapper.FromOpenRgb(new[] { new RgbDevice { Type = type, Name = "Part", Vendor = "V" } }));
    }

    [Fact]
    public void OpenRgb_cooler_type_splits_aio_from_fan_and_first_party_wins_duplicates()
    {
        var rows = DeviceInventoryMapper.FromOpenRgb(new[]
        {
            new RgbDevice { Type = 3, Name = "Corsair Hydro H100i", Vendor = "Corsair", Serial = "A" },
            new RgbDevice { Type = 3, Name = "Case Fan 1", Vendor = "Acme", Serial = "B" },
            new RgbDevice { Type = 5, Name = "HYTE Keeb TKL", Vendor = "HYTE", Serial = "C" },
        });
        Assert.Equal(new[] { "cooler", "fan", "keyboard" }, rows.Select(r => r.Category));

        var firstParty = new[] { new SystemDeviceDto { Vendor = "HYTE", Model = "Keeb TKL", ModelKey = "hyte:keeb-tkl" } };
        var merged = DeviceInventoryMapper.MergeOpenRgb(firstParty, rows);

        Assert.DoesNotContain(merged, r => r.Category == "keyboard" && r.ModelKey.StartsWith("openrgb:"));
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Tl_lcd_usb_ids_and_pinned_uni_slots_are_claimed_by_vid_pid()
    {
        var handlers = new Nexus.Service.Devices.IDeviceHandler[]
        {
            new FakeHandler("lianli", "hub", (0x0CF2, 0x7750)),
            new FakeHandler("lianli2", "hub", (0x0CF2, 0x7750)),
            new FakeHandler("lianli-tl-lcd", "cooler", (0x04FC, 0x7393)),
        };
        var usb = new[] { Usb(0x0CF2, 0x7750, serial: "A"), Usb(0x04FC, 0x7393, serial: "B") };
        var claimed = new HashSet<(int, int)> { (0x0CF2, 0x7750), (0x04FC, 0x7393) };

        Assert.Empty(DeviceInventoryMapper.FromUsb(usb, handlers, new HashSet<string> { "lianli2" }, claimed, new SupportedDeviceDto[0]));
        // Claimed by a later slot's handler id alone: the first matching handler no longer decides.
        Assert.Empty(DeviceInventoryMapper.FromUsb(usb.Take(1).ToArray(), handlers, new HashSet<string> { "lianli2" }, new HashSet<(int, int)>(), new SupportedDeviceDto[0]));
    }

    private static SystemDeviceDto FirstParty(string vendor, string model, int? vid = null, int? pid = null) =>
        new() { Vendor = vendor, Model = model, ModelKey = Slug(vendor, model), Vid = vid, Pid = pid };

    private static string Slug(string vendor, string model) => DeviceInventoryMapper.Slug(vendor) + ":" + DeviceInventoryMapper.Slug(model);

    [Fact]
    public void OpenRgb_row_is_dropped_by_usb_id_from_its_location_and_by_exact_name_only()
    {
        var openRgb = DeviceInventoryMapper.FromOpenRgb(new[]
        {
            new RgbDevice { Type = 6, Name = "Corsair M65 PRO RGB", Vendor = "Corsair", Location = @"HID: \\?\hid#vid_1b1c&pid_1b2e&mi_00#7&abc", Serial = "1" },
            new RgbDevice { Type = 6, Name = "Corsair Dark Core", Vendor = "Corsair", Location = @"HID: \\?\hid#vid_1b1c&pid_1b35&mi_00#7&def", Serial = "2" },
            new RgbDevice { Type = 5, Name = "HYTE Keeb TKL", Vendor = "HYTE", Serial = "3" },
            new RgbDevice { Type = 5, Name = "Corsair K70 RGB", Vendor = "Corsair", Serial = "4" },
        });
        Assert.Equal(0x1B2E, openRgb[0].Pid);

        var rows = new[] { FirstParty("Corsair", "M65 PRO", 0x1B1C, 0x1B2E), FirstParty("HYTE", "Keeb TKL"), FirstParty("Corsair", "K70") };
        var merged = DeviceInventoryMapper.MergeOpenRgb(rows, openRgb).Where(r => r.ModelKey.StartsWith("openrgb:")).Select(r => r.Model).ToList();

        // Same VID:PID drops the M65, an exact name drops the Keeb; a name that only contains another stays.
        Assert.Equal(new[] { "Corsair Dark Core", "Corsair K70 RGB" }, merged);
    }

    [Fact]
    public void OpenRgb_covers_a_catalog_usb_row_with_the_same_usb_id()
    {
        var usbRow = new SystemDeviceDto { ModelKey = "usb:1b1c:1b2e", Model = "M65 PRO", Vid = 0x1B1C, Pid = 0x1B2E };
        var openRgb = new SystemDeviceDto { ModelKey = "openrgb:corsair:x", Model = "Corsair M65 PRO RGB", Vid = 0x1B1C, Pid = 0x1B2E };

        var merged = DeviceInventoryMapper.MergeOpenRgb(new[] { usbRow }, new[] { openRgb });

        Assert.Equal("openrgb:corsair:x", Assert.Single(merged).ModelKey);
    }

    [Fact]
    public void Hashed_keys_hide_serials_stay_short_and_stable()
    {
        var serial = "SERIAL-0123456789-ABCDEF-" + new string('x', 200);
        SystemDeviceDto Row() => new() { Key = "usb:3402:0300:" + serial, ModelKey = "m", Model = "Keeb" };
        var a = new[] { Row() };
        var b = new[] { Row(), new SystemDeviceDto { Key = "usb:3402:0300:other" } };

        DeviceInventoryMapper.HashKeys(a);
        DeviceInventoryMapper.HashKeys(b);

        Assert.StartsWith("usb:", a[0].Key);
        Assert.DoesNotContain("SERIAL", a[0].Key);
        Assert.True(a[0].Key.Length < 64);
        Assert.Equal(a[0].Key, b[0].Key);
        Assert.NotEqual(b[0].Key, b[1].Key);
    }
}
