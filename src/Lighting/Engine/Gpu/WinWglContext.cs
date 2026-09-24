#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Handles one WGL context owns: the window it was made against, that
/// window's DC, and the render context itself.</summary>
internal readonly record struct WglHandles(IntPtr Hwnd, IntPtr Dc, IntPtr Rc)
{
    public bool Created => Rc != IntPtr.Zero;
}

/// <summary>
/// Offscreen OpenGL 3.3 core context on Windows, created straight against WGL
/// the way the macOS path uses CGL and the Linux path uses EGL.
///
/// GLFW wrapped this same primitive, and its glfwInit measured 30.1s under
/// LocalSystem on the T1 bench (AMD iGPU + RTX 5080) against 1.0s for the
/// logged-in user, while the WGL calls below took 83ms in that same service
/// account. The service pays init on every start, so that was the whole of
/// "lighting takes half a minute to come back" after a restart or a reset.
///
/// Everything here must run on the one GL thread: a WGL context is current per
/// thread, and Win32 only lets the creating thread destroy its window.
/// </summary>
internal static class WinWglContext
{
    // ── Win32 ──────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpenGl = 0x00000020;
    private const uint PfdDoubleBuffer = 0x00000001;
    // Set when the format is Microsoft's GDI software renderer; paired with
    // PfdGenericAccelerated it means a hardware ICD behind a generic wrapper.
    private const uint PfdGenericFormat = 0x00000040;
    private const uint PfdGenericAccelerated = 0x00001000;

    private const uint GlVersion = 0x1F02;

    // Context-creation attributes (WGL_ARB_create_context).
    private const int ContextMajorVersionArb = 0x2091;
    private const int ContextMinorVersionArb = 0x2092;
    private const int ContextProfileMaskArb = 0x9126;
    private const int ContextCoreProfileBitArb = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private const uint CsOwnDc = 0x0020;
    private const int ErrorClassAlreadyExists = 1410;
    private const string ClassName = "NexusGpuGlWindow";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibraryA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int DescribePixelFormat(IntPtr dc, int format, uint size, ref PixelFormatDescriptor pfd);

