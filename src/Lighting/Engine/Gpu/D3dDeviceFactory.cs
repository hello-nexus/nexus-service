#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Creates the D3D11 device ANGLE renders on, when the driver's own worker thread is to be switched off.</summary>
internal static unsafe class D3dDeviceFactory
{
    /// <summary>D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS.</summary>
    public const uint PreventInternalThreading = 0x8;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, uint* levels, uint nLevels, uint sdk, out nint device, out uint level, out nint context);

    private const int InvalidArg = unchecked((int)0x80070057);

    /// <summary>The feature levels ANGLE asks for when it creates its own device, highest first.</summary>
    private static readonly uint[] FeatureLevels = [0xC100, 0xC000, 0xB100, 0xB000, 0xA100, 0xA000];

    /// <summary>Same retry ANGLE makes: a runtime that rejects the list for its newest levels is asked again without its newest levels.</summary>
    private const int FirstLevelWithoutNewest = 3;

    private static nint VTable(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static uint ComRelease(nint obj) => ((delegate* unmanaged<nint, uint>)VTable(obj, 2))(obj);

    public static void Release(nint device)
    {
        if (device != 0)
        {
            ComRelease(device);
        }
    }

    /// <summary>A hardware device on the adapter with the given LUID ("high:low"), created with <paramref name="flags"/>; 0 when anything fails.</summary>
    public static nint Create(string? luid, uint flags)
    {
        nint factory = 0;
        nint adapter = 0;
        try
        {
            if (!AngleAdapterSelector.TryParseLuid(luid, out var high, out var low))
            {
                return 0;
            }
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0)
            {
                return 0;
            }
            var desc = stackalloc byte[320];
            for (uint i = 0; ; i++)
            {
                nint a;
                if (((delegate* unmanaged<nint, uint, nint*, int>)VTable(factory, 12))(factory, i, &a) < 0)
                {
                    break;
                }
                // DXGI_ADAPTER_DESC1 puts the LUID after the description, ids, and three sizes.
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
                return 0;
            }
            nint device;
            nint context;
            int hr;
            fixed (uint* levels = FeatureLevels)
            {
                hr = D3D11CreateDevice(adapter, 0, 0, flags, levels, (uint)FeatureLevels.Length, 7, out device, out _, out context);
                if (hr == InvalidArg)
                {
                    hr = D3D11CreateDevice(adapter, 0, 0, flags, levels + FirstLevelWithoutNewest, (uint)(FeatureLevels.Length - FirstLevelWithoutNewest), 7, out device, out _, out context);
                }
            }
            if (hr < 0)
            {
                return 0;
            }
            ComRelease(context);
            return device;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            if (adapter != 0)
            {
                ComRelease(adapter);
            }
            if (factory != 0)
            {
                ComRelease(factory);
            }
        }
    }
}
#endif
