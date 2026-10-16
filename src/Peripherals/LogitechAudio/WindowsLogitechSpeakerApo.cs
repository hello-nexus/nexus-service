#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>
/// Logitech's speaker effect on Windows. The outputs that carry it are found in the
/// MMDevices registry (their effect CLSID), its settings live under
/// <see cref="LogitechSpeakerProtocol.SettingsKey"/>, and writing one of its watched
/// endpoint properties makes it reload them, exactly what G HUB does. COM runs on one
/// MTA thread, as in WindowsVolumeProvider.
/// </summary>
public sealed unsafe class WindowsLogitechSpeakerApo : ILogitechSpeakerApo, IDisposable
{
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string FxPreMixEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
    private const string FxStreamEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
    private const string FxModelName = "{d57f93ae-4210-46b3-acfc-90437b666152},1000";
    private const string PropDeviceDescription = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PropInterfaceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string EndpointIdPrefix = "{0.0.0.00000000}.";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _comThread;
    // Test sounds may overlap (a quick second click plays at once); a few at most.
    private const int MaxTestSounds = 3;
    private int _testSounds;

    public WindowsLogitechSpeakerApo()
    {
        _comThread = new Thread(RunComLoop) { IsBackground = true, Name = "logitech-speaker-com" };
        _comThread.Start();
    }

    public void Dispose() => _queue.CompleteAdding();

