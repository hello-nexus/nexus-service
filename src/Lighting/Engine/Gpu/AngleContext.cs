#if WINDOWS
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>What one ANGLE context owns. Pbuffer only: no window exists.</summary>
internal readonly record struct AngleHandles(IntPtr Display, IntPtr Context, IntPtr Surface)
{
    public bool Created => Context != IntPtr.Zero;
}

/// <summary>
/// Offscreen OpenGL ES 3.0 context on Windows through the bundled ANGLE
/// (libEGL + libGLESv2), translating to Direct3D 11. Chosen over the vendor GL
/// ICD because Intel's ICD spins a core for the life of the process under the
/// service account, while ANGLE only touches the D3D11 driver. The libraries
/// are loaded by full path from the angle folder beside the exe and never by
/// name, so nothing on PATH can substitute for them.
///
/// Everything here runs on the one GL thread; an EGL context is current per thread.
/// </summary>
internal static unsafe partial class AngleContext
{
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
    private const uint EglOpenGlEsApi = 0x30A0;
    private const int EglVendor = 0x3053;
    private const int EglVersion = 0x3054;
    /// <summary>eglGetError after a power event or driver reset took the device (EGL 1.1 core value).</summary>
    internal const int EglContextLost = 0x300E;

    // eglext_angle.h
    private const uint EglPlatformAngleAngle = 0x3202;
    private const int EglPlatformAngleType = 0x3203;
    private const int EglPlatformAngleTypeD3D11 = 0x3208;
    private const int EglPlatformAngleDeviceType = 0x3209;
    private const int EglPlatformAngleDeviceTypeD3DWarp = 0x320B;
    private const int EglPlatformAngleLuidHigh = 0x34A0;
    private const int EglPlatformAngleLuidLow = 0x34A1;

    private const uint LoadWithAlteredSearchPath = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

    private static readonly object LoadLock = new();
    private static bool _loaded;
    private static nint _gles;
    private static nint _egl;
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
    private static delegate* unmanaged[Cdecl]<uint, void*, int*, void*> _eglGetPlatformDisplayExt;

