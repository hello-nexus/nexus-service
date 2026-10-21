using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>What the exactness gate concluded for one shader source on one GPU and driver.</summary>
internal enum LedVerdict
{
    Pending,
    Exact,
    Rejected,
}

/// <summary>
/// Decides whether an effect may take the LED-window path. The path stays closed until a golden sweep on this very GPU,
/// driver and ANGLE build has shown the LED-window pixels byte-identical to the canvas render of the same source over
/// many windows, whole canvas, including clocks that wrap. The verdict is cached beside the program cache, and a
/// runtime mismatch rejects the source for good.
/// </summary>
internal static class LedSpaceGate
{
    /// <summary>Bumped when the rewrite or the sweep changes meaning, so no older verdict is trusted.</summary>
    internal const int Version = 1;

    internal const int SweepWindows = 16;
    internal const int SweepFrames = 30;
    private const int ChunkTaps = 4800;
    private const int MaxVerdictCacheBytes = 1024 * 1024;
    private const int MaxEntries = 256;

    private sealed class Entry
    {
        public volatile LedVerdict Verdict = LedVerdict.Pending;
        public string Reason = "pending";
        public int Logged;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly Queue<string> EntryOrder = new();
    private static DiskBlobCache? _disk;

    /// <summary>Environment switch: NEXUS_LED_SPACE=0 closes the path everywhere.</summary>
    internal static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("NEXUS_LED_SPACE") != "0" && OperatingSystem.IsWindows();

    /// <summary>Test seam: a cache over a scratch directory.</summary>
    internal static DiskBlobCache? DiskOverride;

    private static DiskBlobCache Disk
    {
        get
        {
            if (DiskOverride is { } over)
            {
                return over;
            }
            lock (Gate)
            {
                return _disk ??= new DiskBlobCache(
                    Path.Combine(BlobCachePolicy.CacheRoot(), "ledspace-verdicts"), MaxVerdictCacheBytes, BlobCachePolicy.Allowed);
            }
        }
    }

