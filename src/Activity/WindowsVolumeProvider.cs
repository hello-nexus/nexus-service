using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Reads and writes the system-wide default-render audio endpoint volume via
/// raw Core Audio COM (IMMDeviceEnumerator -> IMMDevice -> IAudioEndpointVolume).
/// Uses IntPtr + manual vtable indirection so this is fully AOT-safe with no
/// ComWrappers or reflection-based marshalling: every COM call goes through a
/// function pointer extracted from the object's vtable.
///
/// All COM work runs on a dedicated MTA worker thread. ASP.NET request threads
/// have unpredictable apartment state; if they're STA-initialised by some other
/// component, calling MTA-flavoured Core Audio interfaces from there silently
/// marshals through the apartment proxy and the SetMasterVolumeLevelScalar
/// returns success but the audible volume never changes (the proxy serializes
/// the call to a different apartment whose endpoint pointer is stale). The
/// dedicated MTA thread guarantees apartment consistency.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsVolumeProvider : IVolumeProvider, IDisposable
{
    private readonly Thread _comThread;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());

    public WindowsVolumeProvider()
    {
        _comThread = new Thread(RunComLoop)
        {
            IsBackground = true,
            Name = "NexusVolumeCOM",
        };
        _comThread.SetApartmentState(ApartmentState.MTA);
        _comThread.Start();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _comThread.Join(1000); } catch { }
        _queue.Dispose();
    }

    public VolumeState GetState() => GetState("");

    public VolumeState GetState(string deviceId)
    {
        return RunOnComThread(() =>
        {
            var ep = OpenEndpoint(deviceId);
            if (ep == IntPtr.Zero) return new VolumeState { Supported = false, Volume = 0, Muted = false };
            try
            {
                var hrV = GetMasterVolumeLevelScalar(ep, out var v);
                Marshal.ThrowExceptionForHR(hrV);
                var hrM = GetMute(ep, out var m);
                Marshal.ThrowExceptionForHR(hrM);
                return new VolumeState { Supported = true, Volume = Math.Clamp((double)v, 0, 1), Muted = m };
            }
            finally { Release(ep); }
        }, fallback: new VolumeState { Supported = deviceId.Length == 0, Volume = 0, Muted = false }, op: "read");
    }

    public void SetVolume(double volume) => SetVolume("", volume);

    public void SetVolume(string deviceId, double volume)
    {
        var clamped = (float)Math.Clamp(volume, 0, 1);
        RunOnComThread(() =>
        {
            var ep = OpenEndpoint(deviceId);
            if (ep == IntPtr.Zero) return 0;
            try
            {
                var hrBefore = GetMasterVolumeLevelScalar(ep, out var before);
                var hrSet = SetMasterVolumeLevelScalar(ep, clamped, IntPtr.Zero);
                var hrAfter = GetMasterVolumeLevelScalar(ep, out var after);
                LogVolume($"SET req={clamped:F3} before={before:F3}(hr=0x{hrBefore:X8}) setHr=0x{hrSet:X8} after={after:F3}(hr=0x{hrAfter:X8})");
                if (hrSet < 0) Marshal.ThrowExceptionForHR(hrSet);
                return 0;
            }
            finally { Release(ep); }
        }, fallback: 0, op: "set");
    }

    private static readonly object s_logLock = new();
    private static void LogVolume(string msg)
    {
        try
        {
            var dir = Nexus.Service.Platform.ServiceLog.LogsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "nexus-volume.log");
            lock (s_logLock)
            {
                System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
        }
        catch { /* logging is best-effort */ }
    }

    public void SetMuted(bool muted) => SetMuted("", muted);

    public void SetMuted(string deviceId, bool muted)
    {
        RunOnComThread(() =>
        {
            var ep = OpenEndpoint(deviceId);
            if (ep == IntPtr.Zero) return 0;
            try { Marshal.ThrowExceptionForHR(SetMute(ep, muted, IntPtr.Zero)); return 0; }
            finally { Release(ep); }
        }, fallback: 0, op: "mute");
    }

    private void RunComLoop()
    {
        // Initialise COM as MTA on this thread. Without this, the implicit
        // apartment is determined by the first COM call, which can be wrong.
        var hr = CoInitializeEx(IntPtr.Zero, 0x0); // COINIT_MULTITHREADED
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
        {
            Console.Error.WriteLine($"[volume] CoInitializeEx failed: 0x{hr:X8}");
            return;
        }

        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Console.Error.WriteLine($"[volume] worker exception: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException) { /* queue completed */ }
    }

    private T RunOnComThread<T>(Func<T> work, T fallback, string op)
    {
        T result = fallback;
        Exception? err = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException)
        {
            // Queue closed (Dispose called).
            return fallback;
        }
        if (!done.Wait(2000))
        {
            Console.Error.WriteLine($"[volume] {op} timed out after 2 s");
            return fallback;
        }
        if (err != null)
        {
            Console.Error.WriteLine($"[volume] {op} failed: {err.Message}");
            return fallback;
        }
        return result;
    }

    /// <summary>Empty <paramref name="deviceId"/> opens the default render
    /// endpoint; otherwise the named endpoint. Returns IntPtr.Zero (not a
    /// throw) when a named device id no longer exists, so a device unplugged
    /// mid-session degrades to unsupported rather than an exception.</summary>
    private static IntPtr OpenEndpoint(string deviceId = "")
    {
        var clsid = MMDeviceEnumeratorClsid;
        var enumIid = IID_IMMDeviceEnumerator;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref enumIid, out var enumPtr));
        try
        {
            int hrDevice;
            IntPtr devicePtr;
            if (deviceId.Length == 0)
            {
                // IMMDeviceEnumerator::GetDefaultAudioEndpoint - vtable slot 4 (after QI/AddRef/Release/EnumAudioEndpoints)
                hrDevice = GetDefaultAudioEndpoint(enumPtr, EDataFlow.eRender, ERole.eConsole, out devicePtr);
            }
            else
            {
                // IMMDeviceEnumerator::GetDevice - vtable slot 5
                hrDevice = GetDevice(enumPtr, deviceId, out devicePtr);
            }
            if (hrDevice < 0 || devicePtr == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var iid = IID_IAudioEndpointVolume;
                // IMMDevice::Activate - vtable slot 3 (first method after IUnknown)
                Marshal.ThrowExceptionForHR(Activate(devicePtr, ref iid, ClsCtxInprocServer, IntPtr.Zero, out var endpointPtr));
                return endpointPtr;
            }
            finally { Release(devicePtr); }
        }
        finally { Release(enumPtr); }
    }

    private static int GetDefaultAudioEndpoint(IntPtr enumeratorPtr, EDataFlow dataFlow, ERole role, out IntPtr device)
    {
        // vtable[4] = GetDefaultAudioEndpoint(this, dataFlow, role, &device)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, EDataFlow, ERole, out IntPtr, int>)GetVTableSlot(enumeratorPtr, 4);
        return fn(enumeratorPtr, dataFlow, role, out device);
    }

    private static int GetDevice(IntPtr enumeratorPtr, string deviceId, out IntPtr device)
    {
        // vtable[5] = GetDevice(this, pwstrId, &device)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)GetVTableSlot(enumeratorPtr, 5);
        var idPtr = Marshal.StringToHGlobalUni(deviceId);
        try { return fn(enumeratorPtr, idPtr, out device); }
        finally { Marshal.FreeHGlobal(idPtr); }
    }

    private static int Activate(IntPtr devicePtr, ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr endpoint)
    {
        // IMMDevice vtable[3] = Activate(this, &iid, clsCtx, activationParams, &endpoint)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, int, IntPtr, out IntPtr, int>)GetVTableSlot(devicePtr, 3);
        return fn(devicePtr, ref iid, clsCtx, activationParams, out endpoint);
    }

    // IAudioEndpointVolume vtable (after QI/AddRef/Release at slots 0-2):
    //  [3]  RegisterControlChangeNotify
    //  [4]  UnregisterControlChangeNotify
    //  [5]  GetChannelCount
    //  [6]  SetMasterVolumeLevel
    //  [7]  SetMasterVolumeLevelScalar
    //  [8]  GetMasterVolumeLevel
    //  [9]  GetMasterVolumeLevelScalar
    //  [10] SetChannelVolumeLevel
    //  [11] SetChannelVolumeLevelScalar
    //  [12] GetChannelVolumeLevel
    //  [13] GetChannelVolumeLevelScalar
    //  [14] SetMute
    //  [15] GetMute
    private static int GetMasterVolumeLevelScalar(IntPtr ep, out float level)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out float, int>)GetVTableSlot(ep, 9);
        return fn(ep, out level);
    }

    private static int SetMasterVolumeLevelScalar(IntPtr ep, float level, IntPtr eventCtx)
    {
        // pguidEventContext is LPCGUID (a pointer). IntPtr.Zero -> NULL = "no event context".
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, float, IntPtr, int>)GetVTableSlot(ep, 7);
        return fn(ep, level, eventCtx);
    }

    private static int GetMute(IntPtr ep, out bool muted)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVTableSlot(ep, 15);
        var hr = fn(ep, out var raw);
        muted = raw != 0;
        return hr;
    }

    private static int SetMute(IntPtr ep, bool muted, IntPtr eventCtx)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)GetVTableSlot(ep, 14);
        return fn(ep, muted ? 1 : 0, eventCtx);
    }

    private static IntPtr GetVTableSlot(IntPtr instance, int slot)
    {
        // *instance -> vtable pointer; vtable[slot] -> function pointer
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void Release(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        // IUnknown::Release is vtable slot 2.
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetVTableSlot(ptr, 2);
        fn(ptr);
    }

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int ClsCtxInprocServer = 0x1;

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        int clsContext,
        ref Guid iid,
        out IntPtr instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
}