    /// <summary>Throws with the failing step when the libraries or an export are missing.</summary>
    private static void EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_loaded)
            {
                return;
            }
            var dir = GpuBackend.AngleDirectory;
            // libEGL imports libGLESv2 by name; having it loaded first by full path satisfies that import from this folder.
            var gles = LoadLibraryExW(Path.Combine(dir, "libGLESv2.dll"), 0, LoadWithAlteredSearchPath);
            if (gles == 0)
            {
                throw new InvalidOperationException(
                    $"could not load ANGLE libGLESv2 from '{dir}' (Win32 {Marshal.GetLastWin32Error()})");
            }
            var egl = LoadLibraryExW(Path.Combine(dir, "libEGL.dll"), 0, LoadWithAlteredSearchPath);
            if (egl == 0)
            {
                throw new InvalidOperationException(
                    $"could not load ANGLE libEGL from '{dir}' (Win32 {Marshal.GetLastWin32Error()})");
            }
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
            fixed (byte* name = "eglGetPlatformDisplayEXT\0"u8)
            {
                _eglGetPlatformDisplayExt = (delegate* unmanaged[Cdecl]<uint, void*, int*, void*>)_eglGetProcAddress(name);
            }
            if (_eglGetPlatformDisplayExt == null)
            {
                throw new InvalidOperationException("ANGLE does not export eglGetPlatformDisplayEXT");
            }
            _gles = gles;
            _egl = egl;
            _loaded = true;
        }
    }

    /// <summary>
    /// Create an ES 3.0 pbuffer context on <paramref name="target"/> and make it
    /// current on the calling thread. Throws with the EGL error on failure.
    /// </summary>
    public static AngleHandles CreateAndMakeCurrent(AngleTarget target, int width, int height)
    {
        EnsureLoaded();
        void* display = null;
        void* context = null;
        void* surface = null;
        try
        {
            var attribs = stackalloc int[10];
            var n = 0;
            attribs[n++] = EglPlatformAngleType;
            attribs[n++] = EglPlatformAngleTypeD3D11;
            if (target.Warp)
            {
                attribs[n++] = EglPlatformAngleDeviceType;
                attribs[n++] = EglPlatformAngleDeviceTypeD3DWarp;
            }
            else if (AngleAdapterSelector.TryParseLuid(target.Luid, out var high, out var low))
            {
                attribs[n++] = EglPlatformAngleLuidHigh;
                attribs[n++] = high;
                attribs[n++] = EglPlatformAngleLuidLow;
                attribs[n++] = unchecked((int)low);
            }
            else
            {
                throw new InvalidOperationException($"no usable adapter LUID in '{target.Luid}'");
            }
            attribs[n] = EglNone;
            display = _eglGetPlatformDisplayExt(EglPlatformAngleAngle, null, attribs);
            if (display == null)
            {
                throw Fail("eglGetPlatformDisplayEXT returned no display");
            }
            int major, minor;
            if (_eglInitialize(display, &major, &minor) == 0)
            {
                throw Fail("eglInitialize failed");
            }
            _eglBindApi(EglOpenGlEsApi);
            var config = stackalloc int[]
            {
                EglSurfaceType, EglPbufferBit,
                EglRenderableType, EglOpenGlEs3Bit,
                EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8,
                EglNone,
            };
            void* cfg = null;
            int count;
            if (_eglChooseConfig(display, config, &cfg, 1, &count) == 0 || count < 1)
            {
                throw Fail("no EGL config offers an ES 3 RGBA8 pbuffer");
            }
            var ctxAttribs = stackalloc int[] { EglContextClientVersion, 3, EglNone };
            context = _eglCreateContext(display, cfg, null, ctxAttribs);
            if (context == null)
            {
                throw Fail("eglCreateContext (ES 3.0) failed");
            }
            var surfAttribs = stackalloc int[] { EglWidth, Math.Max(1, width), EglHeight, Math.Max(1, height), EglNone };
            surface = _eglCreatePbufferSurface(display, cfg, surfAttribs);
            if (surface == null)
            {
                throw Fail("eglCreatePbufferSurface failed");
            }
            if (_eglMakeCurrent(display, surface, surface, context) == 0)
            {
                throw Fail("eglMakeCurrent failed");
            }
            GpuContext.Log($"[gpu] ANGLE EGL {major}.{minor} vendor='{Str(_eglQueryString(display, EglVendor))}' "
                + $"version='{Str(_eglQueryString(display, EglVersion))}' target='{target.Description}'");
            return new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface);
        }
        catch
        {
            Destroy(new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface));
            throw;
        }
    }

    /// <summary>The EGL error pending on this thread, 0x3000 when none. EGL_CONTEXT_LOST means the device went away.</summary>
    public static int PendingEglError() => _loaded ? _eglGetError() : 0x3000;

    /// <summary>Release the context. Runs on the GL thread that created it.</summary>
    public static void Destroy(AngleHandles h)
    {
        if (!_loaded || h.Display == IntPtr.Zero)
        {
            return;
        }
        try
        {
            _eglMakeCurrent((void*)h.Display, null, null, null);
            if (h.Surface != IntPtr.Zero)
            {
                _eglDestroySurface((void*)h.Display, (void*)h.Surface);
            }
            if (h.Context != IntPtr.Zero)
            {
                _eglDestroyContext((void*)h.Display, (void*)h.Context);
            }
            _eglTerminate((void*)h.Display);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Resolve a GL ES entry point: the core ones are libGLESv2 exports, the rest come from eglGetProcAddress.</summary>
    public static IntPtr LoadGlSymbol(string name)
    {
        if (!_loaded)
        {
            return IntPtr.Zero;
        }
        if (NativeLibrary.TryGetExport(_gles, name, out var p))
        {
            return p;
        }
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* b = bytes)
        {
            return (IntPtr)_eglGetProcAddress(b);
        }
    }

    private static InvalidOperationException Fail(string what) =>
        new($"{what} (EGL 0x{_eglGetError().ToString("X", CultureInfo.InvariantCulture)})");

    private static string Str(byte* s) => s == null ? "" : Marshal.PtrToStringAnsi((IntPtr)s) ?? "";
}

/// <summary>Silk.NET's loader over <see cref="AngleContext.LoadGlSymbol"/>.</summary>
internal sealed class AngleNativeContext : Silk.NET.Core.Contexts.INativeContext
{
    public nint GetProcAddress(string procName, int? slot = null)
    {
        nint p = AngleContext.LoadGlSymbol(procName);
        if (p == 0)
        {
            throw new InvalidOperationException($"ANGLE GL symbol not found: {procName}");
        }
        return p;
    }

    public bool TryGetProcAddress(string procName, out nint procAddress, int? slot = null)
    {
        procAddress = AngleContext.LoadGlSymbol(procName);
        return procAddress != 0;
    }

    public void Dispose() { }
}
#endif