    [DllImport("opengl32.dll")]
    private static extern IntPtr glGetString(uint name);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglCreateContext(IntPtr dc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglDeleteContext(IntPtr rc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr wglGetProcAddress(string name);

    // opengl32.dll, held for the lifetime of the process: every core GL entry
    // point is resolved out of it (wglGetProcAddress only answers for
    // extensions, and returns a small non-null sentinel for the rest).
    private static IntPtr _openGl32;

    // Registered once per process; a retry reuses it.
    private static bool _classRegistered;

    /// <summary>
    /// Create an offscreen GL context of the requested version and make it
    /// current on the calling thread. Throws with the Win32 error on failure.
    /// </summary>
    public static WglHandles CreateAndMakeCurrent(int width, int height, int major, int minor)
    {
        var hwnd = IntPtr.Zero;
        var dc = IntPtr.Zero;
        var rc = IntPtr.Zero;
        var legacy = IntPtr.Zero;
        try
        {
            EnsureWindowClass();
            // Never shown: the window exists only to carry a DC with a
            // GL-capable pixel format.
            hwnd = CreateWindowExW(0, ClassName, "nexus-gpu", 0, 0, 0, Math.Max(1, width), Math.Max(1, height),
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            dc = GetDC(hwnd);
            if (dc == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC returned no device context for the GL window");
            }

            var pfd = new PixelFormatDescriptor
            {
                nSize = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
                nVersion = 1,
                dwFlags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
                iPixelType = 0, // PFD_TYPE_RGBA
                cColorBits = 32,
                cDepthBits = 24,
            };
            var format = ChoosePixelFormat(dc, ref pfd);
            if (format == 0)
            {
                throw new InvalidOperationException(
                    $"no GL-capable pixel format on this DC (Win32 {Marshal.GetLastWin32Error()})");
            }
            // ChoosePixelFormat happily answers with Microsoft's software
            // renderer, which cannot give a 3.3 core context; refuse it here
            // rather than fail later resolving a GL 3 entry point.
            var chosen = new PixelFormatDescriptor { nSize = pfd.nSize, nVersion = 1 };
            if (DescribePixelFormat(dc, format, (uint)Marshal.SizeOf<PixelFormatDescriptor>(), ref chosen) != 0
                && (chosen.dwFlags & PfdGenericFormat) != 0
                && (chosen.dwFlags & PfdGenericAccelerated) == 0)
            {
                throw new InvalidOperationException(
                    "only the GDI software renderer offers a pixel format on this device context");
            }
            // A window's pixel format can be set once only, which is why a retry
            // builds a fresh window rather than reusing this one.
            if (!SetPixelFormat(dc, format, ref pfd))
            {
                throw new InvalidOperationException(
                    $"SetPixelFormat({format}) failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            // The legacy context exists to resolve wglCreateContextAttribsARB,
            // which is the only way to ask for a core profile; it is replaced
            // below once the real context is up.
            legacy = wglCreateContext(dc);
            if (legacy == IntPtr.Zero)
            {
                throw new InvalidOperationException($"wglCreateContext failed (Win32 {Marshal.GetLastWin32Error()})");
            }
            if (!wglMakeCurrent(dc, legacy))
            {
                throw new InvalidOperationException($"wglMakeCurrent failed (Win32 {Marshal.GetLastWin32Error()})");
            }

            rc = CreateCoreContext(dc, major, minor);
            if (rc == IntPtr.Zero)
            {
                // No ARB path: the compatibility context the driver handed us is
                // all there is. It is usable only if it already reports the
                // version the shaders need, so that is checked here rather than
                // left to fail later resolving a GL 3 entry point.
                var version = CurrentVersion();
                if (!VersionAtLeast(version, major, minor))
                {
                    throw new InvalidOperationException(
                        $"no wglCreateContextAttribsARB and the context reports '{version}', "
                        + $"below the required {major}.{minor}");
                }
                GpuContext.Log($"[gpu] wglCreateContextAttribsARB unavailable; keeping the "
                    + $"compatibility context ('{version}')");
                var kept = legacy;
                legacy = IntPtr.Zero;
                return new WglHandles(hwnd, dc, kept);
            }

            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(legacy);
            legacy = IntPtr.Zero;
            if (!wglMakeCurrent(dc, rc))
            {
                throw new InvalidOperationException(
                    $"wglMakeCurrent on the {major}.{minor} core context failed (Win32 {Marshal.GetLastWin32Error()})");
            }
            return new WglHandles(hwnd, dc, rc);
        }
        catch
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            if (rc != IntPtr.Zero) wglDeleteContext(rc);
            if (legacy != IntPtr.Zero) wglDeleteContext(legacy);
            if (dc != IntPtr.Zero) ReleaseDC(hwnd, dc);
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            throw;
        }
    }

    // DefWindowProcW by address: a managed WndProc would need a delegate kept
    // alive for the window's life, and this class never handles a message.
    private static void EnsureWindowClass()
    {
        if (_classRegistered)
        {
            return;
        }
        var user32 = LoadLibraryA("user32.dll");
        var wndProc = user32 == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(user32, "DefWindowProcW");
        if (wndProc == IntPtr.Zero)
        {
            throw new InvalidOperationException("could not resolve DefWindowProcW for the GL window class");
        }
        var wc = new WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            // CS_OWNDC: the context is bound to this window's own DC, which must
            // stay the same DC for the context's whole life.
            style = CsOwnDc,
            lpfnWndProc = wndProc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // A retry in the same process finds the class already there.
            if (err != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException($"RegisterClassEx failed (Win32 {err})");
            }
        }
        _classRegistered = true;
    }

    private static unsafe IntPtr CreateCoreContext(IntPtr dc, int major, int minor)
    {
        var entry = wglGetProcAddress("wglCreateContextAttribsARB");
        if (entry == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, IntPtr>)entry;
        Span<int> attribs = stackalloc int[]
        {
            ContextMajorVersionArb, major,
            ContextMinorVersionArb, minor,
            ContextProfileMaskArb, ContextCoreProfileBitArb,
            0,
        };
        IntPtr rc;
        fixed (int* a = attribs)
        {
            rc = create(dc, IntPtr.Zero, a);
        }
        if (rc == IntPtr.Zero)
        {
            // GetLastSystemError, not GetLastWin32Error: nothing sets the
            // managed cache across a raw function-pointer call, so the latter
            // would report whichever DllImport ran last.
            throw new InvalidOperationException(
                $"the driver refused an OpenGL {major}.{minor} core context "
                + $"(Win32 {Marshal.GetLastSystemError()})");
        }
        return rc;
    }

    private static string CurrentVersion()
    {
        var p = glGetString(GlVersion);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p) ?? "";
    }

    /// <summary>GL_VERSION opens with "major.minor"; anything else is unusable
    /// to us and reads as too old.</summary>
    private static bool VersionAtLeast(string version, int major, int minor)
    {
        var head = version.Split(' ')[0].Split('.');
        if (head.Length < 2 || !int.TryParse(head[0], out var gotMajor) || !int.TryParse(head[1], out var gotMinor))
        {
            return false;
        }
        return gotMajor > major || (gotMajor == major && gotMinor >= minor);
    }

    /// <summary>Release the context and the window that carried it. Runs on the
    /// GL thread, the only one Win32 lets destroy that window.</summary>
    public static void Destroy(WglHandles h)
    {
        try { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); } catch { }
        try { if (h.Rc != IntPtr.Zero) wglDeleteContext(h.Rc); } catch { }
        try { if (h.Dc != IntPtr.Zero) ReleaseDC(h.Hwnd, h.Dc); } catch { }
        try { if (h.Hwnd != IntPtr.Zero) DestroyWindow(h.Hwnd); } catch { }
    }

    /// <summary>
    /// Resolve a GL entry point. wglGetProcAddress answers for extensions only
    /// and returns 1, 2, 3 or -1 (not just null) for the core functions it will
    /// not hand out, so those sentinels fall through to opengl32.dll's exports.
    /// </summary>
    public static IntPtr LoadGlSymbol(string name)
    {
        var p = wglGetProcAddress(name);
        if (p != IntPtr.Zero && p != (IntPtr)1 && p != (IntPtr)2 && p != (IntPtr)3 && p != (IntPtr)(-1))
        {
            return p;
        }
        if (_openGl32 == IntPtr.Zero)
        {
            _openGl32 = LoadLibraryA("opengl32.dll");
        }
        return _openGl32 == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(_openGl32, name);
    }
}

/// <summary>Silk.NET's loader over <see cref="WinWglContext.LoadGlSymbol"/>,
/// mirroring the CGL and EGL shims.</summary>
internal sealed class WglNativeContext : Silk.NET.Core.Contexts.INativeContext
{
    public nint GetProcAddress(string procName, int? slot = null)
    {
        nint p = WinWglContext.LoadGlSymbol(procName);
        if (p == 0)
        {
            throw new InvalidOperationException($"WGL GL symbol not found: {procName}");
        }
        return p;
    }

    public bool TryGetProcAddress(string procName, out nint procAddress, int? slot = null)
    {
        procAddress = WinWglContext.LoadGlSymbol(procName);
        return procAddress != 0;
    }

    public void Dispose() { }
}
#endif
