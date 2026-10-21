#if WINDOWS
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>What one ANGLE context owns. Pbuffer only: no window exists.</summary>
internal readonly record struct AngleHandles(IntPtr Display, IntPtr Context, IntPtr Surface, nint Device = 0, nint EglDevice = 0)
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
    private const int EglExtensions = 0x3055;
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

    // EGL_ANDROID_blob_cache: ANGLE hands every compiled program to the set callback and
    // asks the get callback before compiling. One process-wide store, shared across contexts.
    private const long ProgramCacheCapBytes = 128L * 1024 * 1024;
    private const int MaxBlobKeyBytes = 64;
    private static readonly DiskBlobCache ProgramCache = new(
        Path.Combine(BlobCachePolicy.CacheRoot(), "angle-programs"),
        ProgramCacheCapBytes, BlobCachePolicy.Allowed);
    // ANGLE may link on its own worker threads, so the size-query/copy memo is per thread.
    [ThreadStatic]
    private static string? _lastGetName;
    [ThreadStatic]
    private static byte[]? _lastGetBytes;
    // Displays whose callbacks are installed: ANGLE accepts them once per display.
    private static readonly System.Collections.Generic.HashSet<nint> BlobCacheDisplays = new();

    /// <summary>The blob cache callbacks are installed and the cache directory is allowed, so compiled programs persist.</summary>
    internal static bool ProgramCacheActive
    {
        get
        {
            lock (BlobCacheDisplays)
            {
                return BlobCacheDisplays.Count > 0 && BlobCachePolicy.Allowed();
            }
        }
    }

    private static string Short(string hex) => hex.Length > 8 ? hex[..8] : hex;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void BlobSet(void* key, nint keySize, void* value, nint valueSize)
    {
        try
        {
            if (key == null || value == null || keySize <= 0 || keySize > MaxBlobKeyBytes || valueSize <= 0)
            {
                return;
            }
            var name = DiskBlobCache.HexName(new ReadOnlySpan<byte>(key, (int)keySize));
            ProgramCache.Put(name, new ReadOnlySpan<byte>(value, (int)valueSize));
            GpuContext.Log($"[gpu] program cache store {Short(name)} {valueSize} bytes");
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static nint BlobGet(void* key, nint keySize, void* value, nint valueSize)
    {
        try
        {
            if (key == null || keySize <= 0 || keySize > MaxBlobKeyBytes)
            {
                return 0;
            }
            var name = DiskBlobCache.HexName(new ReadOnlySpan<byte>(key, (int)keySize));
            byte[]? bytes;
            if (_lastGetName == name && _lastGetBytes is not null)
            {
                bytes = _lastGetBytes;
            }
            else
            {
                bytes = ProgramCache.TryGet(name);
                if (bytes is null)
                {
                    return 0;
                }
                _lastGetName = name;
                _lastGetBytes = bytes;
            }
            if (value != null && valueSize >= bytes.Length)
            {
                bytes.AsSpan().CopyTo(new Span<byte>(value, bytes.Length));
                GpuContext.Log($"[gpu] program cache hit {Short(name)} {bytes.Length} bytes");
                _lastGetName = null;
                _lastGetBytes = null;
            }
            return bytes.Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Persist compiled programs across restarts and context re-creates. Silent no-op when ANGLE lacks the extension.</summary>
    private static void EnableProgramCache(void* display)
    {
        try
        {
            lock (BlobCacheDisplays)
            {
                if (BlobCacheDisplays.Contains((nint)display))
                {
                    return;
                }
            }
            var extensions = Str(_eglQueryString(display, EglExtensions));
            if (!extensions.Contains("EGL_ANDROID_blob_cache", StringComparison.Ordinal))
            {
                GpuContext.Log("[gpu] program cache: EGL_ANDROID_blob_cache not offered, compiling every start");
                return;
            }
            void* setter;
            fixed (byte* name = "eglSetBlobCacheFuncsANDROID\0"u8)
            {
                setter = _eglGetProcAddress(name);
            }
            if (setter == null)
            {
                GpuContext.Log("[gpu] program cache: eglSetBlobCacheFuncsANDROID missing");
                return;
            }
            _eglGetError();
            ((delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, nint, void*, nint, void>, delegate* unmanaged[Cdecl]<void*, nint, void*, nint, nint>, void>)setter)(
                display, &BlobSet, &BlobGet);
            var err = _eglGetError();
            if (err != 0x3000)
            {
                GpuContext.Log($"[gpu] program cache: eglSetBlobCacheFuncsANDROID failed (EGL 0x{err.ToString("X", CultureInfo.InvariantCulture)})");
                return;
            }
            lock (BlobCacheDisplays)
            {
                BlobCacheDisplays.Add((nint)display);
            }
            GpuContext.Log($"[gpu] program cache: EGL_ANDROID_blob_cache installed, dir '{ProgramCache.Directory}'");
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] program cache unavailable: {ex.Message}");
        }
    }

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
        if (PreventDriverThreading && !target.Warp)
        {
            try
            {
                return CreateCore(target, width, height, ownDevice: true);
            }
            catch (Exception ex)
            {
                GpuContext.Log($"[gpu] context on the prevent-threading device failed ({ex.Message}); using ANGLE's own device");
            }
        }
        return CreateCore(target, width, height, ownDevice: false);
    }

    private static AngleHandles CreateCore(AngleTarget target, int width, int height, bool ownDevice)
    {
        void* display = null;
        void* context = null;
        void* surface = null;
        nint ownedDevice = 0;
        nint ownedEglDevice = 0;
        int major = 0, minor = 0;
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
            display = ownDevice ? TryPreventThreadingDisplay(target, out ownedDevice, out ownedEglDevice, out major, out minor) : null;
            if (display == null)
            {
                display = _eglGetPlatformDisplayExt(EglPlatformAngleAngle, null, attribs);
                if (display == null)
                {
                    throw Fail("eglGetPlatformDisplayEXT returned no display");
                }
                if (_eglInitialize(display, &major, &minor) == 0)
                {
                    throw Fail("eglInitialize failed");
                }
            }
            EnableProgramCache(display);
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
            return new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface, ownedDevice, ownedEglDevice);
        }
        catch
        {
            Destroy(new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface, ownedDevice, ownedEglDevice));
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
        // The EGL device holds its own reference on the D3D device, so it goes first, after the display that used it.
        ReleaseEglDevice(h.EglDevice);
        try
        {
            D3dDeviceFactory.Release(h.Device);
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
