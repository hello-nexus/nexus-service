#if WINDOWS
using System;
using System.Globalization;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Bench-only: an ES 3.0 pbuffer context on a caller-created D3D11 device. The live path never calls this.</summary>
internal static unsafe partial class AngleContext
{
    private const int EglPlatformDeviceExt = 0x313F;
    private const int EglD3D11DeviceAngle = 0x33A1;

    internal static string BenchClientExtensions()
    {
        EnsureLoaded();
        return Str(_eglQueryString(null, EglExtensions));
    }

    internal static AngleHandles BenchCreateOnDevice(nint d3d11Device, int width, int height)
    {
        EnsureLoaded();
        void* create;
        fixed (byte* name = "eglCreateDeviceANGLE\0"u8)
        {
            create = _eglGetProcAddress(name);
        }
        if (create == null)
        {
            throw new InvalidOperationException("eglCreateDeviceANGLE is not exported");
        }
        var eglDevice = ((delegate* unmanaged[Cdecl]<int, void*, nint*, void*>)create)(EglD3D11DeviceAngle, (void*)d3d11Device, null);
        if (eglDevice == null)
        {
            throw Fail("eglCreateDeviceANGLE returned no device");
        }
        var display = _eglGetPlatformDisplayExt(EglPlatformDeviceExt, eglDevice, null);
        if (display == null)
        {
            throw Fail("eglGetPlatformDisplayEXT(device) returned no display");
        }
        int major, minor;
        if (_eglInitialize(display, &major, &minor) == 0)
        {
            throw Fail("eglInitialize failed");
        }
        _eglBindApi(EglOpenGlEsApi);
        var config = stackalloc int[]
        {
            EglSurfaceType, EglPbufferBit, EglRenderableType, EglOpenGlEs3Bit,
            EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8, EglNone,
        };
        void* cfg = null;
        int count;
        if (_eglChooseConfig(display, config, &cfg, 1, &count) == 0 || count < 1)
        {
            throw Fail("no EGL config offers an ES 3 RGBA8 pbuffer");
        }
        var ctxAttribs = stackalloc int[] { EglContextClientVersion, 3, EglNone };
        var context = _eglCreateContext(display, cfg, null, ctxAttribs);
        if (context == null)
        {
            throw Fail("eglCreateContext failed");
        }
        var surfAttribs = stackalloc int[] { EglWidth, Math.Max(1, width), EglHeight, Math.Max(1, height), EglNone };
        var surface = _eglCreatePbufferSurface(display, cfg, surfAttribs);
        if (surface == null || _eglMakeCurrent(display, surface, surface, context) == 0)
        {
            throw Fail("pbuffer or make-current failed");
        }
        return new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface);
    }
}
#endif
