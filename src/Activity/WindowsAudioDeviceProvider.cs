using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Windows audio device enumeration + default-device switching via raw Core Audio
/// COM (IMMDeviceEnumerator / IMMDeviceCollection / IMMDevice / IPropertyStore)
/// plus the undocumented IPolicyConfig::SetDefaultEndpoint. IntPtr + manual
/// vtable indirection (AOT-safe), all on a dedicated MTA thread - same model as
/// <see cref="WindowsVolumeProvider"/>.
///
/// NOTE: PolicyConfig is undocumented. Bench-probed on Windows 11 (26100):
/// CPolicyConfigClient refuses the classic IPolicyConfig / IPolicyConfigVista
/// IIDs (E_NOINTERFACE) and answers only e8478600, the current 41-method
/// IPolicyConfig (see the spatial section). CPolicyConfigVistaClient exposes
/// IPolicyConfigVista, whose SetDefaultEndpoint is vtable slot 12 - IPolicyConfig
/// carries an extra ResetDeviceFormat ahead of it and lands at 13. Verified
/// live: slot 12 moves the default endpoint and the change reads back.
/// SetDefault* returns false on any failure HR rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsAudioDeviceProvider : IAudioDeviceProvider, IDisposable
{
    private readonly Thread _comThread;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
#if WINDOWS
    // Null in the --set-audio-default one-shot, which IS the user session and
    // calls SetDefaultDirect straight through.
    private readonly Nexus.Service.Helper.HelperRegistry? _helper;
#endif

#if WINDOWS
    public WindowsAudioDeviceProvider(Nexus.Service.Helper.HelperRegistry? helper = null) : this()
    {
        _helper = helper;
    }
#endif

    public WindowsAudioDeviceProvider()
    {
        _comThread = new Thread(RunComLoop) { IsBackground = true, Name = "NexusAudioDeviceCOM" };
        _comThread.SetApartmentState(ApartmentState.MTA);
        _comThread.Start();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _comThread.Join(1000); } catch { }
        _queue.Dispose();
    }

    public AudioDeviceList ListDevices()
    {
        var list = ListEndpoints();
        list.Spatial = ReadSpatialForDefault(list);
        return list;
    }

    private AudioDeviceList ListEndpoints() => RunOnComThread(() =>
    {
        var list = new AudioDeviceList();
        var clsid = MMDeviceEnumeratorClsid;
        var iid = IID_IMMDeviceEnumerator;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var enumPtr) < 0)
            return list;
        try
        {
            list.Outputs.AddRange(Enumerate(enumPtr, EDataFlow.eRender, "output"));
            list.Inputs.AddRange(Enumerate(enumPtr, EDataFlow.eCapture, "input"));
        }
        finally { Release(enumPtr); }
        return list;
    }, fallback: new AudioDeviceList(), op: "list");

    /// <summary>Spatial sound for the default output, read as its own work
    /// item so a stalled or throwing audiosrv call cannot blank the list.</summary>
    private AudioSpatialState ReadSpatialForDefault(AudioDeviceList list)
    {
        var id = list.Outputs.Find(d => d.IsDefault)?.Id ?? "";
        if (id.Length == 0) return new AudioSpatialState();
        return RunOnComThread(() => ReadSpatial(id), fallback: new AudioSpatialState { DeviceId = id }, op: "read-spatial");
    }

    // The default audio endpoint is a per-user setting, so the switch has to run
    // as the console user. The helper is already there and answers with the real
    // result; the schtasks one-shot behind it only ever reported "task started",
    // so a failed switch came back as success.
    public bool SetDefaultOutput(string deviceId) => RouteToUserSession(deviceId);
    public bool SetDefaultInput(string deviceId) => RouteToUserSession(deviceId);

    private bool RouteToUserSession(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
#if WINDOWS
        if (_helper is not null && _helper.IsAnyConnected)
        {
            try
            {
                return Nexus.Service.Helper.Domains.AudioMixerCommands
                    .SetDefaultDeviceAsync(_helper, deviceId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio-win] helper set-default failed: {ex.Message}");
            }
        }
#endif
        return RunOneShotInUserSession(deviceId);
    }

    private static bool RunOneShotInUserSession(string deviceId)
    {
#if WINDOWS
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
        return Nexus.Service.Lifecycle.UserHelperBootstrapper.RunInUserSession(
            $"\"{exe}\" --set-audio-default {deviceId}", "audio-default", "NexusAudioDefault");
#else
        return false;
#endif
    }

    /// <summary>Runs the IPolicyConfig switch in-process. Invoked by the
    /// <c>--set-audio-default</c> CLI one-shot inside the user session.</summary>
    public bool SetDefaultDirect(string deviceId) => SetDefault(deviceId);

    private bool SetDefault(string deviceId) => RunOnComThread(() =>
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        var clsid = PolicyConfigVistaClsid;
        var iid = IID_IPolicyConfigVista;
        // INPROC only: PolicyConfig lives in AudioSes.dll, and asking for
        // CLSCTX_ALL reports a missing local server (REGDB_E_CLASSNOTREG),
        // masking the real E_NOINTERFACE from a wrong IID.
        var cc = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var pc);
        if (cc < 0 || pc == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[audio-win] PolicyConfig CoCreateInstance failed hr=0x{cc:X8}");
            return false;
        }
        var idPtr = Marshal.StringToHGlobalUni(deviceId);
        try
        {
            // Set as default across roles; succeed if any role takes (some
            // endpoints reject eCommunications). IPolicyConfig is undocumented,
            // so log the HR per role to diagnose failures on real hardware.
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)GetVTableSlot(pc, 12);
            var any = false;
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                var hr = fn(pc, idPtr, (int)role);
                if (hr >= 0) any = true;
                else Console.Error.WriteLine($"[audio-win] SetDefaultEndpoint role={role} hr=0x{hr:X8}");
            }
            return any;
        }
        finally { Marshal.FreeHGlobal(idPtr); Release(pc); }
    }, fallback: false, op: "set-default");

    // ── Spatial sound (Windows Sonic / Dolby Atmos / DTS:X) ──
    // No documented API. Windows Settings uses the current IPolicyConfig
    // (IID_IPolicyConfig) on CPolicyConfigClient; slot order and layouts were
    // read from Microsoft's public AudioSes.pdb and are gated to Windows 11,
    // the only line they were checked on.
    //   Get: GetDeviceFormatAndSpatialSettings(LPCWSTR id, BOOL bDefault,
    //          WAVEFORMATEX**, SpatialAudioSettings**, UINT* count,
    //          SpatialAudioEncoderDescriptor**)         - CoTaskMem outputs
    //   Set: SetDeviceSpatialSettings(LPCWSTR id, const SpatialAudioSettings*,
    //          const WAVEFORMATEX*)
    // Settings: audiosrv fills in every field but the three below on write.
    // Descriptor: the licensed flag is 0 for a format Windows would only offer
    // as a store link.
    private const int SpatialSettingsSize = 72;
    private const int SpatialDescriptorSize = 834;
    private const int SpatialSettingsEnabledOffset = 0;
    private const int SpatialSettingsSelectedOffset = 12;
    private const int SpatialSettingsConfiguredOffset = 28;
    private const int SpatialDescriptorNameChars = 256;
    private const int SpatialDescriptorGuidOffset = 768;
    private const int SpatialDescriptorLicensedOffset = 784;
    private const int SpatialGetSlot = 34;
    private const int SpatialSetSlot = 35;
    private const int Windows11FirstBuild = 22000;
    private bool _spatialUnavailable = Environment.OSVersion.Version.Build < Windows11FirstBuild;
    private bool _spatialReadFailureLogged;

    public bool SetSpatial(string deviceId, string formatId) => RunOnComThread(() =>
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        var target = Guid.Empty;
        if (formatId.Length > 0 && !Guid.TryParse(formatId, out target)) return false;
        var pc = CreatePolicyConfig();
        if (pc == IntPtr.Zero) return false;
        try
        {
            if (!QuerySpatial(pc, deviceId, out var before, quiet: false)) return false;
            using (before)
            {
                if (before.Format == IntPtr.Zero)
                {
                    Console.Error.WriteLine("[audio-win] set-spatial: endpoint reported no device format");
                    return false;
                }
                // Only a format Windows itself offers for this endpoint; an
                // unlicensed or foreign GUID is refused here rather than handed
                // to audiosrv.
                if (target != Guid.Empty && !before.Formats().Exists(f => f.Id == target.ToString("D")))
                {
                    Console.Error.WriteLine($"[audio-win] set-spatial: {target} is not offered on this endpoint");
                    return false;
                }
                var settings = stackalloc byte[SpatialSettingsSize];
                new Span<byte>(settings, SpatialSettingsSize).Clear();
                if (target != Guid.Empty)
                {
                    *(int*)(settings + SpatialSettingsEnabledOffset) = 1;
                    target.TryWriteBytes(new Span<byte>(settings + SpatialSettingsSelectedOffset, 16));
                    target.TryWriteBytes(new Span<byte>(settings + SpatialSettingsConfiguredOffset, 16));
                }
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)GetVTableSlot(pc, SpatialSetSlot);
                var idPtr = Marshal.StringToHGlobalUni(deviceId);
                int hr;
                try { hr = fn(pc, idPtr, (IntPtr)settings, before.Format); }
                finally { Marshal.FreeHGlobal(idPtr); }
                if (hr < 0)
                {
                    Console.Error.WriteLine($"[audio-win] SetDeviceSpatialSettings hr=0x{hr:X8}");
                    return false;
                }
            }
            // The switch is only real once audiosrv reports it back.
            if (!QuerySpatial(pc, deviceId, out var after, quiet: false)) return false;
            using (after)
            {
                var active = after.ActiveId();
                if (active == (target == Guid.Empty ? "" : target.ToString("D"))) return true;
                Console.Error.WriteLine($"[audio-win] set-spatial: audiosrv reports '{active}' after the switch");
                return false;
            }
        }
        finally { Release(pc); }
    }, fallback: false, op: "set-spatial");

    /// <summary>Must run on the COM thread.</summary>
    private AudioSpatialState ReadSpatial(string deviceId)
    {
        var state = new AudioSpatialState { DeviceId = deviceId };
        if (deviceId.Length == 0 || _spatialUnavailable) return state;
        var pc = CreatePolicyConfig();
        if (pc == IntPtr.Zero) return state;
        try
        {
            // The mixer polls this; log the first failure only.
            if (!QuerySpatial(pc, deviceId, out var q, quiet: _spatialReadFailureLogged))
            {
                _spatialReadFailureLogged = true;
                return state;
            }
            using (q)
            {
                state.Formats = q.Formats();
                state.ActiveId = q.ActiveId();
                state.Supported = state.Formats.Count > 0;
            }
            return state;
        }
        finally { Release(pc); }
    }

    private IntPtr CreatePolicyConfig()
    {
        var clsid = PolicyConfigClsid;
        var iid = IID_IPolicyConfig;
        if (_spatialUnavailable) return IntPtr.Zero;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var pc);
        if (hr >= 0 && pc != IntPtr.Zero) return pc;
        Console.Error.WriteLine($"[audio-win] IPolicyConfig unavailable hr=0x{hr:X8}; spatial sound off");
        _spatialUnavailable = true;
        return IntPtr.Zero;
    }

    private static bool QuerySpatial(IntPtr pc, string deviceId, out SpatialQuery query, bool quiet)
    {
        query = default;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, out IntPtr, out IntPtr, out uint, out IntPtr, int>)GetVTableSlot(pc, SpatialGetSlot);
        var idPtr = Marshal.StringToHGlobalUni(deviceId);
        int hr;
        try { hr = fn(pc, idPtr, 0, out query.Format, out query.Settings, out query.Count, out query.Descriptors); }
        finally { Marshal.FreeHGlobal(idPtr); }
        if (hr >= 0 && query.Settings != IntPtr.Zero) return true;
        if (!quiet) Console.Error.WriteLine($"[audio-win] GetDeviceFormatAndSpatialSettings hr=0x{hr:X8}");
        query.Dispose();
        return false;
    }

    /// <summary>The three CoTaskMem outputs of slot 34, freed together.</summary>
    private struct SpatialQuery : IDisposable
    {
        public IntPtr Format;
        public IntPtr Settings;
        public uint Count;
        public IntPtr Descriptors;

        public string ActiveId()
        {
            if (Settings == IntPtr.Zero || Marshal.ReadInt32(Settings, SpatialSettingsEnabledOffset) == 0) return "";
            var id = new Guid(new ReadOnlySpan<byte>((byte*)Settings + SpatialSettingsSelectedOffset, 16));
            return id == Guid.Empty ? "" : id.ToString("D");
        }

        public List<AudioSpatialFormat> Formats()
        {
            var list = new List<AudioSpatialFormat>();
            if (Descriptors == IntPtr.Zero) return list;
            for (var i = 0; i < Count; i++)
            {
                var d = (byte*)Descriptors + i * SpatialDescriptorSize;
                if (Marshal.ReadInt32((IntPtr)d, SpatialDescriptorLicensedOffset) == 0) continue;
                var id = new Guid(new ReadOnlySpan<byte>(d + SpatialDescriptorGuidOffset, 16));
                if (id == Guid.Empty) continue;
                var name = Marshal.PtrToStringUni((IntPtr)d, SpatialDescriptorNameChars) ?? "";
                var nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                list.Add(new AudioSpatialFormat { Id = id.ToString("D"), Name = name.Length > 0 ? name : id.ToString("D") });
            }
            return list;
        }

        public void Dispose()
        {
            if (Format != IntPtr.Zero) CoTaskMemFree(Format);
            if (Settings != IntPtr.Zero) CoTaskMemFree(Settings);
            if (Descriptors != IntPtr.Zero) CoTaskMemFree(Descriptors);
            Format = Settings = Descriptors = IntPtr.Zero;
        }
    }

    private static IEnumerable<AudioDevice> Enumerate(IntPtr enumPtr, EDataFlow flow, string direction)
    {
        var result = new List<AudioDevice>();
        string defaultId = "";
        if (GetDefaultAudioEndpoint(enumPtr, flow, ERole.eConsole, out var defDev) >= 0 && defDev != IntPtr.Zero)
        {
            try { defaultId = GetId(defDev); } finally { Release(defDev); }
        }

        if (EnumAudioEndpoints(enumPtr, flow, DEVICE_STATE_ACTIVE, out var coll) < 0 || coll == IntPtr.Zero)
            return result;
        try
        {
            if (GetCount(coll, out var count) < 0) return result;
            for (uint i = 0; i < count; i++)
            {
                if (GetItem(coll, i, out var dev) < 0 || dev == IntPtr.Zero) continue;
                try
                {
                    var id = GetId(dev);
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = GetFriendlyName(dev);
                    result.Add(new AudioDevice
                    {
                        Id = id,
                        Name = string.IsNullOrEmpty(name) ? id : name,
                        IsDefault = id == defaultId,
                        Direction = direction,
                    });
                }
                finally { Release(dev); }
            }
        }
        finally { Release(coll); }
        return result;
    }

    private static string GetId(IntPtr device)
    {
        // IMMDevice::GetId (slot 5) -> LPWSTR* (CoTaskMem)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)GetVTableSlot(device, 5);
        if (fn(device, out var pStr) < 0 || pStr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(pStr) ?? ""; }
        finally { CoTaskMemFree(pStr); }
    }

    private static string GetFriendlyName(IntPtr device)
    {
        // IMMDevice::OpenPropertyStore (slot 4), STGM_READ
        var openFn = (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)GetVTableSlot(device, 4);
        if (openFn(device, STGM_READ, out var store) < 0 || store == IntPtr.Zero) return "";
        try
        {
            var key = PKEY_Device_FriendlyName;
            // IPropertyStore::GetValue (slot 5)
            var getFn = (delegate* unmanaged[Stdcall]<IntPtr, ref PROPERTYKEY, out PROPVARIANT, int>)GetVTableSlot(store, 5);
            if (getFn(store, ref key, out var pv) < 0) return "";
            try { return pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) ?? "" : ""; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Release(store); }
    }

    // ── COM thread plumbing (mirrors WindowsVolumeProvider) ──
    private void RunComLoop()
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0x0);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE) return;
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch (Exception ex) { Console.Error.WriteLine($"[audio-win] worker: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException) { }
    }

    private T RunOnComThread<T>(Func<T> work, T fallback, string op)
    {
        T result = fallback;
        Exception? err = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(() => { try { result = work(); } catch (Exception ex) { err = ex; } finally { done.Set(); } });
        }
        catch (InvalidOperationException) { return fallback; }
        if (!done.Wait(3000)) { Console.Error.WriteLine($"[audio-win] {op} timed out"); return fallback; }
        if (err != null) { Console.Error.WriteLine($"[audio-win] {op} failed: {err.Message}"); return fallback; }
        return result;
    }

    private static int EnumAudioEndpoints(IntPtr e, EDataFlow flow, int state, out IntPtr coll)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, EDataFlow, int, out IntPtr, int>)GetVTableSlot(e, 3);
        return fn(e, flow, state, out coll);
    }

    private static int GetDefaultAudioEndpoint(IntPtr e, EDataFlow flow, ERole role, out IntPtr dev)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, EDataFlow, ERole, out IntPtr, int>)GetVTableSlot(e, 4);
        return fn(e, flow, role, out dev);
    }

    private static int GetCount(IntPtr coll, out uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)GetVTableSlot(coll, 3);
        return fn(coll, out count);
    }

    private static int GetItem(IntPtr coll, uint i, out IntPtr dev)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)GetVTableSlot(coll, 4);
        return fn(coll, i, out dev);
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
    private static readonly Guid PolicyConfigVistaClsid = new("294935ce-f637-4e7c-a41b-ab255460b862");
    private static readonly Guid IID_IPolicyConfigVista = new("568b9108-44bf-40b4-9006-86afe5b5a620");
    private static readonly Guid PolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid IID_IPolicyConfig = new("e8478600-a74b-4b3a-a96b-1fc3e796fc46");
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };
    private const int ClsCtxInprocServer = 0x1;
    private const int STGM_READ = 0x0;
    private const int DEVICE_STATE_ACTIVE = 0x1;

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // PROPVARIANT (x64): 2-byte vt + 6 bytes reserved, then a 16-byte union.
    // For VT_LPWSTR the string pointer sits in the first union slot (p).
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1; public ushort r2; public ushort r3; public IntPtr p; public IntPtr p2; }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PROPVARIANT pv);
}
