using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Offscreen OpenGL 4.1 core context on macOS via CGL (Apple's low-level GL
/// API). CGL does not need a window, an AppKit NSApp, or
/// the main thread - exactly what we need for a headless service. The
/// resulting context can be made current on the dedicated GL worker thread
/// the rest of GpuContext already owns.
///
/// Apple deprecated OpenGL on macOS (Mojave onwards) but every supported
/// macOS version still ships it.
/// </summary>
internal static class MacGlContext
{
    // ── CGL pixel-format attribute constants ───────────────────────────────
    private const int kCGLPFAAllRenderers = 1;
    private const int kCGLPFAColorSize = 8;
    private const int kCGLPFAAlphaSize = 11;
    private const int kCGLPFADepthSize = 12;
    private const int kCGLPFANoRecovery = 72;
    private const int kCGLPFAAccelerated = 73;
    private const int kCGLPFAOpenGLProfile = 99;

    // OpenGLProfile values. 0x4100 = GL 4.1 core (accepts GLSL 330 core).
    private const int kCGLOGLPVersion_GL4_Core = 0x4100;
    private const int kCGLOGLPVersion_3_2_Core = 0x3200;

    // ── CGL P/Invoke (framework stub resolves to OpenGL.framework) ─────────
    private const string OpenGL = "/System/Library/Frameworks/OpenGL.framework/OpenGL";

    [DllImport(OpenGL)]
    private static extern int CGLChoosePixelFormat(int[] attribs, out IntPtr pix, out int npix);

    [DllImport(OpenGL)]
    private static extern int CGLDestroyPixelFormat(IntPtr pix);

    [DllImport(OpenGL)]
    private static extern int CGLCreateContext(IntPtr pix, IntPtr share, out IntPtr ctx);

    [DllImport(OpenGL)]
    private static extern int CGLDestroyContext(IntPtr ctx);

    [DllImport(OpenGL)]
    private static extern int CGLSetCurrentContext(IntPtr ctx);

    [DllImport(OpenGL)]
    public static extern IntPtr CGLGetCurrentContext();

    [DllImport(OpenGL)]
    private static extern string CGLErrorString(int err);

    // ── dlopen / dlsym for loading GL function pointers ────────────────────
    private const int RTLD_LAZY = 1;

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr Dlopen(string path, int flags);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlsym")]
    private static extern IntPtr Dlsym(IntPtr handle, string symbol);

    /// <summary>
    /// Create + make-current an offscreen GL 4.1 core context on the calling
    /// thread, tries 4.1 first then falls back to 3.2. Throws on failure.
    /// </summary>
    public static IntPtr CreateAndMakeCurrent()
    {
        IntPtr pix = ChoosePixelFormat(kCGLOGLPVersion_GL4_Core);
        if (pix == IntPtr.Zero)
        {
            pix = ChoosePixelFormat(kCGLOGLPVersion_3_2_Core);
        }
        if (pix == IntPtr.Zero)
        {
            throw new InvalidOperationException("CGL: no acceptable pixel format (no GL 3.2/4.1 core available)");
        }

        int err = CGLCreateContext(pix, IntPtr.Zero, out IntPtr ctx);
        CGLDestroyPixelFormat(pix);
        if (err != 0 || ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CGLCreateContext failed: {ErrStr(err)}");
        }
        err = CGLSetCurrentContext(ctx);
        if (err != 0)
        {
            CGLDestroyContext(ctx);
            throw new InvalidOperationException($"CGLSetCurrentContext failed: {ErrStr(err)}");
        }
        return ctx;
    }

    public static void MakeCurrent(IntPtr ctx) => CGLSetCurrentContext(ctx);
    public static void ClearCurrent() => CGLSetCurrentContext(IntPtr.Zero);

    public static void Destroy(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero)
        {
            return;
        }
        try
        { CGLSetCurrentContext(IntPtr.Zero); }
        catch { }
        try
        { CGLDestroyContext(ctx); }
        catch { }
    }

    private static IntPtr ChoosePixelFormat(int profile)
    {
        // Null-terminated attribute array. We ask for accelerated,
        // color-8888 + depth, and the requested core profile.
        int[] attribs =
        {
            kCGLPFAAccelerated,
            kCGLPFANoRecovery,
            kCGLPFAColorSize, 24,
            kCGLPFAAlphaSize, 8,
            kCGLPFADepthSize, 0,
            kCGLPFAOpenGLProfile, profile,
            0,
        };
        int err = CGLChoosePixelFormat(attribs, out IntPtr pix, out int _);
        if (err != 0 || pix == IntPtr.Zero)
        {
            // Retry without hardware accel (no accelerated renderer available).
            int[] anyRenderer =
            {
                kCGLPFAAllRenderers,
                kCGLPFAColorSize, 24,
                kCGLPFAOpenGLProfile, profile,
                0,
            };
            err = CGLChoosePixelFormat(anyRenderer, out pix, out _);
            if (err != 0)
            {
                return IntPtr.Zero;
            }
        }
        return pix;
    }

    // ── GL symbol loader for Silk.NET ───────────────────────────────────────
    private static IntPtr _glHandle;

    /// <summary>Called by <see cref="CglNativeContext.GetProcAddress"/>.</summary>
    public static IntPtr LoadGlSymbol(string name)
    {
        if (_glHandle == IntPtr.Zero)
        {
            _glHandle = Dlopen(OpenGL, RTLD_LAZY);
            if (_glHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("dlopen(OpenGL.framework) failed");
            }
        }
        return Dlsym(_glHandle, name);
    }

    private static string ErrStr(int err)
    {
        try
        { return CGLErrorString(err) ?? err.ToString(); }
        catch { return err.ToString(); }
    }
}

/// <summary>
/// Silk.NET <see cref="Silk.NET.Core.Contexts.INativeContext"/> that resolves
/// GL function pointers through <see cref="MacGlContext.LoadGlSymbol"/>.
/// Used by <c>GL.GetApi(INativeContext)</c> to bind its typed function table
/// once the CGL context is current.
/// </summary>
internal sealed class CglNativeContext : Silk.NET.Core.Contexts.INativeContext
{
    public nint GetProcAddress(string procName, int? slot = null)
    {
        nint p = MacGlContext.LoadGlSymbol(procName);
        if (p == 0)
        {
            throw new InvalidOperationException($"CGL GL symbol not found: {procName}");
        }
        return p;
    }

    public bool TryGetProcAddress(string procName, out nint procAddress, int? slot = null)
    {
        procAddress = MacGlContext.LoadGlSymbol(procName);
        return procAddress != 0;
    }

    public void Dispose() { }
}
