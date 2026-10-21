using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting.Bench;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Bench;

public class LightingBenchStatsTests
{
    [Fact]
    public void Empty_input_is_all_zero()
    {
        var p = BenchStats.Summarize(ReadOnlySpan<double>.Empty);
        Assert.Equal(0, p.Count);
        Assert.Equal(0, p.Max);
    }

    [Fact]
    public void Single_sample_fills_every_field()
    {
        var p = BenchStats.Summarize(new[] { 4.0 });
        Assert.Equal(4.0, p.P50);
        Assert.Equal(4.0, p.P99);
        Assert.Equal(4.0, p.Max);
    }

    [Fact]
    public void Percentiles_interpolate_between_ranks()
    {
        var data = Enumerable.Range(1, 101).Select(static i => (double)i).Reverse().ToArray();
        var p = BenchStats.Summarize(data);
        Assert.Equal(101, p.Count);
        Assert.Equal(1.0, p.Min);
        Assert.Equal(51.0, p.P50, 6);
        Assert.Equal(96.0, p.P95, 6);
        Assert.Equal(100.0, p.P99, 6);
        Assert.Equal(101.0, p.Max);
        Assert.Equal(51.0, p.Mean, 6);
    }

    [Theory]
    [InlineData(0.0, 10.0)]
    [InlineData(0.5, 25.0)]
    [InlineData(1.0, 40.0)]
    [InlineData(0.25, 17.5)]
    public void Percentile_of_sorted_array(double q, double expected)
    {
        Assert.Equal(expected, BenchStats.Percentile(new[] { 10.0, 20.0, 30.0, 40.0 }, q), 6);
    }
}

public class LightingBenchFingerprintTests
{
    private static byte[] Flat(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * 3];
        for (var i = 0; i < buf.Length; i += 3)
        {
            buf[i] = r;
            buf[i + 1] = g;
            buf[i + 2] = b;
        }
        return buf;
    }

    [Fact]
    public void Flat_canvas_gives_flat_cells()
    {
        var fp = BenchFingerprint.Compute(Flat(160, 90, 10, 20, 30), 160, 90);
        Assert.Equal(BenchFingerprint.Grid * BenchFingerprint.Grid * 3, fp.Length);
        for (var i = 0; i < fp.Length; i += 3)
        {
            Assert.Equal(10, fp[i]);
            Assert.Equal(20, fp[i + 1]);
            Assert.Equal(30, fp[i + 2]);
        }
    }

    [Fact]
    public void Left_half_red_lands_in_the_left_cells()
    {
        const int w = 160, h = 80;
        var buf = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w / 2; x++)
            {
                buf[(y * w + x) * 3] = 200;
            }
        }
        var fp = BenchFingerprint.Compute(buf, w, h);
        Assert.Equal(200, fp[0]);
        Assert.Equal(0, fp[(BenchFingerprint.Grid - 1) * 3]);
    }

    [Fact]
    public void Mismatched_buffer_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BenchFingerprint.Compute(new byte[10], 160, 90));
    }

    [Fact]
    public void Delta_reports_max_and_mean()
    {
        var a = new byte[] { 0, 10, 250, 100 };
        var b = new byte[] { 3, 10, 240, 100 };
        Assert.Equal(10, BenchFingerprint.MaxChannelDelta(a, b));
        Assert.Equal(13.0 / 4, BenchFingerprint.MeanChannelDelta(a, b), 6);
    }

    [Fact]
    public void Delta_of_different_lengths_throws()
    {
        Assert.Throws<ArgumentException>(() => BenchFingerprint.MaxChannelDelta(new byte[2], new byte[3]));
    }
}

public class LightingBenchReportTests
{
    private static BenchEffectResult Effect(string name, params (long At, byte[] Cells)[] fps)
    {
        var e = new BenchEffectResult { Name = name, Ok = true };
        foreach (var (at, cells) in fps)
        {
            e.Fingerprints.Add(new BenchFingerprintSample { AtMs = at, Hex = Convert.ToHexString(cells).ToLowerInvariant() });
        }
        return e;
    }

