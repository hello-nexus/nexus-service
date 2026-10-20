using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

/// <summary>Writes a .lnk through IShellLinkW and IPersistFile, called by vtable slot so it stays AOT-safe.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsShellLink
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidIPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private const uint CoInitApartmentThreaded = 0x2;
    private const int ClsCtxInprocServer = 0x1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // IShellLinkW slots: IUnknown 0-2, SetDescription 7, SetWorkingDirectory 9, SetIconLocation 17, SetPath 20.
    // IPersistFile slot: Save 6.
    public static bool Create(string lnkPath, string targetPath, string workingDirectory, string iconPath, int iconIndex, string description)
    {
        var init = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        var initialized = init >= 0 && init != RpcEChangedMode;
        try
        {
            var clsid = ClsidShellLink;
            var linkIid = IidIShellLinkW;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref linkIid, out var link) < 0 || link == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (SetString(link, 20, targetPath) < 0) return false;
                if (SetString(link, 9, workingDirectory) < 0) return false;
                if (SetString(link, 7, description) < 0) return false;
                var iconPtr = Marshal.StringToHGlobalUni(iconPath);
                try
                {
                    var setIcon = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Slot(link, 17);
                    if (setIcon(link, iconPtr, iconIndex) < 0) return false;
                }
                finally { Marshal.FreeHGlobal(iconPtr); }

                var persistIid = IidIPersistFile;
                var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(link, 0);
                IntPtr persist;
                if (queryInterface(link, &persistIid, &persist) < 0 || persist == IntPtr.Zero) return false;
                try
                {
                    var pathPtr = Marshal.StringToHGlobalUni(lnkPath);
                    try
                    {
                        var save = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Slot(persist, 6);
                        return save(persist, pathPtr, 1) >= 0;
                    }
                    finally { Marshal.FreeHGlobal(pathPtr); }
                }
                finally { Release(persist); }
            }
            finally { Release(link); }
        }
        finally
        {
            if (initialized) CoUninitialize();
        }
    }

    private static int SetString(IntPtr link, int slot, string value)
    {
        var ptr = Marshal.StringToHGlobalUni(value);
        try
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(link, slot);
            return set(link, ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static IntPtr Slot(IntPtr instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void Release(IntPtr instance)
    {
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2);
        release(instance);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);
}
