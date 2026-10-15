#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Platform;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Throwaway diagnostic child: `--gl-session-probe user-session|system-console`.
/// Creates its own WGL 3.3 core context (hidden window, the service's pixel format),
/// renders a trivial frame at about 30 Hz for a minute and logs CPU and GL ICD thread
/// attribution every 10 s to logs/nexus-gpu-session-probe.log (part of the support
/// bundle). It touches no settings, no render-GPU state and no devices; the parent
/// owns the GpuPreference registry value.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GlSessionProbe
{
    internal const string Flag = "--gl-session-probe";
    internal const string UserSession = "user-session";
    internal const string SystemConsole = "system-console";
    internal const int RunSeconds = 60;
    internal const int HardKillSeconds = 120;
    private const string LogFile = "nexus-gpu-session-probe.log";

    internal static void Write(string variant, string message)
    {
        try
        {
            var dir = ServiceLog.LogsDirectory;
            Directory.CreateDirectory(dir);
            using var fs = new FileStream(Path.Combine(dir, LogFile), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] [gpu-session-probe] variant={variant} {message}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // Diagnostic only.
        }
    }

    public static int Run(string[] args)
    {
        var variant = args.Length > 1 ? args[1] : "unknown";
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunGl(variant);
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }
        })
        { IsBackground = true, Name = "nexus-gl" };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(RunSeconds + 20)))
        {
            Write(variant, "GL thread did not finish in time");
            return 2;
        }
        if (failure is not null)
        {
            Write(variant, $"failed: {failure}");
            return 1;
        }
        Write(variant, "done");
        return 0;
    }

    private static string Account()
    {
        using var id = WindowsIdentity.GetCurrent();
        return $"{id.Name}{(id.IsSystem ? " (LocalSystem)" : "")}";
    }

    private static unsafe void RunGl(string variant)
    {
        GpuDiagNative.ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
        Write(variant, $"start session={session} account={Account()} exe={Environment.ProcessPath}");

        var handles = WinWglContext.CreateAndMakeCurrent(160, 90, 3, 3);
        try
        {
            var gl = GL.GetApi(new WglNativeContext());
            var vendor = gl.GetStringS(StringName.Vendor);
            var renderer = gl.GetStringS(StringName.Renderer);
            var version = gl.GetStringS(StringName.Version);
            Write(variant, $"GL_VENDOR='{vendor}' GL_RENDERER='{renderer}' GL_VERSION='{version}' session={session} account={Account()}");
            var intel = renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            if (!intel)
            {
                Write(variant, "renderer not Intel; inconclusive");
            }

            var tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, 160, 90, 0, PixelFormat.Rgb, PixelType.UnsignedByte, null);
            var fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            var program = BuildProgram(gl);
            var vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            gl.UseProgram(program);
            var timeLoc = gl.GetUniformLocation(program, "t");

            var prev = new Dictionary<uint, long>();
            _ = GpuDiag.IcdReport(prev, 1);
            var lastCpu = Cpu();
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var frames = 0;
            var readback = new byte[16 * 16 * 3];
            while (sw.Elapsed < TimeSpan.FromSeconds(RunSeconds))
            {
                var frameStart = sw.Elapsed;
                gl.Viewport(0, 0, 160, 90);
                gl.ClearColor(0.1f, 0.1f, 0.1f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                gl.Uniform1(timeLoc, (float)sw.Elapsed.TotalSeconds);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                fixed (byte* p = readback)
                {
                    gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                    gl.ReadPixels(0, 0, 16, 16, PixelFormat.Rgb, PixelType.UnsignedByte, p);
                }
                frames++;
                if (sw.Elapsed - lastReport >= TimeSpan.FromSeconds(10))
                {
                    var secs = (sw.Elapsed - lastReport).TotalSeconds;
                    var cpu = Cpu();
                    var pct = (cpu - lastCpu).TotalSeconds / secs * 100.0;
                    Write(variant, $"win={secs.ToString("0.0", CultureInfo.InvariantCulture)}s frames={frames} "
                        + $"proc={pct.ToString("0.0", CultureInfo.InvariantCulture)}%core renderer='{renderer}' "
                        + GpuDiag.IcdReport(prev, secs));
                    lastReport = sw.Elapsed;
                    lastCpu = cpu;
                    frames = 0;
                }
                var wait = TimeSpan.FromMilliseconds(33) - (sw.Elapsed - frameStart);
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        finally
        {
            WinWglContext.Destroy(handles);
        }
    }

    private static TimeSpan Cpu()
    {
        using var p = Process.GetCurrentProcess();
        return p.TotalProcessorTime;
    }

    private static uint BuildProgram(GL gl)
    {
        const string vs = "#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0.0,1.0);}";
        const string fs = "#version 330 core\nuniform float t;out vec4 c;void main(){c=vec4(0.5+0.5*sin(t+gl_FragCoord.x*0.05),0.5+0.5*cos(t),0.5,1.0);}";
        var v = Compile(gl, ShaderType.VertexShader, vs);
        var f = Compile(gl, ShaderType.FragmentShader, fs);
        var prog = gl.CreateProgram();
        gl.AttachShader(prog, v);
        gl.AttachShader(prog, f);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0)
        {
            throw new InvalidOperationException("link failed: " + gl.GetProgramInfoLog(prog));
        }
        return prog;
    }

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        var s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            throw new InvalidOperationException("compile failed: " + gl.GetShaderInfoLog(s));
        }
        return s;
    }
}