    private static BenchComboResult Combo(string backend, string adapter, params BenchEffectResult[] effects) =>
        new() { Backend = backend, Adapter = adapter, Effects = effects.ToList() };

    [Fact]
    public void Compare_takes_the_worst_delta_over_shared_times()
    {
        var a = Combo("angle", "dedicated", Effect("plasma", (1000, new byte[] { 10, 10, 10 }), (5000, new byte[] { 50, 50, 50 })));
        var b = Combo("wgl", "dedicated", Effect("plasma", (1000, new byte[] { 12, 10, 10 }), (5000, new byte[] { 50, 50, 41 })));
        var cmp = BenchReport.Compare(a, b);
        var d = Assert.Single(cmp.Effects);
        Assert.Equal(9, d.MaxChannelDelta);
        Assert.Equal(9, cmp.WorstMaxChannelDelta);
        Assert.Equal((2.0 / 3 + 9.0 / 3) / 2, d.MeanChannelDelta, 6);
    }

    [Fact]
    public void Compare_skips_effects_missing_on_one_side_or_failed()
    {
        var a = Combo("angle", "dedicated", Effect("plasma", (1000, new byte[] { 1, 1, 1 })), Effect("fire", (1000, new byte[] { 1, 1, 1 })));
        var failed = Effect("fire", (1000, new byte[] { 9, 9, 9 }));
        failed.Ok = false;
        var b = Combo("wgl", "dedicated", Effect("plasma", (2000, new byte[] { 1, 1, 1 })), failed);
        Assert.Empty(BenchReport.Compare(a, b).Effects);
    }

    [Fact]
    public void Merge_pairs_across_backends_and_across_adapters_only()
    {
        var fp = (1000L, new byte[] { 1, 2, 3 });
        var run = BenchReport.Merge(new BenchRun(), new[]
        {
            Combo("angle", "dedicated", Effect("e", fp)),
            Combo("wgl", "dedicated", Effect("e", fp)),
            Combo("angle", "warp", Effect("e", fp)),
            Combo("wgl", "warp"),
        });
        var pairs = run.Comparisons.Select(static c => $"{c.A} {c.B}").OrderBy(static s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "angle/dedicated angle/warp", "angle/dedicated wgl/dedicated" }, pairs);
    }

    [Fact]
    public void Table_renders_skipped_failed_and_ok_combos()
    {
        var ok = Combo("wgl", "dedicated");
        var e = Effect("plasma", (1000, new byte[] { 1, 2, 3 }));
        e.WorkMs = new Percentiles { P50 = 1, P95 = 2, P99 = 3, Max = 4 };
        e.RenderWallMs = new Percentiles { P95 = 1.5 };
        e.SampleMs = new Percentiles { P95 = 0.5 };
        ok.Effects.Add(e);
        ok.Effects.Add(new BenchEffectResult { Name = "broken", Ok = false, Error = "compile error" });
        var skipped = new BenchComboResult { Backend = "wgl", Adapter = "warp", Status = "skipped", Detail = "no software adapter" };
        var table = BenchReport.FormatTable(BenchReport.Merge(new BenchRun { Seconds = 10 }, new[] { ok, skipped }));
        Assert.Contains("wgl/dedicated", table);
        Assert.Contains("plasma", table);
        Assert.Contains("FAILED", table);
        Assert.Contains("no software adapter", table);
    }
}

public class LightingBenchOptionsTests
{
    [Fact]
    public void Defaults_cover_both_backends_and_three_adapters()
    {
        Assert.Null(BenchOptions.TryParse(new[] { "--lighting-bench", "out.json" }, child: false, out var o));
        Assert.Equal("out.json", o.OutPath);
        Assert.Equal(new[] { "angle", "wgl" }, o.Backends);
        Assert.Equal(new[] { "integrated", "dedicated", "warp" }, o.Adapters);
        Assert.Equal(10, o.Seconds);
        Assert.Null(o.Effects);
    }

