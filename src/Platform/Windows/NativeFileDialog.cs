#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Native IFileOpenDialog (the modern common item dialog) for the user-session
/// helper. Raw vtable indirection like <see cref="Activity.WindowsVolumeProvider"/> -
/// IntPtr + function pointers, no ComWrappers or reflection marshalling, so it
/// is fully NativeAOT-safe. The dialog runs on a dedicated STA thread
/// (IFileDialog::Show pumps its own modal loop there); one dialog at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class NativeFileDialog
{
    // Keep in lockstep with GalleryLibrary's image + video extension lists.
    private const string MediaFilterName = "Images and videos";
    private const string MediaFilterSpec = "*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.avif;*.mp4;*.m4v;*.webm;*.mov";

    // FILEOPENDIALOGOPTIONS
    private const uint FosForceFilesystem = 0x40;
    private const uint FosAllowMultiselect = 0x200;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;
    private const uint FosPickFolders = 0x20;

    // HRESULT_FROM_WIN32(ERROR_CANCELLED) - the user closed the dialog.
    private const int HrCancelled = unchecked((int)0x800704C7);
    private const uint SigdnFilesysPath = 0x80058000;
    private const int ClsCtxInprocServer = 0x1;
    private const uint CoInitApartmentThreaded = 0x2;

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidIFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");

    private static int s_active;

    public static Task<FileDialogResult> ShowAsync(FileDialogPickMode mode)
    {
        if (Interlocked.CompareExchange(ref s_active, 1, 0) != 0)
        {
            return Task.FromResult(new FileDialogResult { Error = "a dialog is already open" });
        }

        var tcs = new TaskCompletionSource<FileDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Show(mode));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new FileDialogResult { Error = ex.Message });
            }
            finally
            {
                Interlocked.Exchange(ref s_active, 0);
            }
        })
        {
            IsBackground = true,
            Name = "NexusFileDialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static FileDialogResult Show(FileDialogPickMode mode)
    {
        var hrInit = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        var needUninit = hrInit >= 0;
        try
        {
            var clsid = ClsidFileOpenDialog;
            var iid = IidIFileOpenDialog;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var dlg));
            try
            {
                Marshal.ThrowExceptionForHR(GetOptions(dlg, out var opts));
                opts |= FosForceFilesystem | FosPathMustExist | FosFileMustExist;
                opts |= mode switch
                {
                    FileDialogPickMode.Folder => FosPickFolders,
                    FileDialogPickMode.MediaMultiSelect => FosAllowMultiselect,
                    _ => 0u,
                };
                Marshal.ThrowExceptionForHR(SetOptions(dlg, opts));

                IntPtr filterName = IntPtr.Zero, filterSpec = IntPtr.Zero, specArray = IntPtr.Zero;
                try
                {
                    if (mode == FileDialogPickMode.MediaMultiSelect)
                    {
                        // One COMDLG_FILTERSPEC entry: { LPCWSTR name, LPCWSTR spec }.
                        filterName = Marshal.StringToHGlobalUni(MediaFilterName);
                        filterSpec = Marshal.StringToHGlobalUni(MediaFilterSpec);
                        specArray = Marshal.AllocHGlobal(IntPtr.Size * 2);
                        Marshal.WriteIntPtr(specArray, 0, filterName);
                        Marshal.WriteIntPtr(specArray, IntPtr.Size, filterSpec);
                        Marshal.ThrowExceptionForHR(SetFileTypes(dlg, 1, specArray));
                    }

                    // Own the dialog to the Nexus app window when it exists so
                    // it opens centered OVER the app (z-order tied to it), and
                    // nudge it to the foreground either way - the helper is a
                    // background process, so an unowned Show lands behind
                    // whatever the user is looking at.
                    var owner = TrayIcon.FindExistingNexusAppWindow();
                    ForegroundNudge.ForegroundThreadWindowWhenShown(GetCurrentThreadId());
                    var hr = ShowDialog(dlg, owner);
                    if (hr == HrCancelled)
                    {
                        return new FileDialogResult { Cancelled = true };
                    }

                    Marshal.ThrowExceptionForHR(hr);
                    Marshal.ThrowExceptionForHR(GetResults(dlg, out var items));
                    try
                    {
                        Marshal.ThrowExceptionForHR(GetCount(items, out var count));
                        var paths = new List<string>();
                        for (uint i = 0; i < count; i++)
                        {
                            Marshal.ThrowExceptionForHR(GetItemAt(items, i, out var item));
                            try
                            {
                                if (GetDisplayName(item, SigdnFilesysPath, out var pszPath) >= 0 && pszPath != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var path = Marshal.PtrToStringUni(pszPath);
                                        if (!string.IsNullOrEmpty(path))
                                        {
                                            paths.Add(path);
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.FreeCoTaskMem(pszPath);
                                    }
                                }
                            }
                            finally
                            {
                                Release(item);
                            }
                        }

                        return new FileDialogResult { Paths = paths };
                    }
                    finally
                    {
                        Release(items);
                    }
                }
                finally
                {
                    if (specArray != IntPtr.Zero) Marshal.FreeHGlobal(specArray);
                    if (filterName != IntPtr.Zero) Marshal.FreeHGlobal(filterName);
                    if (filterSpec != IntPtr.Zero) Marshal.FreeHGlobal(filterSpec);
                }
            }
            finally
            {
                Release(dlg);
            }
        }
        finally
        {
            if (needUninit) CoUninitialize();
        }
    }

    // IFileOpenDialog vtable: IUnknown 0-2; IModalWindow [3]=Show; IFileDialog
    // [4]=SetFileTypes [5]=SetFileTypeIndex [6]=GetFileTypeIndex [7]=Advise
    // [8]=Unadvise [9]=SetOptions [10]=GetOptions ... [20]=GetResult ...
    // [26]=SetFilter; IFileOpenDialog [27]=GetResults [28]=GetSelectedItems.
    private static int ShowDialog(IntPtr dlg, IntPtr ownerHwnd)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)GetVTableSlot(dlg, 3);
        return fn(dlg, ownerHwnd);
    }

    private static int SetFileTypes(IntPtr dlg, uint count, IntPtr filterSpecs)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)GetVTableSlot(dlg, 4);
        return fn(dlg, count, filterSpecs);
    }

    private static int SetOptions(IntPtr dlg, uint options)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetVTableSlot(dlg, 9);
        return fn(dlg, options);
    }

    private static int GetOptions(IntPtr dlg, out uint options)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)GetVTableSlot(dlg, 10);
        return fn(dlg, out options);
    }

    private static int GetResults(IntPtr dlg, out IntPtr shellItemArray)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVTableSlot(dlg, 27);
        return fn(dlg, out shellItemArray);
    }

    // IShellItemArray vtable: IUnknown 0-2; [3]=BindToHandler [4]=GetPropertyStore
    // [5]=GetPropertyDescriptionList [6]=GetAttributes [7]=GetCount [8]=GetItemAt.
    private static int GetCount(IntPtr items, out uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)GetVTableSlot(items, 7);
        return fn(items, out count);
    }

    private static int GetItemAt(IntPtr items, uint index, out IntPtr item)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)GetVTableSlot(items, 8);
        return fn(items, index, out item);
    }

    // IShellItem vtable: IUnknown 0-2; [3]=BindToHandler [4]=GetParent
    // [5]=GetDisplayName(SIGDN, out LPWSTR).
    private static int GetDisplayName(IntPtr item, uint sigdn, out IntPtr pszName)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)GetVTableSlot(item, 5);
        return fn(item, sigdn, out pszName);
    }

    private static IntPtr GetVTableSlot(IntPtr instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void Release(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetVTableSlot(ptr, 2);
        fn(ptr);
    }

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    [DllImport("ole32")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32")]
    private static extern void CoUninitialize();

    [DllImport("ole32")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid, out IntPtr instance);
}
#endif
