#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Lighting.Bench;

internal static partial class LightingBench
{
    /// <summary>Runs the production exactness gate on every effect, then renders windows both ways through the production LED-window program and compares the device bytes for the effects the gate opened.</summary>
    private static void RunGateBench(BenchOptions o, GpuContext ctx, BenchComboResult result)
    {
        var section = new BenchGateSection();
        result.Gate = section;
        result.Status = "partial";
        var (u, v) = LedSpaceCore.StrimerMap();
        var names = new List<string>((IEnumerable<string>?)o.Effects ?? ShaderLibrary.AllEffectKeys);
        using var engine = new LightingEngine();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var n = 0; n < names.Count; n++)
        {
            var name = names[n];
            Console.WriteLine($"gate {n + 1}/{names.Count} {name}");
            var e = new BenchGateEffect { Name = name };
            section.Effects.Add(e);
            try
            {
                var frag = ShaderLibrary.Get(name);
                var sw = Stopwatch.StartNew();
                var reason = LedSpaceGate.SweepBlocking(ctx, name, frag, out _);
                e.SweepMs = sw.Elapsed.TotalMilliseconds;
                e.Exact = reason is null;
                e.Detail = reason;
                if (reason is not null)
                {
                    continue;
                }
                using var fx = new ShaderEffect(name, ctx, frag);
                var canvas = new CanvasBuffer(ctx.Width, ctx.Height);
                for (var i = 0; i < 400 && fx.IsRenderPending || i == 0; i++)
                {
                    fx.RenderFrame(canvas, start);
                    if (!fx.IsRenderPending)
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(10);
                }
                foreach (var def in LsPlacementDefs)
                {
                    var dev = new DeviceFrame(0, "gate", u.Length, def.X, def.Y, def.W, def.H, def.Rotation) { LedU = u, LedV = v };
                    var taps = new LedTapSet(ctx.Width, ctx.Height);
                    taps.AddDevice(dev, ctx.Width, ctx.Height);
                    var sparse = new CanvasBuffer(ctx.Width, ctx.Height);
                    var rgba = Array.Empty<byte>();
                    for (var w = 0; w < 4; w++)
                    {
                        var ticks = Enumerable.Range(0, LsWindow).Select(i => (double)(start + w * 977L + i * (long)PeriodMs)).ToArray();
                        if (!fx.TryRenderLedWindowUnchecked(taps.X, taps.Y, ticks, LsWindow, ref rgba))
                        {
                            e.Windows++;
                            continue;
                        }
                        var equal = true;
                        for (var f = 0; f < LsWindow; f++)
                        {
                            fx.RenderFrame(canvas, ticks[f]);
                            engine.SampleForBench(new[] { dev }, canvas);
                            dev.Publish();
                            var want = dev.LedBytes.ToArray();
                            var o4 = f * taps.Count * 4;
                            for (var t = 0; t < taps.Count; t++)
                            {
                                sparse.SetPixel(taps.X[t], taps.Y[t], rgba[o4], rgba[o4 + 1], rgba[o4 + 2]);
                                o4 += 4;
                            }
                            engine.SampleForBench(new[] { dev }, sparse);
                            dev.Publish();
                            equal &= want.AsSpan().SequenceEqual(dev.LedBytes);
                        }
                        e.Windows++;
                        if (equal)
                        {
                            e.WindowsEqual++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                e.Exact = false;
                e.Detail = $"{ex.GetType().Name}: {ex.Message}";
            }
            SaveChild(o.OutPath, result);
        }
        result.Status = "ok";
    }
}
#endif
