#if WINDOWS
using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

internal static unsafe partial class AngleContext
{
    /// <summary>Bench harness: a pbuffer ES 3.0 context current on the calling thread, over a device the caller created.</summary>
    internal static AngleHandles BenchCreateOnDevice(nint d3d11Device, int width, int height)
    {
        EnsureLoaded();
        var display = DisplayOnDevice(d3d11Device, out var eglDevice, out _, out _);
        if (display == null)
        {
            throw new InvalidOperationException("ANGLE refused the device");
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
        return new AngleHandles((IntPtr)display, (IntPtr)context, (IntPtr)surface, 0, eglDevice);
    }
}
#endif