    /// <summary>Everything the verdict depends on: the source, the rewrite, and the GPU stack that compiles it.</summary>
    internal static string Key(string fragSource, string? renderer, string? glVersion, bool ownsD3dDevice = false)
    {
        // A context on a device this process created does not share verdicts with one on ANGLE's own device.
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ledspace|{Version}|{LedWindowShader.MaxFrames}|{renderer}|{glVersion}|{(ownsD3dDevice ? "own" : "angle")}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fragSource)))}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>True when the effect may render its look-ahead windows through the LED-window path right now. Never blocks: an unknown source starts its gate in the background and answers false until it is proven.</summary>
    internal static bool IsExact(ShaderEffect fx)
    {
        if (!Enabled)
        {
            return false;
        }
        var ctx = fx.Context;
        if (!ctx.Available || ctx.Renderer is null || ctx.GlVersion is null || !fx.LedWindowCapable)
        {
            return false;
        }
        var key = fx.LedGateKey();
        Entry entry;
        bool start;
        lock (Gate)
        {
            start = !Entries.TryGetValue(key, out entry!);
            if (start)
            {
                entry = Add(key);
            }
        }
        if (start)
        {
            Begin(fx, ctx, key, entry);
        }
        if (entry.Verdict != LedVerdict.Exact)
        {
            return false;
        }
        var lp = fx.LedProgramForSource();
        return lp is not null && lp.State == LedProgram.Ready && lp.Generation == ctx.Generation;
    }

    /// <summary>Caller holds the gate. Oldest entries go first once the table is full; a sweep in flight keeps its own entry.</summary>
    private static Entry Add(string key)
    {
        while (Entries.Count >= MaxEntries && EntryOrder.TryDequeue(out var oldest))
        {
            Entries.Remove(oldest);
        }
        var entry = new Entry();
        Entries[key] = entry;
        EntryOrder.Enqueue(key);
        return entry;
    }

    /// <summary>Latest known verdict without starting anything. For tests and diagnostics.</summary>
    internal static LedVerdict Peek(string key)
    {
        lock (Gate)
        {
            return Entries.TryGetValue(key, out var e) ? e.Verdict : LedVerdict.Pending;
        }
    }

    /// <summary>A runtime spot-check disagreed: the source never takes the fast path again on this stack.</summary>
    internal static void Reject(ShaderEffect fx, string detail)
    {
        var ctx = fx.Context;
        var key = fx.LedGateKey();
        Entry? entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out entry))
            {
                entry = Add(key);
            }
        }
        Conclude(key, entry, fx.Name, LedVerdict.Rejected, detail);
    }

    private static void Conclude(string key, Entry entry, string name, LedVerdict verdict, string reason, bool persist = true)
    {
        entry.Reason = reason;
        entry.Verdict = verdict;
        if (Interlocked.Exchange(ref entry.Logged, 1) == 0 || verdict == LedVerdict.Rejected)
        {
            GpuContext.Log($"[ledspace] {name}: {(verdict == LedVerdict.Exact ? "exact, window path open" : "window path closed")} ({reason})");
        }
        if (!persist)
        {
            return;
        }
        try
        {
            var payload = new byte[4 + 1 + 32];
            Encoding.ASCII.GetBytes("LSG1").CopyTo(payload, 0);
            payload[4] = (byte)verdict;
            SHA256.HashData(Encoding.ASCII.GetBytes(key)).CopyTo(payload, 5);
            Disk.Put(CacheName(key), payload);
        }
        catch (Exception)
        {
        }
    }

    private static string CacheName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("v|" + key))).ToLowerInvariant();

    /// <summary>Verdict recorded by an earlier run on this exact stack, or null.</summary>
    internal static LedVerdict? ReadCached(DiskBlobCache disk, string key)
    {
        var bytes = disk.TryGet(CacheName(key));
        if (bytes is null || bytes.Length != 4 + 1 + 32 || Encoding.ASCII.GetString(bytes, 0, 4) != "LSG1")
        {
            return null;
        }
        if (!bytes.AsSpan(5).SequenceEqual(SHA256.HashData(Encoding.ASCII.GetBytes(key))))
        {
            return null;
        }
        return bytes[4] switch
        {
            (byte)LedVerdict.Exact => LedVerdict.Exact,
            (byte)LedVerdict.Rejected => LedVerdict.Rejected,
            _ => null,
        };
    }

    private static void Begin(ShaderEffect fx, GpuContext ctx, string key, Entry entry)
    {
        var name = fx.Name;
        var frag = fx.FragSource;
        var flags = LedWindowShader.CanvasDependencyFlags(frag).Where(static f => f is "derivative" or "texture" or "discard").ToList();
        if (flags.Count > 0)
        {
            Conclude(key, entry, name, LedVerdict.Rejected, "reads neighboring pixels: " + string.Join(',', flags));
            return;
        }
        // The program build starts now so a cached Exact verdict finds it ready.
        fx.LedProgramForSource();
        // The verdict cache starts loading now; the thread below waits for it before reading.
        var disk = Disk;
        new Thread(() => RunGate(disk, ctx, name, frag, key, entry)) { IsBackground = true, Name = "nexus-ledspace-gate", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>FNV-1a over the source bytes, so every run sweeps the same randomized windows for the same source.</summary>
    private static int StableSeed(string text)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            h = (h ^ b) * 16777619u;
        }
        return unchecked((int)h);
    }

    private static long[] WindowTicks(long baseMs, int salt)
    {
        var t = new long[SweepFrames];
        for (var k = 0; k < SweepFrames; k++)
        {
            t[k] = baseMs + (long)Math.Round(k * (33.0 + (salt % 3) * 0.37));
        }
        return t;
    }

    private static void RunGate(DiskBlobCache disk, GpuContext ctx, string name, string frag, string key, Entry entry)
    {
        try
        {
            if (disk.WaitReady(TimeSpan.FromSeconds(30)) && ReadCached(disk, key) is { } known)
            {
                entry.Reason = "cached";
                entry.Verdict = known;
                return;
            }
            using var background = GpuContext.BeginBackground();
            var reason = SweepBlocking(ctx, name, frag, out var definitive);
            Conclude(key, entry, name, reason is null ? LedVerdict.Exact : LedVerdict.Rejected, reason ?? "golden sweep matched", persist: reason is null || definitive);
        }
        catch (Exception ex)
        {
            Conclude(key, entry, name, LedVerdict.Rejected, $"gate error: {ex.GetType().Name}: {ex.Message}", persist: false);
        }
    }

    /// <summary>Builds the LED program if needed and runs the sweep on the calling thread. Null when the source is exact here; otherwise why not.</summary>
    internal static string? SweepBlocking(GpuContext ctx, string name, string frag, out bool definitive)
    {
        definitive = false;
        var lp = LedWindowStore.For(ctx).Program(ctx, frag);
        if (lp is null || !lp.Done.Wait(TimeSpan.FromMinutes(3)) || lp.State != LedProgram.Ready)
        {
            return "LED program did not build: " + lp?.Error;
        }
        return Sweep(ctx, name, frag, out definitive);
    }

    /// <summary>Null when every pixel of every frame of every window matched; otherwise the first difference.</summary>
    private static string? Sweep(GpuContext ctx, string name, string frag, out bool definitive)
    {
        definitive = false;
        using var probe = new ShaderEffect(name, ctx, frag);
        var w = ctx.Width;
        var h = ctx.Height;
        var canvases = new CanvasBuffer[SweepFrames];
        for (var i = 0; i < canvases.Length; i++)
        {
            canvases[i] = new CanvasBuffer(w, h);
        }
        var specs = ShaderLibrary.IsCustomKey(name) ? ShaderParamSpec.Parse(frag) : ShaderLibrary.Params(name);
        var rng = new Random(StableSeed(frag));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long Day = 86_400_000;
        var bases = new long[]
        {
            1_000_000, now, Day - 500, 3 * Day - 495, 9 * Day + 17, 40 * Day - 1000, 20_000 * Day - 480,
            now + 12345, 123_456_789, Day - 33, 7 * Day - 990, 1_700_000_000_123,
            rng.NextInt64(1, 2_000_000_000_000), rng.NextInt64(1, 2_000_000_000_000),
            rng.NextInt64(1, 2_000_000_000_000), 2 * Day - 1,
        };
        var total = w * h;
        var px = new int[total];
        var py = new int[total];
        for (var i = 0; i < total; i++)
        {
            px[i] = i % w;
            py[i] = i / w;
        }
        var rgba = Array.Empty<byte>();
        for (var win = 0; win < SweepWindows; win++)
        {
            if (win > 0)
            {
                Randomize(probe, specs, rng);
            }
            var ticks = WindowTicks(bases[win % bases.Length], win);
            var dticks = ticks.Select(static t => (double)t).ToArray();
            for (var f = 0; f < SweepFrames; f++)
            {
                if (!RenderFull(probe, canvases[f], dticks[f]))
                {
                    return "full-canvas render did not complete";
                }
            }
            for (var start = 0; start < total; start += ChunkTaps)
            {
                var n = Math.Min(ChunkTaps, total - start);
                if (!probe.TryRenderLedWindowUnchecked(px.AsSpan(start, n), py.AsSpan(start, n), dticks, SweepFrames, ref rgba))
                {
                    return "LED-window render did not complete";
                }
                for (var f = 0; f < SweepFrames; f++)
                {
                    var pixels = canvases[f].Pixels;
                    for (var t = 0; t < n; t++)
                    {
                        var o = ((py[start + t] * w) + px[start + t]) * 3;
                        var r = (f * n + t) * 4;
                        if (pixels[o] != rgba[r] || pixels[o + 1] != rgba[r + 1] || pixels[o + 2] != rgba[r + 2])
                        {
                            definitive = true;
                            return string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"window {win} frame {f} pixel ({px[start + t]},{py[start + t]}) tick {ticks[f]}: canvas {pixels[o]},{pixels[o + 1]},{pixels[o + 2]} vs window {rgba[r]},{rgba[r + 1]},{rgba[r + 2]}");
                        }
                    }
                }
            }
        }
        return null;
    }

    private static bool RenderFull(ShaderEffect probe, CanvasBuffer canvas, double tick)
    {
        // A shader compiles in the driver's workers; the render path reports pending until it lands.
        for (var attempt = 0; attempt < 6000; attempt++)
        {
            probe.RenderFrame(canvas, tick);
            if (probe.HasFailed)
            {
                return false;
            }
            if (!probe.IsRenderPending)
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    private static void Randomize(ShaderEffect fx, IReadOnlyDictionary<string, ShaderParamSpec> specs, Random rng)
    {
        float Pick(string n, float fallback) =>
            specs.TryGetValue(n, out var s) ? s.Min + (float)rng.NextDouble() * (s.Max - s.Min) : fallback;
        fx.Speed = Pick("u_speed", 1f);
        fx.Intensity = Pick("u_intensity", 1f);
        fx.Hue = Pick("u_hue", 0f);
        fx.Colorize = Pick("u_colorize", 0f);
        fx.Saturation = Pick("u_saturation", 1f);
        fx.Contrast = Pick("u_contrast", 1f);
        var extra = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var kv in specs)
        {
            if (kv.Value.Declared && !ShaderEffect.BaseParamNames.Contains(kv.Key))
            {
                extra[kv.Key] = Pick(kv.Key, kv.Value.Default);
            }
        }
        fx.ExtraParams = extra;
    }
}
