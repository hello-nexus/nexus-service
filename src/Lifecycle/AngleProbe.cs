#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lifecycle;

/// <summary>`--angle-probe`: renders through the bundled ANGLE (GLES on D3D11) per adapter, one child each so module and thread state never carry over.</summary>
internal static unsafe partial class AngleProbe
{
    private const int MeasureSeconds = 60;
    private const int SampleSeconds = 10;
    private const int FrameHz = 30;
    private const int Width = 160;
    private const int Height = 90;
    private const int WorkerWaitMs = 100_000;
    private const uint IntelVendorId = 0x8086;
    private const uint MicrosoftVendorId = 0x1414;

    private static readonly string LogPath = Path.Combine(ServiceLog.LogsDirectory, "nexus-angle-probe.log");

    internal static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [angle-probe] {line}\n");
        }
        catch (Exception)
        {
        }
        Console.WriteLine($"[angle-probe] {line}");
    }

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[1] == "--run")
            {
                return RunWorker(args[2], args[3]);
            }
            return RunOrchestrator();
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex}");
            return 1;
        }
    }

    private static int RunOrchestrator()
    {
        using (var self = Process.GetCurrentProcess())
        {
            Log($"start session={self.SessionId} account={System.Security.Principal.WindowsIdentity.GetCurrent().Name} "
                + $"exe={Environment.ProcessPath} version={typeof(AngleProbe).Assembly.GetName().Version}");
        }
        var adapters = GpuAdapterLuids.Enumerate();
        foreach (var a in adapters)
        {
            Log($"adapter luid={a.Luid} vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} vram={a.DedicatedVramMb}MB "
                + $"umd={a.UmdVersion} name='{a.Description}'");
        }
        GpuAdapterLuids.Adapter? integrated = null;
        GpuAdapterLuids.Adapter? dedicated = null;
        foreach (var a in adapters)
        {
            if (a.VendorId == MicrosoftVendorId)
            {
                continue;
            }
            if (a.VendorId == IntelVendorId && a.DedicatedVramMb < GpuAdapterLuids.DedicatedVramMinMb)
            {
                integrated ??= a;
            }
            else if (a.DedicatedVramMb >= GpuAdapterLuids.DedicatedVramMinMb)
            {
                dedicated ??= a;
            }
        }
        if (integrated is null)
        {
            Log("integrated: no Intel integrated adapter, run skipped");
        }
        else
        {
            SpawnWorker("integrated", integrated.Value.Luid);
        }
        if (dedicated is null)
        {
            Log("dedicated: no dedicated adapter, run skipped");
        }
        else
        {
            SpawnWorker("dedicated", dedicated.Value.Luid);
        }
        Log("done");
        return 0;
    }

    private static void SpawnWorker(string label, string luid)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log($"{label}: no process path, run skipped");
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--angle-probe");
            psi.ArgumentList.Add("--run");
            psi.ArgumentList.Add(label);
            psi.ArgumentList.Add(luid);
            using var p = Process.Start(psi);
            if (p is null)
            {
                Log($"{label}: worker did not start");
                return;
            }
            if (!p.WaitForExit(WorkerWaitMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                Log($"{label}: worker timed out and was killed");
                return;
            }
            Log($"{label}: worker exit={p.ExitCode}");
        }
        catch (Exception ex)
        {
            Log($"{label}: worker spawn failed: {ex.Message}");
        }
    }

    private static int RunWorker(string label, string luidText)
    {
        var parts = luidText.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var low))
        {
            Log($"{label}: bad luid '{luidText}', run skipped");
            return 2;
        }
        using (var self = Process.GetCurrentProcess())
        {
            Log($"{label}: worker pid={self.Id} session={self.SessionId} "
                + $"account={System.Security.Principal.WindowsIdentity.GetCurrent().Name} luid={luidText}");
        }
        var baseline = AngleDiagThreads.LoadedIntelModules();
        Log($"{label}: intel modules before load: [{string.Join(", ", baseline)}] icd-loaded={Yes(AngleDiagThreads.IcdLoaded(baseline))}");

        if (!LoadAngle(label))
        {
            return 3;
        }

        void* display = null;
        void* context = null;
        void* surface = null;
        try
        {
            if (!CreateContext(label, high, low, out display, out context, out surface))
            {
                return 4;
            }
            return Measure(label);
        }
        catch (Exception ex)
        {
            Log($"{label}: failed: {ex}");
            return 5;
        }
        finally
        {
            try
            {
                if (display != null)
                {
                    _eglMakeCurrent(display, null, null, null);
                    if (surface != null)
                    {
                        _eglDestroySurface(display, surface);
                    }
                    if (context != null)
                    {
                        _eglDestroyContext(display, context);
                    }
                    _eglTerminate(display);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static string Yes(bool v) => v ? "yes" : "no";

    private static bool LoadAngle(string label)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "angle");
        // libEGL imports libGLESv2 by name; loading it first by full path satisfies that import from this folder.
        var gles = LoadLibraryExW(Path.Combine(dir, "libGLESv2.dll"), 0, LoadWithAlteredSearchPath);
        if (gles == 0)
        {
            Log($"{label}: load libGLESv2 failed win32={Marshal.GetLastWin32Error()} dir={dir}");
            return false;
        }
        var egl = LoadLibraryExW(Path.Combine(dir, "libEGL.dll"), 0, LoadWithAlteredSearchPath);
        if (egl == 0)
        {
            Log($"{label}: load libEGL failed win32={Marshal.GetLastWin32Error()} dir={dir}");
            return false;
        }
        try
        {
            _eglGetProcAddress = (delegate* unmanaged[Cdecl]<byte*, void*>)NativeLibrary.GetExport(egl, "eglGetProcAddress");
            _eglInitialize = (delegate* unmanaged[Cdecl]<void*, int*, int*, uint>)NativeLibrary.GetExport(egl, "eglInitialize");
            _eglBindApi = (delegate* unmanaged[Cdecl]<uint, uint>)NativeLibrary.GetExport(egl, "eglBindAPI");
            _eglChooseConfig = (delegate* unmanaged[Cdecl]<void*, int*, void**, int, int*, uint>)NativeLibrary.GetExport(egl, "eglChooseConfig");
            _eglCreateContext = (delegate* unmanaged[Cdecl]<void*, void*, void*, int*, void*>)NativeLibrary.GetExport(egl, "eglCreateContext");
            _eglCreatePbufferSurface = (delegate* unmanaged[Cdecl]<void*, void*, int*, void*>)NativeLibrary.GetExport(egl, "eglCreatePbufferSurface");
            _eglMakeCurrent = (delegate* unmanaged[Cdecl]<void*, void*, void*, void*, uint>)NativeLibrary.GetExport(egl, "eglMakeCurrent");
            _eglGetError = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(egl, "eglGetError");
            _eglQueryString = (delegate* unmanaged[Cdecl]<void*, int, byte*>)NativeLibrary.GetExport(egl, "eglQueryString");
            _eglDestroySurface = (delegate* unmanaged[Cdecl]<void*, void*, uint>)NativeLibrary.GetExport(egl, "eglDestroySurface");
            _eglDestroyContext = (delegate* unmanaged[Cdecl]<void*, void*, uint>)NativeLibrary.GetExport(egl, "eglDestroyContext");
            _eglTerminate = (delegate* unmanaged[Cdecl]<void*, uint>)NativeLibrary.GetExport(egl, "eglTerminate");

            _glGetString = (delegate* unmanaged[Cdecl]<uint, byte*>)NativeLibrary.GetExport(gles, "glGetString");
            _glGetError = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(gles, "glGetError");
            _glCreateShader = (delegate* unmanaged[Cdecl]<uint, uint>)NativeLibrary.GetExport(gles, "glCreateShader");
            _glShaderSource = (delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)NativeLibrary.GetExport(gles, "glShaderSource");
            _glCompileShader = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(gles, "glCompileShader");
            _glGetShaderiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)NativeLibrary.GetExport(gles, "glGetShaderiv");
            _glGetShaderInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)NativeLibrary.GetExport(gles, "glGetShaderInfoLog");
            _glCreateProgram = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(gles, "glCreateProgram");
            _glAttachShader = (delegate* unmanaged[Cdecl]<uint, uint, void>)NativeLibrary.GetExport(gles, "glAttachShader");
            _glLinkProgram = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(gles, "glLinkProgram");
            _glGetProgramiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)NativeLibrary.GetExport(gles, "glGetProgramiv");
            _glGetProgramInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)NativeLibrary.GetExport(gles, "glGetProgramInfoLog");
            _glUseProgram = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(gles, "glUseProgram");
            _glGetUniformLocation = (delegate* unmanaged[Cdecl]<uint, byte*, int>)NativeLibrary.GetExport(gles, "glGetUniformLocation");
            _glUniform1f = (delegate* unmanaged[Cdecl]<int, float, void>)NativeLibrary.GetExport(gles, "glUniform1f");
            _glUniform2f = (delegate* unmanaged[Cdecl]<int, float, float, void>)NativeLibrary.GetExport(gles, "glUniform2f");
            _glGenFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(gles, "glGenFramebuffers");
            _glBindFramebuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)NativeLibrary.GetExport(gles, "glBindFramebuffer");
            _glGenRenderbuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(gles, "glGenRenderbuffers");
            _glBindRenderbuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)NativeLibrary.GetExport(gles, "glBindRenderbuffer");
            _glRenderbufferStorage = (delegate* unmanaged[Cdecl]<uint, uint, int, int, void>)NativeLibrary.GetExport(gles, "glRenderbufferStorage");
            _glFramebufferRenderbuffer = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)NativeLibrary.GetExport(gles, "glFramebufferRenderbuffer");
            _glCheckFramebufferStatus = (delegate* unmanaged[Cdecl]<uint, uint>)NativeLibrary.GetExport(gles, "glCheckFramebufferStatus");
            _glViewport = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)NativeLibrary.GetExport(gles, "glViewport");
            _glGenVertexArrays = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(gles, "glGenVertexArrays");
            _glBindVertexArray = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(gles, "glBindVertexArray");
            _glDrawArrays = (delegate* unmanaged[Cdecl]<uint, int, int, void>)NativeLibrary.GetExport(gles, "glDrawArrays");
            _glReadPixels = (delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void>)NativeLibrary.GetExport(gles, "glReadPixels");
        }
        catch (Exception ex)
        {
            Log($"{label}: export lookup failed: {ex.Message}");
            return false;
        }
        Log($"{label}: loaded ANGLE from {dir}");
        return true;
    }

    private static bool CreateContext(string label, int high, uint low, out void* display, out void* context, out void* surface)
    {
        context = null;
        surface = null;
        var getDisplay = (delegate* unmanaged[Cdecl]<uint, void*, int*, void*>)_eglGetProcAddress(Utf8("eglGetPlatformDisplayEXT"));
        if (getDisplay == null)
        {
            Log($"{label}: eglGetPlatformDisplayEXT not exported");
            display = null;
            return false;
        }
        int* displayAttribs = stackalloc int[]
        {
            EglPlatformAngleType, EglPlatformAngleTypeD3D11,
            EglPlatformAngleLuidHigh, high,
            EglPlatformAngleLuidLow, unchecked((int)low),
            EglNone,
        };
        display = getDisplay(EglPlatformAngleAngle, null, displayAttribs);
        if (display == null)
        {
            Log($"{label}: eglGetPlatformDisplayEXT returned no display egl-error=0x{_eglGetError():X}");
            return false;
        }
        int major;
        int minor;
        if (_eglInitialize(display, &major, &minor) == 0)
        {
            Log($"{label}: eglInitialize failed egl-error=0x{_eglGetError():X}");
            return false;
        }
        Log($"{label}: EGL {major}.{minor} vendor='{Str(_eglQueryString(display, EglVendor))}' "
            + $"version='{Str(_eglQueryString(display, EglVersion))}'");
        _eglBindApi(EglOpenGlEsApi);

        int* configAttribs = stackalloc int[]
        {
            EglSurfaceType, EglPbufferBit,
            EglRenderableType, EglOpenGlEs3Bit,
            EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8,
            EglNone,
        };
        void* config = null;
        int count;
        if (_eglChooseConfig(display, configAttribs, &config, 1, &count) == 0 || count < 1)
        {
            Log($"{label}: eglChooseConfig found no config egl-error=0x{_eglGetError():X}");
            return false;
        }
        int* contextAttribs = stackalloc int[] { EglContextClientVersion, 3, EglNone };
        context = _eglCreateContext(display, config, null, contextAttribs);
        if (context == null)
        {
            Log($"{label}: eglCreateContext (ES 3.0) failed egl-error=0x{_eglGetError():X}");
            return false;
        }
        int* surfaceAttribs = stackalloc int[] { EglWidth, Width, EglHeight, Height, EglNone };
        surface = _eglCreatePbufferSurface(display, config, surfaceAttribs);
        if (surface == null)
        {
            Log($"{label}: eglCreatePbufferSurface failed egl-error=0x{_eglGetError():X}");
            return false;
        }
        if (_eglMakeCurrent(display, surface, surface, context) == 0)
        {
            Log($"{label}: eglMakeCurrent failed egl-error=0x{_eglGetError():X}");
            return false;
        }
        return true;
    }

    private const string VertexSource =
        "#version 300 es\n"
        + "void main() {\n"
        + "  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n"
        + "  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n"
        + "}\n";

    private const string FragmentSource =
        "#version 300 es\n"
        + "precision highp float;\n"
        + "uniform float uTime;\n"
        + "uniform vec2 uRes;\n"
        + "out vec4 o;\n"
        + "void main() {\n"
        + "  vec2 uv = gl_FragCoord.xy / uRes;\n"
        + "  float v = 0.0;\n"
        + "  for (int i = 0; i < 4; i++) {\n"
        + "    float f = float(i + 1);\n"
        + "    v += sin(uv.x * 6.0 * f + uTime * f) * cos(uv.y * 5.0 * f - uTime * 0.7 * f);\n"
        + "  }\n"
        + "  o = vec4(0.5 + 0.5 * sin(v + uTime), 0.5 + 0.5 * sin(v * 1.3), 0.5 + 0.5 * cos(v + uTime * 0.5), 1.0);\n"
        + "}\n";

    private static int Measure(string label)
    {
        var ci = CultureInfo.InvariantCulture;
        var renderer = Str(_glGetString(GlRenderer));
        Log($"{label}: GL_VENDOR='{Str(_glGetString(GlVendor))}' GL_RENDERER='{renderer}' GL_VERSION='{Str(_glGetString(GlVersion))}'");

        var program = BuildProgram(label);
        if (program == 0)
        {
            return 6;
        }
        uint rb;
        uint fbo;
        _glGenRenderbuffers(1, &rb);
        _glBindRenderbuffer(GlRenderbuffer, rb);
        _glRenderbufferStorage(GlRenderbuffer, GlRgba8, Width, Height);
        _glGenFramebuffers(1, &fbo);
        _glBindFramebuffer(GlFramebuffer, fbo);
        _glFramebufferRenderbuffer(GlFramebuffer, GlColorAttachment0, GlRenderbuffer, rb);
        var status = _glCheckFramebufferStatus(GlFramebuffer);
        if (status != GlFramebufferComplete)
        {
            Log($"{label}: framebuffer incomplete status=0x{status:X}");
            return 7;
        }
        uint vao;
        _glGenVertexArrays(1, &vao);
        _glBindVertexArray(vao);
        _glViewport(0, 0, Width, Height);
        _glUseProgram(program);
        var uTime = _glGetUniformLocation(program, Utf8("uTime"));
        var uRes = _glGetUniformLocation(program, Utf8("uRes"));
        _glUniform2f(uRes, Width, Height);

        var pixels = new byte[Width * Height * 4];
        var prevThreads = new Dictionary<uint, long>();
        _ = AngleDiagThreads.Sample(prevThreads, 1);
        var windows = new List<(double Proc, double IntelMs, bool Icd)>();
        var intelModulesSeen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyIcd = false;
        long frames = 0;
        long windowFrames = 0;
        var lastCpu = CpuTime();
        var wall = Stopwatch.StartNew();
        var lastWall = TimeSpan.Zero;
        var window = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / FrameHz));
        while (wall.Elapsed.TotalSeconds < MeasureSeconds)
        {
            timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult();
            _glUniform1f(uTime, (float)wall.Elapsed.TotalSeconds);
            _glDrawArrays(GlTriangles, 0, 3);
            fixed (byte* px = pixels)
            {
                _glReadPixels(0, 0, Width, Height, GlRgba, GlUnsignedByte, px);
            }
            frames++;
            windowFrames++;
            if (frames == 1)
            {
                var err = _glGetError();
                var mods = AngleDiagThreads.LoadedIntelModules();
                Log($"{label}: first frame done gl-error=0x{err:X} center-pixel={pixels[(Height / 2 * Width + Width / 2) * 4]},"
                    + $"{pixels[(Height / 2 * Width + Width / 2) * 4 + 1]},{pixels[(Height / 2 * Width + Width / 2) * 4 + 2]} "
                    + $"intel-modules=[{string.Join(", ", mods)}] icd-loaded={Yes(AngleDiagThreads.IcdLoaded(mods))}");
            }
            if ((wall.Elapsed - lastWall).TotalSeconds < SampleSeconds)
            {
                continue;
            }
            var secs = (wall.Elapsed - lastWall).TotalSeconds;
            lastWall = wall.Elapsed;
            var cpu = CpuTime();
            var procPct = (cpu - lastCpu).TotalSeconds / secs * 100.0;
            lastCpu = cpu;
            var rows = AngleDiagThreads.Sample(prevThreads, secs);
            var intelRows = rows.Where(r => AngleDiagThreads.IsIntelDriver(r.Module)).ToList();
            var intelMs = intelRows.Sum(r => r.MsPerSec);
            var modules = AngleDiagThreads.LoadedIntelModules();
            var icd = AngleDiagThreads.IcdLoaded(modules);
            anyIcd |= icd;
            foreach (var m in modules)
            {
                intelModulesSeen.Add(m);
            }
            windows.Add((procPct, intelMs, icd));
            window++;
            Log($"{label}: window={window} proc={procPct.ToString("0.0", ci)}%core fps={(windowFrames / secs).ToString("0.0", ci)} "
                + $"intel-driver-threads={intelRows.Count} intel-driver-ms/s={intelMs.ToString("0.0", ci)} icd-loaded={Yes(icd)} "
                + $"intel-modules=[{string.Join(", ", modules)}] top-threads:{AngleDiagThreads.Top(rows, 5)}");
            windowFrames = 0;
            Log(SummaryLine("partial-summary"));
        }
        Log(SummaryLine("summary"));
        return 0;

        string SummaryLine(string kind)
        {
            var medProc = Median(windows.Select(w => w.Proc));
            var medIntel = Median(windows.Select(w => w.IntelMs));
            return $"{kind} adapter={label} renderer='{renderer}' windows={windows.Count} "
                + $"median-proc={medProc.ToString("0.0", ci)}%core intel-driver-ms/s={medIntel.ToString("0.0", ci)} "
                + $"icd-loaded={Yes(anyIcd)} avg-fps={(frames / Math.Max(wall.Elapsed.TotalSeconds, 0.001)).ToString("0.0", ci)} "
                + $"intel-modules=[{string.Join(", ", intelModulesSeen)}]";
        }
    }

    private static TimeSpan CpuTime()
    {
        using var p = Process.GetCurrentProcess();
        return p.TotalProcessorTime;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0)
        {
            return 0;
        }
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    private static uint BuildProgram(string label)
    {
        var vs = Compile(label, GlVertexShader, VertexSource);
        var fs = Compile(label, GlFragmentShader, FragmentSource);
        if (vs == 0 || fs == 0)
        {
            return 0;
        }
        var program = _glCreateProgram();
        _glAttachShader(program, vs);
        _glAttachShader(program, fs);
        _glLinkProgram(program);
        int ok;
        _glGetProgramiv(program, GlLinkStatus, &ok);
        if (ok == 0)
        {
            var buf = stackalloc byte[1024];
            int len;
            _glGetProgramInfoLog(program, 1024, &len, buf);
            Log($"{label}: program link failed: {Encoding.UTF8.GetString(buf, Math.Max(len, 0))}");
            return 0;
        }
        return program;
    }

    private static uint Compile(string label, uint type, string source)
    {
        var shader = _glCreateShader(type);
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = bytes)
        {
            byte* pp = p;
            int len = bytes.Length;
            _glShaderSource(shader, 1, &pp, &len);
        }
        _glCompileShader(shader);
        int ok;
        _glGetShaderiv(shader, GlCompileStatus, &ok);
        if (ok == 0)
        {
            var buf = stackalloc byte[1024];
            int outLen;
            _glGetShaderInfoLog(shader, 1024, &outLen, buf);
            Log($"{label}: shader compile failed: {Encoding.UTF8.GetString(buf, Math.Max(outLen, 0))}");
            return 0;
        }
        return shader;
    }

    // Never freed: the process is short-lived.
    private static byte* Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        var mem = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(mem, bytes.Length));
        return mem;
    }

    private static string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

    private const uint LoadWithAlteredSearchPath = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    private const int EglNone = 0x3038;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenGlEs3Bit = 0x0040;
    private const int EglRedSize = 0x3024;
    private const int EglGreenSize = 0x3023;
    private const int EglBlueSize = 0x3022;
    private const int EglAlphaSize = 0x3021;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;
    private const int EglContextClientVersion = 0x3098;
    private const int EglVendor = 0x3053;
    private const int EglVersion = 0x3054;
    private const uint EglOpenGlEsApi = 0x30A0;
    private const uint EglPlatformAngleAngle = 0x3202;
    private const int EglPlatformAngleType = 0x3203;
    private const int EglPlatformAngleTypeD3D11 = 0x3208;
    private const int EglPlatformAngleLuidHigh = 0x34A0;
    private const int EglPlatformAngleLuidLow = 0x34A1;

    private const uint GlVendor = 0x1F00;
    private const uint GlRenderer = 0x1F01;
    private const uint GlVersion = 0x1F02;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlRenderbuffer = 0x8D41;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlRgba8 = 0x8058;
    private const uint GlRgba = 0x1908;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlTriangles = 0x0004;

    private static delegate* unmanaged[Cdecl]<byte*, void*> _eglGetProcAddress;
    private static delegate* unmanaged[Cdecl]<void*, int*, int*, uint> _eglInitialize;
    private static delegate* unmanaged[Cdecl]<uint, uint> _eglBindApi;
    private static delegate* unmanaged[Cdecl]<void*, int*, void**, int, int*, uint> _eglChooseConfig;
    private static delegate* unmanaged[Cdecl]<void*, void*, void*, int*, void*> _eglCreateContext;
    private static delegate* unmanaged[Cdecl]<void*, void*, int*, void*> _eglCreatePbufferSurface;
    private static delegate* unmanaged[Cdecl]<void*, void*, void*, void*, uint> _eglMakeCurrent;
    private static delegate* unmanaged[Cdecl]<int> _eglGetError;
    private static delegate* unmanaged[Cdecl]<void*, int, byte*> _eglQueryString;
    private static delegate* unmanaged[Cdecl]<void*, void*, uint> _eglDestroySurface;
    private static delegate* unmanaged[Cdecl]<void*, void*, uint> _eglDestroyContext;
    private static delegate* unmanaged[Cdecl]<void*, uint> _eglTerminate;

    private static delegate* unmanaged[Cdecl]<uint, byte*> _glGetString;
    private static delegate* unmanaged[Cdecl]<uint> _glGetError;
    private static delegate* unmanaged[Cdecl]<uint, uint> _glCreateShader;
    private static delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> _glShaderSource;
    private static delegate* unmanaged[Cdecl]<uint, void> _glCompileShader;
    private static delegate* unmanaged[Cdecl]<uint, uint, int*, void> _glGetShaderiv;
    private static delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> _glGetShaderInfoLog;
    private static delegate* unmanaged[Cdecl]<uint> _glCreateProgram;
    private static delegate* unmanaged[Cdecl]<uint, uint, void> _glAttachShader;
    private static delegate* unmanaged[Cdecl]<uint, void> _glLinkProgram;
    private static delegate* unmanaged[Cdecl]<uint, uint, int*, void> _glGetProgramiv;
    private static delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> _glGetProgramInfoLog;
    private static delegate* unmanaged[Cdecl]<uint, void> _glUseProgram;
    private static delegate* unmanaged[Cdecl]<uint, byte*, int> _glGetUniformLocation;
    private static delegate* unmanaged[Cdecl]<int, float, void> _glUniform1f;
    private static delegate* unmanaged[Cdecl]<int, float, float, void> _glUniform2f;
    private static delegate* unmanaged[Cdecl]<int, uint*, void> _glGenFramebuffers;
    private static delegate* unmanaged[Cdecl]<uint, uint, void> _glBindFramebuffer;
    private static delegate* unmanaged[Cdecl]<int, uint*, void> _glGenRenderbuffers;
    private static delegate* unmanaged[Cdecl]<uint, uint, void> _glBindRenderbuffer;
    private static delegate* unmanaged[Cdecl]<uint, uint, int, int, void> _glRenderbufferStorage;
    private static delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void> _glFramebufferRenderbuffer;
    private static delegate* unmanaged[Cdecl]<uint, uint> _glCheckFramebufferStatus;
    private static delegate* unmanaged[Cdecl]<int, int, int, int, void> _glViewport;
    private static delegate* unmanaged[Cdecl]<int, uint*, void> _glGenVertexArrays;
    private static delegate* unmanaged[Cdecl]<uint, void> _glBindVertexArray;
    private static delegate* unmanaged[Cdecl]<uint, int, int, void> _glDrawArrays;
    private static delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void> _glReadPixels;
}
#endif
