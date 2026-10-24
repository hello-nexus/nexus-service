using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hid.LampArray;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

public class LampArrayProtocolTests
{
    private static readonly LampArrayReportIds Ids = new(1, 2, 3, 4, 5, 6);

    [Fact]
    public void Report_ids_resolve_from_the_link_collection_usage()
    {
        var ids = LampArrayProtocol.ResolveReportIds(new FakeLampArrayDevice().ReadFeatureValueCaps()!, out var reason);
        Assert.Equal(Ids, ids);
        Assert.Equal("", reason);
    }

    [Fact]
    public void A_missing_request_report_fails_closed_and_names_it()
    {
        var caps = new FakeLampArrayDevice().ReadFeatureValueCaps()!.ToList();
        caps.RemoveAll(c => c.ReportId == FakeLampArrayDevice.Req);
        Assert.Null(LampArrayProtocol.ResolveReportIds(caps, out var reason));
        Assert.Contains("req=0", reason);
    }

    [Fact]
    public void Field_usages_resolve_the_reports_when_the_link_usage_is_missing()
    {
        var caps = new FakeLampArrayDevice().ReadFeatureValueCaps()!.Select(c =>
            c.ReportId == FakeLampArrayDevice.Req ? c : c with { LinkUsage = 0, LinkUsagePage = 0 }).ToList();
        Assert.Equal(Ids, LampArrayProtocol.ResolveReportIds(caps, out _));
    }

    [Fact]
    public void A_multi_update_with_other_field_widths_is_refused()
    {
        var caps = new FakeLampArrayDevice().ReadFeatureValueCaps()!.Select(c =>
            c.ReportId == FakeLampArrayDevice.Multi && c.Usage == 0x21 ? c with { BitSize = 8 } : c).ToList();
        Assert.Null(LampArrayProtocol.ResolveReportIds(caps, out var reason));
        Assert.Contains("unexpected field layout", reason);
    }