    [Fact]
    public void Lists_seconds_and_effects_parse()
    {
        var args = new[] { "--lighting-bench", "o.json", "--backends", "WGL, angle", "--adapters", "warp", "--seconds", "3", "--effects", "plasma,fire" };
        Assert.Null(BenchOptions.TryParse(args, child: false, out var o));
        Assert.Equal(new[] { "wgl", "angle" }, o.Backends);
        Assert.Equal(new[] { "warp" }, o.Adapters);
        Assert.Equal(3, o.Seconds);
        Assert.Equal(new[] { "plasma", "fire" }, o.Effects);
    }

    [Theory]
    [InlineData("--backends", "metal")]
    [InlineData("--adapters", "egpu")]
    [InlineData("--seconds", "0")]
    [InlineData("--seconds", "abc")]
    [InlineData("--seconds", "601")]
    [InlineData("--bogus", "x")]
    public void Bad_values_are_rejected(string flag, string value)
    {
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench", "o.json", flag, value }, child: false, out _));
    }

    [Fact]
    public void Missing_output_path_or_value_is_rejected()
    {
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench" }, child: false, out _));
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench", "--seconds", "3" }, child: false, out _));
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench", "o.json", "--seconds" }, child: false, out _));
    }

    [Fact]
    public void Child_flags_parse_and_parent_only_flags_are_refused()
    {
        var args = new[] { "--lighting-bench-child", "c.json", "--backend", "angle", "--adapter", "warp", "--luid", "0:123" };
        Assert.Null(BenchOptions.TryParse(args, child: true, out var o));
        Assert.Equal("angle", o.Backend);
        Assert.Equal("warp", o.Adapter);
        Assert.Equal("0:123", o.Luid);
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench-child", "c.json", "--backends", "wgl" }, child: true, out _));
    }

    [Fact]
    public void Child_requires_a_known_backend()
    {
        Assert.NotNull(BenchOptions.TryParse(new[] { "--lighting-bench-child", "c.json", "--adapter", "warp" }, child: true, out _));
    }
}

public class LightingBenchAdapterPickTests
{
    private static GpuAdapterLuids.Adapter A(string luid, long vramMb) => new(luid, luid, 0, vramMb);

    [Fact]
    public void Integrated_is_the_low_memory_adapter_and_dedicated_the_largest()
    {
        var list = new List<GpuAdapterLuids.Adapter> { A("0:1", 512), A("0:2", 8192), A("0:3", 16384) };
        Assert.Equal("0:1", BenchAdapterPick.Pick(list, "integrated")!.Value.Luid);
        Assert.Equal("0:3", BenchAdapterPick.Pick(list, "dedicated")!.Value.Luid);
    }

    [Fact]
    public void Microsoft_adapters_are_never_picked()
    {
        var list = new List<GpuAdapterLuids.Adapter>
        {
            new("0:9", "Microsoft Basic Render Driver", 0x1414, 0),
            new("0:8", "Indirect Display", 0x1414, 16384),
        };
        Assert.Null(BenchAdapterPick.Pick(list, "integrated"));
        Assert.Null(BenchAdapterPick.Pick(list, "dedicated"));
    }

    [Fact]
    public void Child_adapter_name_parses()
    {
        var args = new[] { "--lighting-bench-child", "c.json", "--backend", "angle", "--adapter-name", "Intel(R) UHD Graphics" };
        Assert.Null(BenchOptions.TryParse(args, child: true, out var o));
        Assert.Equal("Intel(R) UHD Graphics", o.AdapterDescription);
    }

    [Fact]
    public void Missing_class_yields_null()
    {
        var list = new List<GpuAdapterLuids.Adapter> { A("0:2", 8192) };
        Assert.Null(BenchAdapterPick.Pick(list, "integrated"));
        Assert.Null(BenchAdapterPick.Pick(new List<GpuAdapterLuids.Adapter>(), "dedicated"));
    }
}

public class LightingBenchDevicesTests
{
    [Fact]
    public void Device_set_is_stable_and_has_matrix_uvs()
    {
        var a = BenchDevices.Create();
        Assert.Equal(BenchDevices.TotalLeds(), a.Sum(static d => d.LedCount));
        var kb = a.Single(static d => d.Archetype == "keyboard");
        Assert.Equal(kb.LedCount, kb.LedU!.Length);
        Assert.All(a, static d => Assert.True(d.LedCount > 0));
    }
}