    public IReadOnlyList<LogitechSpeakerEndpoint> FindEndpoints()
    {
        var found = new List<(string Id, string Name, string Model)>();
        try
        {
            using var render = Registry.LocalMachine.OpenSubKey(RenderKey);
            if (render is null)
            {
                return Array.Empty<LogitechSpeakerEndpoint>();
            }
            foreach (var sub in render.GetSubKeyNames())
            {
                using var endpoint = render.OpenSubKey(sub);
                if (endpoint?.GetValue("DeviceState") is not int state || (state & 0xF) != 1)
                {
                    continue;
                }
                using var fx = endpoint.OpenSubKey("FxProperties");
                if (fx is null || !(IsSpeakerApo(fx.GetValue(FxStreamEffect)) || IsSpeakerApo(fx.GetValue(FxPreMixEffect))))
                {
                    continue;
                }
                using var props = endpoint.OpenSubKey("Properties");
                var model = fx.GetValue(FxModelName) as string;
                var name = props?.GetValue(PropInterfaceName) as string ?? props?.GetValue(PropDeviceDescription) as string;
                found.Add((EndpointIdPrefix + sub.ToLowerInvariant(), name ?? "Logitech speaker", string.IsNullOrEmpty(model) ? name ?? "Logitech speaker" : model));
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[logitech-speaker] endpoint scan failed: {ex.Message}");
            return Array.Empty<LogitechSpeakerEndpoint>();
        }

        if (found.Count == 0)
        {
            return Array.Empty<LogitechSpeakerEndpoint>();
        }
        var defaultId = RunOnComThread(DefaultRenderId, "", "default output");
        var result = new List<LogitechSpeakerEndpoint>(found.Count);
        foreach (var (id, name, model) in found)
        {
            result.Add(new LogitechSpeakerEndpoint(id, name, model, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static bool IsSpeakerApo(object? value) =>
        value is string s && Guid.TryParse(s, out var clsid) && clsid == LogitechSpeakerProtocol.SpeakerApoClsid;

    public LogitechSpeakerApoValues Read(string endpointId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsPath(endpointId));
            if (key is null)
            {
                return new LogitechSpeakerApoValues();
            }
            return new LogitechSpeakerApoValues
            {
                SelectedEq = key.GetValue(LogitechSpeakerProtocol.SelectedEq) as int?,
                AdvancedBands = LogitechSpeakerProtocol.DecodeFloats(key.GetValue(LogitechSpeakerProtocol.AdvancedEqParams) as byte[], LogitechSpeakerProtocol.BandCount),
                SelectedSurround = key.GetValue(LogitechSpeakerProtocol.SelectedSurround) as int?,
                DtsOn = key.GetValue(LogitechSpeakerProtocol.DtsOn) is int on ? on != 0 : null,
                DtsRoom = key.GetValue(LogitechSpeakerProtocol.DtsRoomModel) as int?,
                ChannelGains = LogitechSpeakerProtocol.DecodeFloats(key.GetValue(LogitechSpeakerProtocol.VolumeSpeakers) as byte[], LogitechSpeakerProtocol.ChannelCount),
            };
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[logitech-speaker] reading settings failed: {ex.Message}");
            return new LogitechSpeakerApoValues();
        }
    }

    public bool Write(string endpointId, LogitechSpeakerApoChange change)
    {
        var signals = new List<(Guid Set, uint Pid)>();
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SettingsPath(endpointId), writable: true);
            if (change.AdvancedBands is { } bands)
            {
                key.SetValue(LogitechSpeakerProtocol.AdvancedEqParams, LogitechSpeakerProtocol.EncodeBands(bands), RegistryValueKind.Binary);
                for (var band = 0; band < LogitechSpeakerProtocol.BandCount; band++)
                {
                    signals.Add((LogitechSpeakerProtocol.EqKeys, LogitechSpeakerProtocol.AdvancedBandPid(band)));
                }
            }
            if (change.SelectedEq is { } eq)
            {
                key.SetValue(LogitechSpeakerProtocol.SelectedEq, eq, RegistryValueKind.DWord);
                signals.Add((LogitechSpeakerProtocol.EqKeys, LogitechSpeakerProtocol.EqTypePid));
            }
            if (change.SelectedSurround is { } surround)
            {
                key.SetValue(LogitechSpeakerProtocol.SelectedSurround, surround, RegistryValueKind.DWord);
                signals.Add((LogitechSpeakerProtocol.DeviceKeys, LogitechSpeakerProtocol.SurroundTypePid));
            }
            if (change.DtsOn is { } dts)
            {
                key.SetValue(LogitechSpeakerProtocol.DtsOn, dts ? 1 : 0, RegistryValueKind.DWord);
                signals.Add((LogitechSpeakerProtocol.DeviceKeys, LogitechSpeakerProtocol.DtsStatePid));
            }
            if (change.DtsRoom is { } room)
            {
                key.SetValue(LogitechSpeakerProtocol.DtsRoomModel, room, RegistryValueKind.DWord);
                signals.Add((LogitechSpeakerProtocol.DeviceKeys, LogitechSpeakerProtocol.DtsRoomPid));
            }
            if (change.ChannelGains is { } gains)
            {
                key.SetValue(LogitechSpeakerProtocol.VolumeSpeakers, LogitechSpeakerProtocol.EncodeChannelGains(gains), RegistryValueKind.Binary);
                signals.Add((LogitechSpeakerProtocol.VolumeKeys, LogitechSpeakerProtocol.SpeakerVolumePid));
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[logitech-speaker] writing settings failed: {ex.Message}");
            return false;
        }

        return signals.Count == 0 || RunOnComThread(() => Signal(endpointId, signals), false, "reload signal");
    }

    private static string SettingsPath(string endpointId) =>
        LogitechSpeakerProtocol.SettingsKey + "\\" + endpointId.ToLowerInvariant();

    public float? GetVolume(string endpointId) =>
        RunOnComThread(() => WithEndpointVolume(endpointId, volume =>
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Slot(volume, 9);
            float level;
            return get(volume, &level) >= 0 ? level : (float?)null;
        }), null, "get volume");

    public bool SetVolume(string endpointId, float level) =>
        RunOnComThread(() => WithEndpointVolume(endpointId, volume =>
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, float, Guid*, int>)Slot(volume, 7);
            return set(volume, Math.Clamp(level, 0f, 1f), null) >= 0;
        }), false, "set volume");

    public bool PlayTestSound(string endpointId, int channel)
    {
        if (channel < 0 || channel >= LogitechSpeakerProtocol.ChannelCount)
        {
            return false;
        }
        if (Interlocked.Increment(ref _testSounds) > MaxTestSounds)
        {
            Interlocked.Decrement(ref _testSounds);
            return false;
        }
        _ = Task.Run(() =>
        {
            try
            {
                CoInitializeEx(IntPtr.Zero, 0);
                PlayTestSoundOnThisThread(endpointId, channel);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[logitech-speaker] test sound failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _testSounds);
            }
        });
        return true;
    }

    // ── COM work, on the COM thread ──

    private static string DefaultRenderId()
    {
        var enumerator = CreateEnumerator();
        if (enumerator == IntPtr.Zero)
        {
            return "";
        }
        try
        {
            var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4);
            IntPtr device;
            if (getDefault(enumerator, 0 /* eRender */, 0 /* eConsole */, &device) < 0 || device == IntPtr.Zero)
            {
                return "";
            }
            try
            {
                var getId = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(device, 5);
                IntPtr text;
                if (getId(device, &text) < 0 || text == IntPtr.Zero)
                {
                    return "";
                }
                try { return Marshal.PtrToStringUni(text) ?? ""; }
                finally { CoTaskMemFree(text); }
            }
            finally { Release(device); }
        }
        finally { Release(enumerator); }
    }

    // Each write of a watched property raises IMMNotificationClient::OnPropertyValueChanged
    // in the effect, which maps the key to "reload this setting". G HUB leaves them VT_UI4 0.
    private static bool Signal(string endpointId, List<(Guid Set, uint Pid)> keys)
    {
        var device = GetDevice(endpointId);
        if (device == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            var open = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(device, 4);
            IntPtr store;
            if (open(device, 2 /* STGM_READWRITE */, &store) < 0 || store == IntPtr.Zero)
            {
                ServiceLog.Warn("[logitech-speaker] the output's property store did not open for writing");
                return false;
            }
            try
            {
                var setValue = (delegate* unmanaged[Stdcall]<IntPtr, PROPERTYKEY*, PROPVARIANT*, int>)Slot(store, 6);
                var commit = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(store, 7);
                var ok = true;
                foreach (var (set, pid) in keys)
                {
                    var key = new PROPERTYKEY { fmtid = set, pid = pid };
                    var value = new PROPVARIANT { vt = 19 /* VT_UI4 */ };
                    if (setValue(store, &key, &value) < 0)
                    {
                        ok = false;
                    }
                }
                return commit(store) >= 0 && ok;
            }
            finally { Release(store); }
        }
        finally { Release(device); }
    }

    private static T? WithEndpointVolume<T>(string endpointId, Func<IntPtr, T?> work)
    {
        var device = GetDevice(endpointId);
        if (device == IntPtr.Zero)
        {
            return default;
        }
        try
        {
            var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Slot(device, 3);
            var iid = IID_IAudioEndpointVolume;
            IntPtr volume;
            if (activate(device, &iid, ClsCtxAll, IntPtr.Zero, &volume) < 0 || volume == IntPtr.Zero)
            {
                return default;
            }
            try { return work(volume); }
            finally { Release(volume); }
        }
        finally { Release(device); }
    }

    private static IntPtr GetDevice(string endpointId)
    {
        var enumerator = CreateEnumerator();
        if (enumerator == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        try
        {
            var getDevice = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(enumerator, 5);
            IntPtr device;
            fixed (char* id = endpointId)
            {
                return getDevice(enumerator, id, &device) >= 0 ? device : IntPtr.Zero;
            }
        }
        finally { Release(enumerator); }
    }

    private static IntPtr CreateEnumerator()
    {
        var clsid = MMDeviceEnumeratorClsid;
        var iid = IID_IMMDeviceEnumerator;
        return CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var enumerator) >= 0 ? enumerator : IntPtr.Zero;
    }

    // ── test sound: a short sound (a low strike for the subwoofer) on one channel, through the
    //    output's shared-mode mix format ──

    private const int SubwooferChannel = 3;
    private static readonly uint[] ChannelBits = { 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x200, 0x400 };

    private static void PlayTestSoundOnThisThread(string endpointId, int channel)
    {
        var device = GetDevice(endpointId);
        if (device == IntPtr.Zero)
        {
            return;
        }
        IntPtr client = IntPtr.Zero, render = IntPtr.Zero, format = IntPtr.Zero;
        try
        {
            var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Slot(device, 3);
            var iidClient = IID_IAudioClient;
            if (activate(device, &iidClient, ClsCtxAll, IntPtr.Zero, &client) < 0 || client == IntPtr.Zero)
            {
                return;
            }
            var getMixFormat = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(client, 8);
            if (getMixFormat(client, &format) < 0 || format == IntPtr.Zero)
            {
                return;
            }
            var wf = (WAVEFORMATEX*)format;
            var channels = wf->nChannels;
            var rate = (int)wf->nSamplesPerSec;
            var isFloat = wf->wFormatTag == 3 || (wf->wFormatTag == 0xFFFE && ((WAVEFORMATEXTENSIBLE*)format)->SubFormat == KsDataFormatIeeeFloat);
            if (!isFloat || wf->wBitsPerSample != 32)
            {
                ServiceLog.Warn($"[logitech-speaker] test sound: mix format is not 32-bit float ({wf->wFormatTag}/{wf->wBitsPerSample})");
                return;
            }
            var mask = wf->wFormatTag == 0xFFFE ? ((WAVEFORMATEXTENSIBLE*)format)->dwChannelMask : 0u;
            var position = ChannelPosition(mask, channels, channel);
            if (position < 0)
            {
                ServiceLog.Warn($"[logitech-speaker] test sound: the output has no {LogitechSpeakerProtocol.Channels[channel]} channel (mask 0x{mask:X})");
                return;
            }

            var initialize = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, Guid*, int>)Slot(client, 3);
            if (initialize(client, 0 /* shared */, 0, 30_000_000 /* 3 s */, 0, format, null) < 0)
            {
                return;
            }
            var getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(client, 4);
            uint bufferFrames;
            if (getBufferSize(client, &bufferFrames) < 0)
            {
                return;
            }
            var getService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(client, 14);
            var iidRender = IID_IAudioRenderClient;
            if (getService(client, &iidRender, &render) < 0 || render == IntPtr.Zero)
            {
                return;
            }

            var signal = channel == SubwooferChannel
                ? LogitechSpeakerTestSounds.Dong(rate)
                : LogitechSpeakerTestSounds.PinkNoise(rate);
            var frames = (uint)Math.Min(bufferFrames, (uint)signal.Length);
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(render, 3);
            var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Slot(render, 4);
            IntPtr data;
            if (getBuffer(render, frames, &data) < 0 || data == IntPtr.Zero)
            {
                return;
            }
            var samples = new Span<float>((void*)data, (int)frames * channels);
            samples.Clear();
            for (var i = 0; i < frames; i++)
            {
                samples[i * channels + position] = signal[i];
            }
            releaseBuffer(render, frames, 0);

            var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(client, 10);
            var stop = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(client, 11);
            if (start(client) >= 0)
            {
                Thread.Sleep((int)(frames * 1000L / rate) + 250);
                stop(client);
            }
        }
        finally
        {
            if (format != IntPtr.Zero) CoTaskMemFree(format);
            Release(render);
            Release(client);
            Release(device);
        }
    }

    /// <summary>Where a channel sits in an interleaved frame; -1 when the layout has no such speaker.</summary>
    internal static int ChannelPosition(uint mask, int channels, int channel)
    {
        if (mask == 0)
        {
            return channel < channels ? channel : -1;
        }
        var bit = ChannelBits[channel];
        if ((mask & bit) == 0)
        {
            return -1;
        }
        var position = BitOperations.PopCount(mask & (bit - 1));
        return position < channels ? position : -1;
    }

    // ── COM thread plumbing ──

    private void RunComLoop()
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0x0);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
        {
            return;
        }
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch (Exception ex) { ServiceLog.Warn($"[logitech-speaker] COM worker: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException) { }
    }

    private T RunOnComThread<T>(Func<T> work, T fallback, string op)
    {
        var result = fallback;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(() => { try { result = work(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
        }
        catch (InvalidOperationException) { return fallback; }
        if (!done.Wait(3000))
        {
            ServiceLog.Warn($"[logitech-speaker] {op} timed out");
            return fallback;
        }
        if (error != null)
        {
            ServiceLog.Warn($"[logitech-speaker] {op} failed: {error.Message}");
            return fallback;
        }
        return result;
    }

    private static IntPtr Slot(IntPtr instance, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);

    private static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2);
        release(instance);
    }

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid KsDataFormatIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private const int ClsCtxInprocServer = 0x1;
    private const int ClsCtxAll = 0x17;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // PROPVARIANT (x64): vt + 6 reserved bytes, then a 16-byte union; VT_UI4 0 is all zeros past vt.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1; public ushort r2; public ushort r3; public IntPtr p; public IntPtr p2; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr ptr);
}
#endif