/// <summary>
/// Parent side of the probe: owns the GpuPreference registry value (written before the
/// spawn, restored in a finally after the child exits or is killed), spawns the child and
/// enforces the hard kill. The helper runs the user-session variant; the service runs the
/// system-console variant through a SYSTEM token moved to the active console session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GlSessionProbeLauncher
{
    private const string PrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static int _helperStarted;
    private static int _serviceStarted;

    /// <summary>Helper process: about 60 s after start, one user-session child, preference in this user's HKCU.</summary>
    public static void StartHelperSchedule(CancellationToken stop)
    {
        if (Interlocked.Exchange(ref _helperStarted, 1) != 0)
        {
            return;
        }
        var t = new Thread(() =>
        {
            try
            {
                if (stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(60)))
                {
                    return;
                }
                RunWithPreference(GlSessionProbe.UserSession, Registry.CurrentUser, "", SpawnPlain);
            }
            catch (Exception ex)
            {
                GlSessionProbe.Write(GlSessionProbe.UserSession, $"launcher failed: {ex.GetType().Name}: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "nexus-gl-session-probe" };
        t.Start();
    }

    /// <summary>Service process: about 150 s after the first shader frame, one SYSTEM child in the console session, preference in the S-1-5-18 hive.</summary>
    public static void StartServiceSchedule()
    {
        if (Interlocked.Exchange(ref _serviceStarted, 1) != 0)
        {
            return;
        }
        var t = new Thread(() =>
        {
            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(150));
                RunWithPreference(GlSessionProbe.SystemConsole, Registry.Users, @"S-1-5-18\", SpawnSystemInConsole);
            }
            catch (Exception ex)
            {
                GlSessionProbe.Write(GlSessionProbe.SystemConsole, $"launcher failed: {ex.GetType().Name}: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "nexus-gl-session-probe" };
        t.Start();
    }

    private static void RunWithPreference(string variant, RegistryKey root, string prefix, Func<string, string, int> spawn)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            GlSessionProbe.Write(variant, "skipped: no process path");
            return;
        }
        object? previous = null;
        var kind = RegistryValueKind.String;
        var wrote = false;
        try
        {
            using (var read = root.OpenSubKey(prefix + PrefPath))
            {
                previous = read?.GetValue(exe);
                if (previous is not null)
                {
                    kind = read!.GetValueKind(exe);
                }
            }
            using (var key = root.CreateSubKey(prefix + PrefPath))
            {
                key.SetValue(exe, "GpuPreference=1;", RegistryValueKind.String);
                wrote = true;
            }
            GlSessionProbe.Write(variant, $"preference set (previous={(previous is null ? "absent" : previous.ToString())})");
            var code = spawn(variant, exe);
            GlSessionProbe.Write(variant, $"child exit code {code}");
        }
        catch (Exception ex)
        {
            GlSessionProbe.Write(variant, $"launch failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (wrote)
            {
                try
                {
                    using var key = root.CreateSubKey(prefix + PrefPath);
                    if (previous is null)
                    {
                        key.DeleteValue(exe, throwOnMissingValue: false);
                    }
                    else
                    {
                        key.SetValue(exe, previous, kind);
                    }
                    GlSessionProbe.Write(variant, "preference restored");
                }
                catch (Exception ex)
                {
                    GlSessionProbe.Write(variant, $"preference restore FAILED: {ex.Message}");
                }
            }
        }
    }

    private static int SpawnPlain(string variant, string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(GlSessionProbe.Flag);
        psi.ArgumentList.Add(variant);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        if (!p.WaitForExit(TimeSpan.FromSeconds(GlSessionProbe.HardKillSeconds)))
        {
            GlSessionProbe.Write(variant, "hard timeout, killing child");
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        return p.HasExited ? p.ExitCode : -1;
    }

    // SYSTEM token duplicated from this service, moved to the active console session.
    private static int SpawnSystemInConsole(string variant, string exe)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            GlSessionProbe.Write(variant, "skipped: no active console session (id 0xFFFFFFFF)");
            return -2;
        }
        nint token = 0;
        nint dup = 0;
        nint desktop = 0;
        var pi = new ProcessInformation();
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x2 | 0x8 | 0x1 | 0x100, out token))
            {
                GlSessionProbe.Write(variant, $"skipped: OpenProcessToken failed (Win32 {Marshal.GetLastWin32Error()})");
                return -2;
            }
            if (!DuplicateTokenEx(token, 0x02000000, 0, 2, 1, out dup))
            {
                GlSessionProbe.Write(variant, $"skipped: DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()})");
                return -2;
            }
            var sid = session;
            if (!SetTokenInformation(dup, 12, ref sid, 4))
            {
                GlSessionProbe.Write(variant, $"skipped: SetTokenInformation(TokenSessionId={session}) failed (Win32 {Marshal.GetLastWin32Error()})");
                return -2;
            }
            desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            var si = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>(), lpDesktop = desktop };
            var cmd = $"\"{exe}\" {GlSessionProbe.Flag} {variant}";
            GlSessionProbe.Write(variant, $"launching in console session {session} as SYSTEM");
            if (!CreateProcessAsUserW(dup, exe, cmd, 0, 0, false, 0x08000000, 0, null, ref si, out pi))
            {
                GlSessionProbe.Write(variant, $"skipped: CreateProcessAsUser failed (Win32 {Marshal.GetLastWin32Error()})");
                return -2;
            }
            if (WaitForSingleObject(pi.hProcess, (uint)(GlSessionProbe.HardKillSeconds * 1000)) != 0)
            {
                GlSessionProbe.Write(variant, "hard timeout, killing child");
                TerminateProcess(pi.hProcess, 1);
                WaitForSingleObject(pi.hProcess, 5000);
            }
            return GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : -1;
        }
        finally
        {
            if (pi.hProcess != 0)
            {
                CloseHandle(pi.hProcess);
            }
            if (pi.hThread != 0)
            {
                CloseHandle(pi.hThread);
            }
            if (desktop != 0)
            {
                Marshal.FreeHGlobal(desktop);
            }
            if (dup != 0)
            {
                CloseHandle(dup);
            }
            if (token != 0)
            {
                CloseHandle(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attrs, int impersonationLevel, int tokenType, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(nint token, string? application, string commandLine, nint processAttrs,
        nint threadAttrs, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo si, out ProcessInformation pi);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
#endif
