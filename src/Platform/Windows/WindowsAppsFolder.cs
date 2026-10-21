using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// The Start apps list (shell:AppsFolder) and packaged-app install paths, read
/// in-process. IShellItem and IEnumShellItems are called by vtable slot, the same
/// AOT-safe pattern as <see cref="WindowsShellLink"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsAppsFolder
{
    internal readonly record struct StartApp(string Name, string AppId);

    private static readonly Guid FolderIdAppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IidIEnumShellItems = new("70629033-E363-4A28-A567-0DB78006E6D7");
    private static readonly Guid BhidEnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private const uint SigdnNormalDisplay = 0x0;
    private const uint SigdnParentRelativeParsing = 0x80018001;
    private const uint CoInitApartmentThreaded = 0x2;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private const int MaxAbandonedEnumerations = 2;

    private static readonly SingleFlight<List<StartApp>> Flight = new(
        StartEnumeration,
        TimeSpan.FromMinutes(5),
        maxAbandoned: MaxAbandonedEnumerations,
        fallback: () => new List<StartApp>(),
        onStarved: () => Console.Error.WriteLine("[apps-folder] enumeration abandoned too often; skipping new attempts until the stuck ones finish"));

    /// <summary>
    /// Every apps-folder entry as name plus parent-relative parsing name (the
    /// AppUserModelID for packaged apps). The task yields an empty list on failure. One
    /// enumeration is shared by concurrent callers, a hung one is abandoned after a while,
    /// and the number of abandoned threads is capped.
    /// </summary>
    public static FlightRun<List<StartApp>> EnumerateStartApps() => Flight.GetRun();

    private static Task<List<StartApp>> StartEnumeration()
    {
        var done = new TaskCompletionSource<List<StartApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Shell namespace enumeration is apartment-threaded, so it gets its own STA thread.
        var thread = new Thread(() =>
        {
            var apps = new List<StartApp>();
            var init = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            try { Enumerate(apps); }
            catch (Exception ex) { Console.Error.WriteLine($"[apps-folder] {ex.GetType().Name}: {ex.Message}"); }
            finally { if (init >= 0) CoUninitialize(); }
            done.SetResult(apps);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    private static void Enumerate(List<StartApp> apps)
    {
        var folderId = FolderIdAppsFolder;
        var itemIid = IidIShellItem;
        if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref itemIid, out var folder) < 0 || folder == IntPtr.Zero) return;
        try
        {
            var handler = BhidEnumItems;
            var enumIid = IidIEnumShellItems;
            // IShellItem::BindToHandler is slot 3.
            var bind = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(folder, 3);
            IntPtr enumerator;
            if (bind(folder, IntPtr.Zero, &handler, &enumIid, &enumerator) < 0 || enumerator == IntPtr.Zero) return;
            try
            {
                // IEnumShellItems::Next is slot 3.
                var next = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)Slot(enumerator, 3);
                while (true)
                {
                    IntPtr item;
                    uint fetched;
                    if (next(enumerator, 1, &item, &fetched) != 0 || fetched == 0) break;
                    try
                    {
                        var name = DisplayName(item, SigdnNormalDisplay);
                        var id = DisplayName(item, SigdnParentRelativeParsing);
                        if (name is not null && id is not null) apps.Add(new StartApp(name, id));
                    }
                    finally { Release(item); }
                }
            }
            finally { Release(enumerator); }
        }
        finally { Release(folder); }
    }

    private static string? DisplayName(IntPtr item, uint form)
    {
        // IShellItem::GetDisplayName is slot 5.
        var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(item, 5);
        IntPtr text;
        if (get(item, form, &text) < 0 || text == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { CoTaskMemFree(text); }
    }

    /// <summary>Install location of the first package registered to the current user for a family name, or null.</summary>
    public static string? FindPackageInstallPath(string packageFamilyName)
    {
        uint count = 0;
        uint bufferLength = 0;
        var rc = GetPackagesByPackageFamily(packageFamilyName, ref count, null, ref bufferLength, null);
        if (rc != ErrorInsufficientBuffer || count == 0) return null;

        var names = new IntPtr[count];
        var buffer = new char[bufferLength];
        fixed (IntPtr* namesPtr = names)
        fixed (char* bufferPtr = buffer)
        {
            if (GetPackagesByPackageFamily(packageFamilyName, ref count, namesPtr, ref bufferLength, bufferPtr) != ErrorSuccess
                || count == 0)
            {
                return null;
            }
            var fullName = Marshal.PtrToStringUni(namesPtr[0]);
            if (string.IsNullOrEmpty(fullName)) return null;

            uint pathLength = 0;
            if (GetPackagePathByFullName(fullName, ref pathLength, null) != ErrorInsufficientBuffer || pathLength == 0) return null;
            var path = new char[pathLength];
            fixed (char* pathPtr = path)
            {
                if (GetPackagePathByFullName(fullName, ref pathLength, pathPtr) != ErrorSuccess) return null;
                return new string(pathPtr);
            }
        }
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
    private static partial void CoTaskMemFree(IntPtr memory);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid iid, out IntPtr item);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagesByPackageFamily(string familyName, ref uint count, IntPtr* fullNames, ref uint bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagePathByFullName(string fullName, ref uint pathLength, char* path);
}
