using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Enumerates and writes per-application audio sessions on the default render
/// endpoint via raw Core Audio COM (IMMDeviceEnumerator -> IMMDevice ->
/// IAudioSessionManager2 -> IAudioSessionEnumerator). IntPtr + manual vtable
/// indirection so it is AOT-safe with no ComWrappers, same model as
/// <see cref="WindowsVolumeProvider"/>, including the dedicated MTA thread:
/// Core Audio calls made from an STA-initialised thread marshal through an
/// apartment proxy that reports success while writing nothing.
///
/// Runs in the user-session helper, never in the service. Audio sessions belong
/// to the interactive logon session; Session 0 enumerates its own (empty) set.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsAudioSessionEnumerator : IDisposable
{
    private readonly Thread _comThread;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    // pid -> (strip key, label). Resolving a label costs a file-version read, so
    // it is done once per pid and pruned when the pid stops showing up.
    private readonly Dictionary<int, ProcessIdentity> _identities = new();

    public WindowsAudioSessionEnumerator()
    {
        _comThread = new Thread(RunComLoop)
        {
            IsBackground = true,
            Name = "NexusAudioSessionCOM",
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

    /// <summary>One strip per process name, collapsing that process's sessions.</summary>
    public List<AudioSessionDto> Snapshot() =>
        RunOnComThread(SnapshotCore, new List<AudioSessionDto>(), "snapshot");

    /// <summary>Writes to every session the strip covers. Null leaves that field alone.</summary>
    public void Apply(string id, double? volume, bool? muted) =>
        RunOnComThread(() => { ApplyCore(id, volume, muted); return true; }, false, "apply");

    private List<AudioSessionDto> SnapshotCore()
    {
        var strips = new Dictionary<string, AudioSessionDto>(StringComparer.OrdinalIgnoreCase);
        var seenPids = new HashSet<int>();

        ForEachSession((ctl, pid, isSystem, endpointId, isDefaultEndpoint) =>
        {
            seenPids.Add(pid);
            var identity = ResolveIdentity(pid, isSystem);
            if (identity.Key.Length == 0) return;

            var volumePtr = QueryInterface(ctl, IID_ISimpleAudioVolume);
            var meterPtr = QueryInterface(ctl, IID_IAudioMeterInformation);
            try
            {
                if (volumePtr == IntPtr.Zero) return;
                if (GetSessionVolume(volumePtr, out var level) != 0) return;
                GetSessionMute(volumePtr, out var isMuted);
                var peak = 0f;
                if (meterPtr != IntPtr.Zero) GetPeakValue(meterPtr, out peak);
                var active = GetState(ctl, out var state) == 0 && state == SessionStateActive;

                if (!strips.TryGetValue(identity.Key, out var strip))
                {
                    strip = new AudioSessionDto
                    {
                        Id = identity.Key,
                        Name = identity.Name,
                        Volume = Math.Clamp(level, 0f, 1f),
                        Muted = isMuted,
                        Peak = Math.Clamp(peak, 0f, 1f),
                        Active = active,
                        OnDefault = false,
                    };
                    strips[identity.Key] = strip;
                }
                else
                {
                    // Sessions of one process can disagree (one stream ducked);
                    // the strip shows the loudest.
                    strip.Volume = Math.Max(strip.Volume, Math.Clamp(level, 0f, 1f));
                    strip.Peak = Math.Max(strip.Peak, Math.Clamp(peak, 0f, 1f));
                    strip.Muted = strip.Muted && isMuted;
                    strip.Active = strip.Active || active;
                }
                if (!strip.DeviceIds.Contains(endpointId)) strip.DeviceIds.Add(endpointId);
                if (isDefaultEndpoint) strip.OnDefault = true;
            }
            finally
            {
                Release(volumePtr);
                Release(meterPtr);
            }
        });

        PruneIdentities(seenPids);

        var list = new List<AudioSessionDto>(strips.Values);
        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private void ApplyCore(string id, double? volume, bool? muted)
    {
        if (volume is null && muted is null) return;
        ForEachSession((ctl, pid, isSystem, endpointId, isDefaultEndpoint) =>
        {
            var identity = ResolveIdentity(pid, isSystem);
            if (!string.Equals(identity.Key, id, StringComparison.OrdinalIgnoreCase)) return;

            var volumePtr = QueryInterface(ctl, IID_ISimpleAudioVolume);
            if (volumePtr == IntPtr.Zero) return;
            try
            {
                if (volume is double v) SetSessionVolume(volumePtr, (float)Math.Clamp(v, 0, 1), IntPtr.Zero);
                if (muted is bool m) SetSessionMute(volumePtr, m, IntPtr.Zero);
            }
            finally { Release(volumePtr); }
        });
    }

    /// <summary>
    /// Walks every ACTIVE render endpoint (not just the default one), and on
    /// each one its session enumerator, releasing every interface on the way
    /// out. The callback gets the session control (still owned by this
    /// method), the owning pid, whether it is the system-sounds session, the
    /// endpoint id the session lives on, and whether that endpoint is the
    /// default eConsole render device.
    /// </summary>
    private static void ForEachSession(Action<IntPtr, int, bool, string, bool> visit)
    {
        var clsid = MMDeviceEnumeratorClsid;
        var enumIid = IID_IMMDeviceEnumerator;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref enumIid, out var enumPtr));
        try
        {
            var defaultId = "";
            if (GetDefaultAudioEndpoint(enumPtr, EDataFlow.eRender, ERole.eConsole, out var defaultDevicePtr) >= 0
                && defaultDevicePtr != IntPtr.Zero)
            {
                try { defaultId = GetDeviceId(defaultDevicePtr); }
                finally { Release(defaultDevicePtr); }
            }

            if (EnumAudioEndpoints(enumPtr, EDataFlow.eRender, DeviceStateActive, out var collPtr) < 0 || collPtr == IntPtr.Zero)
                return;
            try
            {
                if (GetDeviceCollectionCount(collPtr, out var deviceCount) < 0) return;
                for (uint d = 0; d < deviceCount; d++)
                {
                    if (GetDeviceCollectionItem(collPtr, d, out var devicePtr) != 0 || devicePtr == IntPtr.Zero) continue;
                    try
                    {
                        var endpointId = GetDeviceId(devicePtr);
                        if (endpointId.Length == 0) continue;
                        WalkEndpointSessions(devicePtr, endpointId, string.Equals(endpointId, defaultId, StringComparison.Ordinal), visit);
                    }
                    finally { Release(devicePtr); }
                }
            }
            finally { Release(collPtr); }
        }
        finally { Release(enumPtr); }
    }

    private static void WalkEndpointSessions(IntPtr devicePtr, string endpointId, bool isDefaultEndpoint, Action<IntPtr, int, bool, string, bool> visit)
    {
        var mgrIid = IID_IAudioSessionManager2;
        if (Activate(devicePtr, ref mgrIid, ClsCtxInprocServer, IntPtr.Zero, out var mgrPtr) < 0 || mgrPtr == IntPtr.Zero) return;
        try
        {
            if (GetSessionEnumerator(mgrPtr, out var sessEnumPtr) < 0 || sessEnumPtr == IntPtr.Zero) return;
            try
            {
                if (GetCount(sessEnumPtr, out var count) < 0) return;
                for (var i = 0; i < count; i++)
                {
                    if (GetSession(sessEnumPtr, i, out var ctl) != 0 || ctl == IntPtr.Zero) continue;
                    var ctl2 = QueryInterface(ctl, IID_IAudioSessionControl2);
                    try
                    {
                        if (ctl2 == IntPtr.Zero) continue;
                        // Expired = the app is gone and the session is a
                        // tombstone; it has no level worth showing.
                        if (GetState(ctl, out var state) == 0 && state == SessionStateExpired) continue;
                        // Packaged apps span several processes and answer
                        // AUDCLNT_S_NO_SINGLE_PROCESS (a success code) with the initial pid.
                        var pidHr = GetProcessId(ctl2, out var pid);
                        if (pidHr != 0 && pidHr != AudclntSNoSingleProcess) continue;
                        // S_OK true, S_FALSE false - a plain HR test would read both as success.
                        var isSystem = IsSystemSoundsSession(ctl2) == 0;
                        // Only the system-sounds session may map to pid 0 (ResolveIdentity folds pid 0 into it).
                        if (pid == 0 && !isSystem) continue;
                        visit(ctl, pid, isSystem, endpointId, isDefaultEndpoint);
                    }
                    finally
                    {
                        Release(ctl2);
                        Release(ctl);
                    }
                }
            }
            finally { Release(sessEnumPtr); }
        }
        finally { Release(mgrPtr); }
    }

    private ProcessIdentity ResolveIdentity(int pid, bool isSystem)
    {
        if (isSystem || pid == 0)
        {
            return new ProcessIdentity(AudioMixerIds.SystemSounds, "System Sounds");
        }
        if (_identities.TryGetValue(pid, out var cached)) return cached;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var key = proc.ProcessName.ToLowerInvariant();
            if (key.Length > 0)
            {
                var identity = new ProcessIdentity(key, ResolveLabel(proc, key));
                _identities[pid] = identity;
                return identity;
            }
        }
        catch
        {
            // Exited between enumeration and lookup, or a process we cannot
            // open. Not cached: a transient failure would otherwise hide the
            // strip for as long as the app keeps that session open.
        }
        return new ProcessIdentity("", "");
    }

    private static string ResolveLabel(Process proc, string key)
    {
        try
        {
            var path = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
        }
        catch
        {
            // MainModule throws for processes at a higher integrity level.
        }
        // InvariantGlobalization is on, so no culture-aware title casing.
        return char.ToUpperInvariant(key[0]) + key[1..];
    }

    private void PruneIdentities(HashSet<int> seenPids)
    {
        if (_identities.Count == 0) return;
        List<int>? stale = null;
        foreach (var pid in _identities.Keys)
        {
            if (seenPids.Contains(pid)) continue;
            (stale ??= new List<int>()).Add(pid);
        }
        if (stale is null) return;
        foreach (var pid in stale) _identities.Remove(pid);
    }

    private void RunComLoop()
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0x0); // COINIT_MULTITHREADED
        if (hr < 0)
        {
            Console.Error.WriteLine($"[audio-sessions] CoInitializeEx failed: 0x{hr:X8}");
        }
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Console.Error.WriteLine($"[audio-sessions] work item failed: {ex.Message}"); }
        }
    }

    private T RunOnComThread<T>(Func<T> work, T fallback, string op)
    {
        var result = fallback;
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
            return fallback;
        }
        if (!done.Wait(2000))
        {
            Console.Error.WriteLine($"[audio-sessions] {op} timed out after 2 s");
            return fallback;
        }
        if (err != null)
        {
            Console.Error.WriteLine($"[audio-sessions] {op} failed: {err.Message}");
            return fallback;
        }
        return result;
    }

    private readonly record struct ProcessIdentity(string Key, string Name);

    // IMMDeviceEnumerator vtable[4] = GetDefaultAudioEndpoint(this, dataFlow, role, &device)
    private static int GetDefaultAudioEndpoint(IntPtr enumeratorPtr, EDataFlow dataFlow, ERole role, out IntPtr device)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, EDataFlow, ERole, out IntPtr, int>)GetVTableSlot(enumeratorPtr, 4);
        return fn(enumeratorPtr, dataFlow, role, out device);
    }

    // IMMDeviceEnumerator vtable[3] = EnumAudioEndpoints(this, dataFlow, stateMask, &devices)
    private static int EnumAudioEndpoints(IntPtr enumeratorPtr, EDataFlow dataFlow, int stateMask, out IntPtr devices)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, EDataFlow, int, out IntPtr, int>)GetVTableSlot(enumeratorPtr, 3);
        return fn(enumeratorPtr, dataFlow, stateMask, out devices);
    }

    // IMMDeviceCollection: GetCount [3], Item [4]
    private static int GetDeviceCollectionCount(IntPtr coll, out uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)GetVTableSlot(coll, 3);
        return fn(coll, out count);
    }

    private static int GetDeviceCollectionItem(IntPtr coll, uint index, out IntPtr device)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)GetVTableSlot(coll, 4);
        return fn(coll, index, out device);
    }

    // IMMDevice::GetId (slot 5) -> LPWSTR* (CoTaskMem)
    private static string GetDeviceId(IntPtr device)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVTableSlot(device, 5);
        if (fn(device, out var pStr) < 0 || pStr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(pStr) ?? ""; }
        finally { CoTaskMemFree(pStr); }
    }

    // IMMDevice vtable[3] = Activate(this, &iid, clsCtx, activationParams, &out)
    private static int Activate(IntPtr devicePtr, ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, int, IntPtr, out IntPtr, int>)GetVTableSlot(devicePtr, 3);
        return fn(devicePtr, ref iid, clsCtx, activationParams, out instance);
    }

    // IAudioSessionManager2 inherits IAudioSessionManager (GetAudioSessionControl [3],
    // GetSimpleAudioVolume [4]), so its own methods start at [5].
    private static int GetSessionEnumerator(IntPtr mgrPtr, out IntPtr sessionEnum)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVTableSlot(mgrPtr, 5);
        return fn(mgrPtr, out sessionEnum);
    }

    // IAudioSessionEnumerator: GetCount [3], GetSession [4]
    private static int GetCount(IntPtr sessEnumPtr, out int count)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVTableSlot(sessEnumPtr, 3);
        return fn(sessEnumPtr, out count);
    }

    private static int GetSession(IntPtr sessEnumPtr, int index, out IntPtr session)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)GetVTableSlot(sessEnumPtr, 4);
        return fn(sessEnumPtr, index, out session);
    }

    // IAudioSessionControl: GetState [3], GetDisplayName [4], SetDisplayName [5],
    // GetIconPath [6], SetIconPath [7], GetGroupingParam [8], SetGroupingParam [9],
    // RegisterAudioSessionNotification [10], UnregisterAudioSessionNotification [11]
    private static int GetState(IntPtr ctl, out int state)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVTableSlot(ctl, 3);
        return fn(ctl, out state);
    }

    // IAudioSessionControl2 continues at [12]: GetSessionIdentifier,
    // GetSessionInstanceIdentifier [13], GetProcessId [14], IsSystemSoundsSession [15]
    private static int GetProcessId(IntPtr ctl2, out int pid)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVTableSlot(ctl2, 14);
        return fn(ctl2, out pid);
    }

    private static int IsSystemSoundsSession(IntPtr ctl2)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetVTableSlot(ctl2, 15);
        return fn(ctl2);
    }

    // ISimpleAudioVolume: SetMasterVolume [3], GetMasterVolume [4], SetMute [5], GetMute [6]
    private static int SetSessionVolume(IntPtr vol, float level, IntPtr eventCtx)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, float, IntPtr, int>)GetVTableSlot(vol, 3);
        return fn(vol, level, eventCtx);
    }

    private static int GetSessionVolume(IntPtr vol, out float level)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out float, int>)GetVTableSlot(vol, 4);
        return fn(vol, out level);
    }

    private static int SetSessionMute(IntPtr vol, bool muted, IntPtr eventCtx)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)GetVTableSlot(vol, 5);
        return fn(vol, muted ? 1 : 0, eventCtx);
    }

    private static int GetSessionMute(IntPtr vol, out bool muted)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)GetVTableSlot(vol, 6);
        var hr = fn(vol, out var raw);
        muted = raw != 0;
        return hr;
    }

    // IAudioMeterInformation: GetPeakValue [3]
    private static int GetPeakValue(IntPtr meter, out float peak)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out float, int>)GetVTableSlot(meter, 3);
        return fn(meter, out peak);
    }

    /// <summary>IUnknown::QueryInterface [0]. Returns IntPtr.Zero when unsupported.</summary>
    private static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)GetVTableSlot(instance, 0);
        return fn(instance, ref iid, out var result) == 0 ? result : IntPtr.Zero;
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

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private const int ClsCtxInprocServer = 0x1;
    private const int SessionStateActive = 1;
    private const int SessionStateExpired = 2;
    private const int AudclntSNoSingleProcess = 0x0889000D;
    private const int DeviceStateActive = 0x1;

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

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr ptr);
}