    [Fact]
    public void Multi_update_packs_ids_then_rgbi_quads_and_flags_the_last_chunk()
    {
        var colors = new[] { new RgbColor(1, 2, 3), new RgbColor(4, 5, 6) };
        var report = LampArrayProtocol.BuildMultiUpdate(Ids, new[] { 7, 0x0102 }, colors, new byte[] { 255, 9 }, complete: true);
        Assert.Equal(LampArrayProtocol.MultiUpdateBytes, report.Length);
        Assert.Equal(new byte[] { 4, 2, 1, 7, 0, 0x02, 0x01 }, report[..7]);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 9 }, report[19..27]);
        Assert.Equal(0, LampArrayProtocol.BuildMultiUpdate(Ids, new[] { 7 }, colors, new byte[] { 255 }, complete: false)[2]);
    }

    [Fact]
    public void Range_update_spans_first_to_last_lamp_with_update_complete()
    {
        var report = LampArrayProtocol.BuildRangeUpdate(Ids, 0, 0x0103, new RgbColor(9, 8, 7), 255);
        Assert.Equal(new byte[] { 5, 1, 0, 0, 0x03, 0x01, 9, 8, 7, 255 }, report);
    }

    [Fact]
    public void Control_report_carries_the_autonomous_flag()
    {
        Assert.Equal(new byte[] { 6, 0 }, LampArrayProtocol.BuildControl(Ids, autonomous: false));
        Assert.Equal(new byte[] { 6, 1 }, LampArrayProtocol.BuildControl(Ids, autonomous: true));
    }

    [Fact]
    public void Attributes_and_lamp_reports_parse_and_short_ones_do_not()
    {
        var attr = new byte[LampArrayProtocol.AttributesBytes];
        attr[1] = 12;
        attr[3] = 0x10; attr[4] = 0x27;
        attr[19] = 0xE8; attr[20] = 0x03;
        Assert.True(LampArrayProtocol.TryParseAttributes(attr, out var a));
        Assert.Equal(12, a.LampCount);
        Assert.Equal(10000u, a.WidthUm);
        Assert.Equal(1000u, a.MinUpdateIntervalUs);
        Assert.False(LampArrayProtocol.TryParseAttributes(attr[..10], out _));

        var resp = new byte[LampArrayProtocol.ResponseBytes];
        resp[1] = 3; resp[3] = 50; resp[23] = 255; resp[27] = 1;
        Assert.True(LampArrayProtocol.TryParseLampAttributes(resp, out var l));
        Assert.Equal(3, l.LampId);
        Assert.Equal(50, l.XUm);
        Assert.Equal(255, l.RedLevels);
        Assert.True(l.Programmable);
        Assert.False(LampArrayProtocol.TryParseLampAttributes(resp[..20], out _));
    }

    [Fact]
    public void Uv_normalizes_lamp_positions_and_is_null_without_spread()
    {
        LampAttributes At(int x, int y) => new(0, x, y, 0, 0, 0, 255, 255, 255, 255, true, 0);
        var uv = LampArrayProtocol.ComputeUv(new[] { At(100, 50), At(300, 50), At(200, 150) });
        Assert.NotNull(uv);
        Assert.Equal(new[] { 0f, 1f, 0.5f }, uv!.Value.U);
        Assert.Equal(new[] { 0f, 0f, 1f }, uv.Value.V);
        Assert.Null(LampArrayProtocol.ComputeUv(new[] { At(5, 5), At(5, 5) }));
        Assert.Null(LampArrayProtocol.ComputeUv(new[] { At(5, 5) }));
    }

    [Fact]
    public void Intensity_is_full_for_rgb_lamps_and_the_brightest_channel_for_intensity_only_lamps()
    {
        var rgb = new LampAttributes(0, 0, 0, 0, 0, 0, 255, 255, 255, 0, true, 0);
        var mono = new LampAttributes(0, 0, 0, 0, 0, 0, 0, 0, 0, 255, true, 0);
        Assert.Equal(255, LampArrayProtocol.IntensityFor(rgb, new RgbColor(1, 2, 3)));
        Assert.Equal(7, LampArrayProtocol.IntensityFor(mono, new RgbColor(1, 7, 3)));
    }

    private static List<HidValueCap> Replace(int report, params HidValueCap[] with)
    {
        var caps = new FakeLampArrayDevice().ReadFeatureValueCaps()!.Where(c => c.ReportId != report).ToList();
        caps.AddRange(with);
        return caps;
    }

    [Fact]
    public void Listed_usages_reported_one_entry_each_with_count_one_total_the_same_bits()
    {
        var multi = new List<HidValueCap>
        {
            FakeLampArrayDevice.Cap(4, 0x50, 0x03, 8, 1), FakeLampArrayDevice.Cap(4, 0x50, 0x55, 8, 1),
        };
        for (var i = 0; i < 8; i++) multi.Add(FakeLampArrayDevice.Cap(4, 0x50, 0x21, 16, 1));
        foreach (var u in new[] { 0x51, 0x52, 0x53, 0x54 })
        {
            for (var i = 0; i < 8; i++) multi.Add(FakeLampArrayDevice.Cap(4, 0x50, u, 8, 1));
        }
        Assert.Equal(Ids, LampArrayProtocol.ResolveReportIds(Replace(4, multi.ToArray()), out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void Channels_declared_as_one_usage_range_spread_their_count_over_the_usages()
    {
        var range = new HidValueCap(4, 0x59, 0x51, 0x59, 0x50, 8, 32, UsageMax: 0x54, IsRange: true);
        var caps = Replace(4,
            FakeLampArrayDevice.Cap(4, 0x50, 0x03, 8, 1), FakeLampArrayDevice.Cap(4, 0x50, 0x55, 8, 1),
            FakeLampArrayDevice.Cap(4, 0x50, 0x21, 16, 8), range);
        Assert.Equal(Ids, LampArrayProtocol.ResolveReportIds(caps, out _));
    }

    [Fact]
    public void A_short_listed_layout_is_refused()
    {
        var multi = new List<HidValueCap>
        {
            FakeLampArrayDevice.Cap(4, 0x50, 0x03, 8, 1), FakeLampArrayDevice.Cap(4, 0x50, 0x55, 8, 1),
        };
        for (var i = 0; i < 7; i++) multi.Add(FakeLampArrayDevice.Cap(4, 0x50, 0x21, 16, 1));
        multi.Add(FakeLampArrayDevice.Cap(4, 0x50, 0x51, 8, 8));
        Assert.Null(LampArrayProtocol.ResolveReportIds(Replace(4, multi.ToArray()), out var reason));
        Assert.Contains("unexpected field layout", reason);
    }

    [Fact]
    public void Cap_description_names_every_field()
    {
        var text = LampArrayProtocol.DescribeCap(new HidValueCap(4, 0x59, 0x51, 0x59, 0x50, 8, 32, UsageMax: 0x54, IsRange: true, LinkCollection: 2));
        Assert.Equal("cap report=4 page=59 usage=51-54 range=True link=2:59/50 bits=8 count=32", text);
    }

    [Fact]
    public void Lamp_response_reads_update_latency_before_purposes()
    {
        var resp = new byte[LampArrayProtocol.ResponseBytes];
        resp[0] = 226;
        resp[1] = 1;
        resp[15] = 0x20; resp[16] = 0x4E;
        resp[19] = 0x01;
        Assert.True(LampArrayProtocol.TryParseLampAttributes(resp, out var lamp));
        Assert.Equal(20000u, lamp.LatencyUs);
        Assert.Equal(1u, lamp.Purposes);
    }

    // The Galleon 100 SD dial interface as the customer bundle reported it: ids 224-229, LampId one cap of 16 bits x 8, RGBI as 32 count-one caps.
    [Fact]
    public void The_galleon_mixed_cap_layout_resolves_to_report_ids_224_to_229()
    {
        HidValueCap Cap(int report, int link, int usage, int bits, int count) => FakeLampArrayDevice.Cap(report, link, usage, bits, count);
        var caps = new List<HidValueCap>
        {
            Cap(224, 0x02, 0x03, 16, 1), Cap(224, 0x02, 0x04, 32, 1),
            Cap(225, 0x20, 0x21, 16, 1),
            Cap(226, 0x22, 0x21, 16, 1), Cap(226, 0x22, 0x23, 32, 1),
            Cap(227, 0x50, 0x03, 8, 1), Cap(227, 0x50, 0x55, 8, 1), Cap(227, 0x50, 0x21, 16, 8),
            Cap(228, 0x60, 0x55, 8, 1), Cap(228, 0x60, 0x61, 16, 1),
            Cap(229, 0x70, 0x71, 8, 1),
        };
        foreach (var u in new[] { 0x51, 0x52, 0x53, 0x54 })
        {
            for (var i = 0; i < 8; i++) caps.Add(Cap(227, 0x50, u, 8, 1));
        }
        var ids = LampArrayProtocol.ResolveReportIds(caps, out var reason);
        Assert.Equal(new LampArrayReportIds(224, 225, 226, 227, 228, 229), ids);
        Assert.Equal("", reason);
    }
}
