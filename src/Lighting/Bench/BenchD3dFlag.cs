#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Bench;

/// <summary>
/// A/B of the D3D11 device flag that stops the driver from spawning its own worker thread. The device is created
/// here and handed to ANGLE through the device-creation extension, with and without the flag. Separate harness from
/// the service's own context: one dedicated GL thread per device, the same hop the service pays per call.
/// </summary>
internal static partial class LightingBench
{
    private const uint D3dPreventThreading = 0x200;
    private const int D3dTicks30 = 14;
    private const int D3dSliceFrames = 8;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels, uint nLevels, uint sdk, out nint device, out uint level, out nint context);

    [DllImport("kernel32.dll")]
    private static extern nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint tid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint h);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(nint thread, int cls, out nint info, int len, out int ret);

    private static unsafe nint VTable(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static unsafe uint ComRelease(nint obj) => ((delegate* unmanaged<nint, uint>)VTable(obj, 2))(obj);

    /// <summary>A hardware D3D11 device on the adapter with the given LUID, created with the given flags.</summary>
    private static unsafe nint CreateD3dDevice(string luid, uint flags)
    {
        if (!AngleAdapterSelector.TryParseLuid(luid, out var high, out var low))
        {
            throw new InvalidOperationException($"no usable LUID in '{luid}'");
        }
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var hr = CreateDXGIFactory1(ref iid, out var factory);
        if (hr < 0)
        {
            throw new InvalidOperationException($"CreateDXGIFactory1 0x{hr:X}");
        }
        nint adapter = 0;
        try
        {
            var desc = stackalloc byte[320];
            for (uint i = 0; ; i++)
            {
                nint a;
                if (((delegate* unmanaged<nint, uint, nint*, int>)VTable(factory, 12))(factory, i, &a) < 0)
                {
                    break;
                }
                if (((delegate* unmanaged<nint, byte*, int>)VTable(a, 10))(a, desc) >= 0
                    && *(uint*)(desc + 296) == low && *(int*)(desc + 300) == high)
                {
                    adapter = a;
                    break;
                }
                ComRelease(a);
            }
            if (adapter == 0)
            {
                throw new InvalidOperationException($"adapter {luid} not found");
            }
            hr = D3D11CreateDevice(adapter, 0, 0, flags, 0, 0, 7, out var device, out _, out var context);
            if (hr < 0)
            {
                throw new InvalidOperationException($"D3D11CreateDevice 0x{hr:X}");
            }
            ComRelease(context);
            return device;
        }
        finally
        {
            if (adapter != 0)
            {
                ComRelease(adapter);
            }
            ComRelease(factory);
        }
    }

    private sealed class LsEnv : ILsGl, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _done = new();
        private Exception? _error;
        private Exception? _callError;
        private nint _device;
        private AngleHandles _handles;
        public GL Gl { get; private set; } = null!;
        public uint Quad { get; private set; }
        public uint Fbo { get; private set; }
        public int CanvasH => 90;
        public int Width => 160;
        public int Height => 90;
        public uint[] Pbos { get; } = new uint[D3dSliceFrames];

        public LsEnv(string luid, uint flags)
        {
            _thread = new Thread(() => Main(luid, flags)) { IsBackground = true, Name = "ls-gl" };
            _thread.Start();
            _ready.Wait();
            if (_error is not null)
            {
                throw new InvalidOperationException(_error.Message, _error);
            }
        }

        private unsafe void Main(string luid, uint flags)
        {
            try
            {
                _device = CreateD3dDevice(luid, flags);
                _handles = AngleContext.BenchCreateOnDevice(_device, Width, Height);
                var gl = GL.GetApi(new AngleNativeContext());
                Gl = gl;
                var tex = gl.GenTexture();
                gl.BindTexture(TextureTarget.Texture2D, tex);
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
                Fbo = gl.GenFramebuffer();
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                Quad = gl.GenVertexArray();
                var vbo = gl.GenBuffer();
                gl.BindVertexArray(Quad);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
                Span<float> quad = stackalloc float[] { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
                fixed (float* p = quad)
                {
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
                gl.BindVertexArray(0);
                for (var i = 0; i < Pbos.Length; i++)
                {
                    Pbos[i] = gl.GenBuffer();
                    gl.BindBuffer(BufferTargetARB.PixelPackBuffer, Pbos[i]);
                    gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)(Width * Height * 4), null, BufferUsageARB.StreamRead);
                }
                gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            }
            catch (Exception ex)
            {
                _error = ex;
                _ready.Set();
                return;
            }
            _ready.Set();
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }

        public void Invoke(Action work)
        {
            _done.Reset();
            _callError = null;
            _queue.Add(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    _callError = ex;
                }
                finally
                {
                    _done.Set();
                }
            });
            _done.Wait();
            if (_callError is not null)
            {
                throw new InvalidOperationException(_callError.Message, _callError);
            }
        }

        public void Dispose()
        {
            try
            {
                Invoke(() => AngleContext.Destroy(_handles));
            }
            catch (Exception)
            {
            }
            _queue.CompleteAdding();
            if (_device != 0)
            {
                ComRelease(_device);
            }
        }
    }

    private sealed class ThreadCpuSnapshot
    {
        public readonly Dictionary<int, ulong> Cycles = new();

        public static ThreadCpuSnapshot Take()
        {
            var snap = new ThreadCpuSnapshot();
            using var p = Process.GetCurrentProcess();
            foreach (ProcessThread t in p.Threads)
            {
                var h = OpenThread(0x40 | 0x800, false, (uint)t.Id);
                if (h == 0)
                {
                    continue;
                }
                if (QueryThreadCycleTime(h, out var c))
                {
                    snap.Cycles[t.Id] = c;
                }
                CloseHandle(h);
            }
            return snap;
        }

        public static List<BenchThreadCpu> Diff(ThreadCpuSnapshot a, ThreadCpuSnapshot b, double cyclesPerMs, double seconds)
        {
            using var p = Process.GetCurrentProcess();
            var mods = new List<(long Base, long End, string Name)>();
            foreach (ProcessModule m in p.Modules)
            {
                mods.Add(((long)m.BaseAddress, (long)m.BaseAddress + m.ModuleMemorySize, m.ModuleName));
            }
            var list = new List<BenchThreadCpu>();
            foreach (var kv in b.Cycles)
            {
                a.Cycles.TryGetValue(kv.Key, out var before);
                var ms = (kv.Value - before) / cyclesPerMs / seconds;
                if (ms < 0.05)
                {
                    continue;
                }
                var name = "?";
                var h = OpenThread(0x40 | 0x800, false, (uint)kv.Key);
                if (h != 0)
                {
                    if (NtQueryInformationThread(h, 9, out var addr, nint.Size, out _) == 0)
                    {
                        name = "unknown";
                        foreach (var m in mods)
                        {
                            if (addr >= m.Base && addr < m.End)
                            {
                                name = m.Name;
                                break;
                            }
                        }
                    }
                    CloseHandle(h);
                }
                list.Add(new BenchThreadCpu { Module = name, ThreadId = kv.Key, CpuMsPerSec = ms });
            }
            return list.OrderByDescending(static t => t.CpuMsPerSec).ToList();
        }
    }

    private static void RunD3dFlag(BenchOptions o, GpuContext ctx, BenchComboResult result)
    {
        // The service context would add its own driver threads to every per-thread reading.
        ctx.Dispose();
        var section = new BenchD3dFlagSection();
        result.D3dFlag = section;
        result.Status = "partial";
        try
        {
            section.Extensions = AngleContext.BenchClientExtensions();
        }
        catch (Exception ex)
        {
            section.Detail = $"could not read EGL extensions: {ex.Message}";
            result.Status = "ok";
            return;
        }
        section.DeviceCreationAvailable = section.Extensions.Contains("EGL_ANGLE_device_creation_d3d11", StringComparison.Ordinal);
        if (!section.DeviceCreationAvailable)
        {
            section.Detail = "EGL_ANGLE_device_creation_d3d11 is not offered by this ANGLE build; nothing measured";
            result.Status = "ok";
            return;
        }
        if (string.IsNullOrEmpty(o.Luid))
        {
            section.Detail = "needs a hardware adapter (--adapters dedicated or integrated)";
            result.Status = "ok";
            return;
        }
        var cyclesPerMs = CalibrateCyclesPerMs();
        var names = o.Effects ?? new[] { "plasma", "fire" };
        foreach (var name in names)
        {
            foreach (var scenario in new[] { "live", "live+P0 windows", "live+P2 windows" })
            {
                foreach (var flag in new[] { false, true })
                {
                    Console.WriteLine($"d3dflag {name} {scenario} flag {(flag ? "on" : "off")}");
                    try
                    {
                        section.Runs.Add(RunD3dScenario(o, name, scenario, flag, cyclesPerMs));
                    }
                    catch (Exception ex)
                    {
                        section.Runs.Add(new BenchD3dRun { Effect = name, Scenario = scenario, PreventThreading = flag });
                        section.Detail = $"{name} {scenario} flag {flag}: {ex.GetType().Name}: {ex.Message}";
                        Console.WriteLine(section.Detail);
                    }
                    SaveChild(o.OutPath, result);
                }
            }
        }
        result.Status = "ok";
    }

    private static BenchD3dRun RunD3dScenario(BenchOptions o, string name, string scenario, bool flag, double cyclesPerMs)
    {
        var run = new BenchD3dRun { Effect = name, Scenario = scenario, PreventThreading = flag };
        var frag = ShaderLibrary.Get(name);
        var rewritten = LedSpaceCore.Rewrite(frag, LsWindow) ?? throw new InvalidOperationException("rewrite failed");
        var specs = ShaderParamSpec.Parse(frag);
        var (u, v) = LedSpaceCore.StrimerMap();
        var def = LsPlacementDefs[0];
        var dev = new DeviceFrame(0, "ls-strimer", u.Length, def.X, def.Y, def.W, def.H, def.Rotation) { LedU = u, LedV = v };
        var place = new LsPlacementState { Def = def, Device = dev, Layout = LedSpaceCore.BuildTaps(dev, 160, 90) };
        using var env = new LsEnv(o.Luid!, flag ? D3dPreventThreading : 0u);
        using var engine = new LightingEngine();
        LsCreatePlacementGl(env, place);
        LsProgram? orig = null;
        LsProgram? led = null;
        env.Invoke(() =>
        {
            orig = LsBuild(env.Gl, true, frag, 160, 90, specs);
            led = LsBuild(env.Gl, true, rewritten, 160, 90, specs);
        });
        var draw = new LsLedDraw(env);
        var canvas = new CanvasBuffer(160, 90);
        var devs = new[] { dev };
        var rgba = new byte[160 * 90 * 4];
        var batch = new byte[D3dSliceFrames][];
        for (var i = 0; i < batch.Length; i++)
        {
            batch[i] = new byte[160 * 90 * 4];
        }
        var tapRgb = new byte[place.Layout.TotalTaps * 3];
        var ledRgb = new byte[u.Length * 3];
        var times = new long[LsWindow];
        float utime = 0;

        void DrawOrig(GL gl)
        {
            LsRasterState(gl, env.Quad);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, env.Fbo);
            gl.Viewport(0, 0, 160, 90);
            gl.UseProgram(orig!.Handle);
            gl.Uniform1(orig.UTime, utime);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        Action liveFrame = () =>
        {
            var gl = env.Gl;
            DrawOrig(gl);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            unsafe
            {
                fixed (byte* p = rgba)
                {
                    gl.ReadPixels(0, 0, 160, 90, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        };

        void SampleCanvas()
        {
            canvas.WriteFromGl(rgba, true);
            engine.SampleForBench(devs, canvas);
            dev.Publish();
        }

        int slot = 0;
        Action queueFrame = () =>
        {
            var gl = env.Gl;
            DrawOrig(gl);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, env.Pbos[slot]);
            unsafe
            {
                gl.ReadPixels(0, 0, 160, 90, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
            }
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        };
        int mapCount = 0;
        Action mapAll = () =>
        {
            var gl = env.Gl;
            for (var k = 0; k < mapCount; k++)
            {
                gl.BindBuffer(BufferTargetARB.PixelPackBuffer, env.Pbos[k]);
                unsafe
                {
                    var p = gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, (nuint)(160 * 90 * 4), MapBufferAccessMask.ReadBit);
                    if (p is not null)
                    {
                        new ReadOnlySpan<byte>(p, 160 * 90 * 4).CopyTo(batch[k]);
                        gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
                    }
                }
            }
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        };

        // Window byte equality: the LED-space window against the full-canvas frames, once per device configuration.
        var start0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < LsWindow; i++)
        {
            times[i] = start0 + (long)i * PeriodMs;
        }
        var equal = true;
        var want = new byte[LsWindow][];
        for (var f = 0; f < LsWindow; f++)
        {
            utime = LedSpaceCore.UTime(times[f]);
            env.Invoke(liveFrame);
            SampleCanvas();
            want[f] = dev.LedBytes.ToArray();
        }
        for (var f = 0; f < LsWindow; f++)
        {
            draw.Times[f] = LedSpaceCore.UTime(times[f]);
        }
        draw.Go(led!, place, LsWindow, read: true);
        for (var f = 0; f < LsWindow; f++)
        {
            LsGatherTaps(draw.Pixels, place.Width, f, tapRgb);
            LedSpaceCore.Resolve(place.Layout, tapRgb, ledRgb);
            equal &= want[f].AsSpan().SequenceEqual(ledRgb);
        }
        run.WindowBytesEqual = equal;

        void P0Slice(int at, int n)
        {
            for (var k = 0; k < n; k++)
            {
                utime = LedSpaceCore.UTime(times[at + k]);
                slot = k;
                env.Invoke(queueFrame);
            }
            mapCount = n;
            env.Invoke(mapAll);
            for (var k = 0; k < n; k++)
            {
                System.Buffer.BlockCopy(batch[k], 0, rgba, 0, rgba.Length);
                SampleCanvas();
            }
        }

        void P2Window()
        {
            for (var f = 0; f < LsWindow; f++)
            {
                draw.Times[f] = LedSpaceCore.UTime(times[f]);
            }
            draw.Go(led!, place, LsWindow, read: true);
            for (var f = 0; f < LsWindow; f++)
            {
                LsGatherTaps(draw.Pixels, place.Width, f, tapRgb);
                LedSpaceCore.Resolve(place.Layout, tapRgb, ledRgb);
            }
        }

        var ticks = Math.Max(1, o.Seconds) * TargetHz;
        var interval = new List<double>(ticks);
        var work = new List<double>(ticks);
        var windows = new List<double>();
        var toMs = 1000.0 / Stopwatch.Frequency;
        var winDone = LsWindow;
        long winStart = 0;
        long prev = 0;
        ThreadCpuSnapshot? snapA = null;
        var cyc0 = 0UL;
        Stopwatch? wall = null;
        using var pacer = new Pacer();
        const int warm = 45;
        for (var i = -warm; i < ticks; i++)
        {
            pacer.Wait();
            if (i == 0)
            {
                snapA = ThreadCpuSnapshot.Take();
                cyc0 = ProcessCycles();
                wall = Stopwatch.StartNew();
                prev = 0;
            }
            var t0 = Stopwatch.GetTimestamp();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            utime = LedSpaceCore.UTime(nowMs);
            env.Invoke(liveFrame);
            SampleCanvas();
            if (scenario != "live")
            {
                if (i % D3dTicks30 == 0)
                {
                    for (var k = 0; k < LsWindow; k++)
                    {
                        times[k] = nowMs + (long)(k + 1) * PeriodMs;
                    }
                    winDone = 0;
                    winStart = t0;
                }
                if (scenario == "live+P2 windows" && winDone == 0)
                {
                    P2Window();
                    winDone = LsWindow;
                    if (i >= 0)
                    {
                        windows.Add((Stopwatch.GetTimestamp() - winStart) * toMs);
                    }
                }
                else if (scenario == "live+P0 windows" && winDone < LsWindow)
                {
                    var n = Math.Min(D3dSliceFrames, LsWindow - winDone);
                    P0Slice(winDone, n);
                    winDone += n;
                    if (winDone == LsWindow && i >= 0)
                    {
                        windows.Add((Stopwatch.GetTimestamp() - winStart) * toMs);
                    }
                }
            }
            var t1 = Stopwatch.GetTimestamp();
            if (i >= 0)
            {
                if (prev != 0)
                {
                    interval.Add((t0 - prev) * toMs);
                }
                work.Add((t1 - t0) * toMs);
            }
            prev = t0;
        }
        var seconds = wall!.Elapsed.TotalSeconds;
        var cyc1 = ProcessCycles();
        var snapB = ThreadCpuSnapshot.Take();
        run.Ticks = ticks;
        run.IntervalMs = BenchStats.Summarize(interval.ToArray());
        run.WorkMs = BenchStats.Summarize(work.ToArray());
        run.WindowMs = windows.Count == 0 ? null : BenchStats.Summarize(windows.ToArray());
        run.ProcessCpuMsPerSec = (cyc1 - cyc0) / cyclesPerMs / seconds;
        run.Threads = ThreadCpuSnapshot.Diff(snapA!, snapB, cyclesPerMs, seconds);
        run.DriverCpuMsPerSec = run.Threads
            .Where(static t => t.Module.StartsWith("nv", StringComparison.OrdinalIgnoreCase))
            .Sum(static t => t.CpuMsPerSec);
        run.Threads = run.Threads.Take(10).ToList();
        env.Invoke(() =>
        {
            env.Gl.DeleteProgram(orig!.Handle);
            env.Gl.DeleteProgram(led!.Handle);
        });
        LsDestroyPlacementGl(env, place);
        return run;
    }
}
#endif
