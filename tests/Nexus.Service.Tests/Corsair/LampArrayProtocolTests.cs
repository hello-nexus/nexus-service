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
}
