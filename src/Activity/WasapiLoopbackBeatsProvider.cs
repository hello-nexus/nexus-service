using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Activity;

/// <summary>
/// Windows-only audio provider that captures via WASAPI the endpoint picked in
/// Lighting settings while it is plugged in, otherwise the default of the picked
/// direction: loopback of a render endpoint (whatever is playing) or a capture
/// endpoint (microphone / line-in). An input pick falls back to the default
/// output loopback on the first open when no input endpoint opens.
///
/// Windows path; <see cref="BeatsProvider"/> is the Linux ffmpeg fallback.
/// WASAPI is built into Windows (Vista+) and needs no extra binary, driver
/// support or user setup.
///
/// WASAPI COM interfaces are called via direct vtable dispatch using function
/// pointers. This avoids ComImport's reflection-based marshalling (which
/// Native AOT does not support) and keeps every call blittable. Samples
/// arrive in the device's native mix format (typically
/// 48 kHz float32 stereo) and are downmixed to mono as they're read, then
/// fed to the shared beat-detection / FFT pipeline that backs AudioState.
/// </summary>
public sealed class WasapiLoopbackBeatsProvider : IBeatsProvider
{
    // A stream stays bound to the endpoint it opened, so the loop re-resolves
    // the target this often to follow a new pick, a default switch or an unplug.
    private const int TargetCheckMs = 1000;

    private readonly AudioAnalyser _analyser = new();
    private readonly IConfigStore _store;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private bool _running;

    public WasapiLoopbackBeatsProvider(IConfigStore store) => _store = store;

    public event Action? OnBeat;

    public void Start()
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
        }

        CancellationTokenSource cts;
        CancellationToken token;
        lock (_lock)
        {
            cts = _cts!;
            token = cts.Token;
        }
        try
        {
            // Dedicated thread so the WASAPI COM objects are accessed from a
            // single, deterministic thread the whole time (Task.Run would
            // thread-hop on every await and the COM pointers are apartment-
            // affine). Background so it doesn't block CLR shutdown.
            _captureThread = new Thread(() => CaptureLoop(cts, token))
            {
                IsBackground = true,
                Name = "wasapi-loopback-capture",
            };
            _captureThread.Start();
        }
        catch (Exception ex)
        {
            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] failed to start: {ex.Message}");
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _running = false;
                }
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            cts = _cts;
            _cts = null;
        }

        try
        { cts?.Cancel(); }
        catch { }
        try
        { cts?.Dispose(); }
        catch { }

        AudioState.Reset();
    }

    public void Dispose() => Stop();

    private void CaptureLoop(CancellationTokenSource owner, CancellationToken ct)
    {
        var (initialInput, initial) = PreferredTarget();
        var cap = OpenWithFallback(initialInput, initial, fallbackToOutput: true, sameDirectionDefault: true, out var openError);
        if (cap is null)
        {
            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] open failed: {openError}");
            // Nothing opened: let a later Start() retry, unless a newer run already replaced this one.
            lock (_lock)
            {
                if (ReferenceEquals(_cts, owner))
                {
                    _running = false;
                }
            }
            return;
        }
        Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log(
            $"[wasapi] opened {(cap.Input ? "input" : "output")} {cap.DeviceId}: {cap.SampleRate} Hz, {cap.Channels} ch"
            + (openError is null ? "" : $" (fell back after: {openError})"));

        try
        {
            var window = new float[AudioAnalyser.WindowSize];
            int written = 0;
            int channels = cap.Channels;
            int targetRate = AudioAnalyser.SampleRate;
            double decimAcc = 0;
            double decimStep = (double)cap.SampleRate / targetRate;
            long lastTargetTick = Environment.TickCount64;
            var failedTarget = "";
            long totalFrames = 0;
            long totalAnalyses = 0;
            long lastLogTick = Environment.TickCount64;
            long lastPacketTick = Environment.TickCount64;
            var stalled = false;
            // WASAPI loopback (not microphone capture) stops emitting packets entirely when the endpoint
            // is silent. After this timeout we synthesise silence windows so
            // smoothed AudioState decays to zero and shaders fall back to idle.
            const int silenceTimeoutMs = 250;
            var silenceWindow = new float[AudioAnalyser.WindowSize];

            while (!ct.IsCancellationRequested)
            {
                if (Environment.TickCount64 - lastTargetTick >= TargetCheckMs)
                {
                    lastTargetTick = Environment.TickCount64;
                    var (preferredInput, preferred) = PreferredTarget();
                    var target = cap.TargetId(preferredInput, preferred);
                    // Invalidated covers a replug of the same endpoint (same id)
                    // and a format change in Sound settings.
                    if (target.Length > 0 && (target != cap.DeviceId || preferredInput != cap.Input || cap.Invalidated))
                    {
                        // Across directions a refused pick falls back to that direction's default, not the old stream.
                        var next = OpenWithFallback(preferredInput, preferred, fallbackToOutput: false,
                            sameDirectionDefault: preferredInput != cap.Input, out var error);
                        if (next is not null)
                        {
                            cap.Dispose();
                            cap = next;
                            channels = cap.Channels;
                            decimStep = (double)cap.SampleRate / targetRate;
                            decimAcc = 0;
                            written = 0;
                            failedTarget = "";
                            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] switched endpoint: {cap.SampleRate} Hz, {cap.Channels} ch");
                        }
                        else
                        {
                            // Retried every check, logged once per target.
                            if (target != failedTarget)
                            {
                                Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] switch failed: {error}");
                            }
                            failedTarget = target;
                        }
                    }
                }

                int frames = cap.Read(out var src);
                long now = Environment.TickCount64;
                if (now - lastLogTick > 5000)
                {
                    // Only transitions are worth a line; an unconditional beat
                    // every 5s wrote 11190 lines of one 17-hour log.
                    var nowStalled = totalAnalyses == 0;
                    if (nowStalled != stalled)
                    {
                        Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log(nowStalled
                            ? $"[wasapi] no analyses in last {(now - lastLogTick) / 1000}s ({totalFrames} frames)"
                            : $"[wasapi] capture resumed: {totalFrames} frames, {totalAnalyses} analyses in last {(now - lastLogTick) / 1000}s");
                        stalled = nowStalled;
                    }
                    totalFrames = 0;
                    totalAnalyses = 0;
                    lastLogTick = now;
                }
                if (frames == 0)
                {
                    // No packets: after the timeout, analyse a synthetic
                    // silence window so audio state drains to zero. The peak
                    // meter still works during loopback silence, so forward it
                    // (shaders track volume changes with nothing playing).
                    if (now - lastPacketTick > silenceTimeoutMs)
                    {
                        _analyser.Analyse(silenceWindow, cap.GetPeak());
                        totalAnalyses++;
                        try
                        { OnBeat?.Invoke(); }
                        catch { /* swallow subscriber errors */ }
                        lastPacketTick = now - 50;   // pace the synthetic rate
                    }
                    Thread.Sleep(10);
                    continue;
                }
                totalFrames += frames;
                lastPacketTick = now;

                for (int f = 0; f < frames; f++)
                {
                    float mono = 0f;
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        mono += src[baseIdx + c];
                    }
                    mono /= channels;

                    decimAcc += 1.0;
                    if (decimAcc >= decimStep)
                    {
                        decimAcc -= decimStep;
                        window[written++] = mono;
                        if (written >= window.Length)
                        {
                            _analyser.Analyse(window, cap.GetPeak());
                            totalAnalyses++;
                            try
                            { OnBeat?.Invoke(); }
                            catch { /* swallow subscriber errors */ }
                            written = 0;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] capture loop crashed: {ex}");
        }
        finally
        {
            cap?.Dispose();
        }
    }

    /// <summary>
    /// Opens the pick, then (optionally) the same-direction default, then (optionally) the default output so
    /// capture keeps running when no microphone resolves. Error joins every failed attempt, in order.
    /// </summary>
    private static WasapiCapturer? OpenWithFallback(bool input, string id, bool fallbackToOutput, bool sameDirectionDefault, out string? error)
    {
        error = null;
        var attempts = new List<(bool Input, string Id)> { (input, id) };
        if (id.Length > 0 && sameDirectionDefault)
        {
            attempts.Add((input, ""));
        }
        if (input && fallbackToOutput)
        {
            attempts.Add((false, ""));
        }
        foreach (var (tryInput, tryId) in attempts)
        {
            try
            { return WasapiCapturer.Open(tryInput, tryId); }
            catch (Exception ex)
            { error = error is null ? ex.Message : $"{error} | {ex.Message}"; }
        }
        return null;
    }

    private (bool Input, string Id) PreferredTarget()
    {
        var l = _store.Load().Lighting;
        return (l.AudioOutputDeviceDirection == "input", l.AudioOutputDeviceId ?? "");
    }
}

/// <summary>
/// Thin wrapper around the raw WASAPI IAudioClient / IAudioCaptureClient
/// interfaces. Caller asks <see cref="Read"/> for more frames; the wrapper
/// hands back a span into a reused scratch buffer holding interleaved
/// float32 samples. No managed allocations on the hot path.
/// </summary>
internal sealed unsafe class WasapiCapturer : IDisposable
{
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    /// <summary>IMMDevice id of the endpoint this capturer is bound to.</summary>
    public string DeviceId { get; private set; } = "";
    /// <summary>True when bound to a capture endpoint (microphone / line-in), false for render loopback.</summary>
    public bool Input { get; private set; }
    /// <summary>The stream died under us (endpoint removed or reformatted); only a reopen recovers it.</summary>
    public bool Invalidated { get; private set; }

    private IntPtr _enumerator;
    private IntPtr _device;
    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private IntPtr _meterInfo;
    private IntPtr _mixFormat;
    private float[] _scratch = new float[0];
    private bool _started;

    // CLSID / IID constants for WASAPI COM surface.
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private const int CLSCTX_ALL = 1 | 2 | 4 | 16;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int COINIT_MULTITHREADED = 0;
    private const int eRender = 0;
    private const int eCapture = 1;
    private const int eConsole = 0;
    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const long ReftimesPerMs = 10000L;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [DllImport("ole32", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, int ctx, in Guid riid, out IntPtr ppv);

    [DllImport("ole32", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr ptr);

    /// <summary>Opens <paramref name="preferredId"/> when it is plugged in, else the default endpoint of the same direction.</summary>
    public static WasapiCapturer Open(bool input, string preferredId)
    {
        // COINIT_MULTITHREADED is safe to call repeatedly; COM tolerates it
        // returning RPC_E_CHANGED_MODE if something else already initialised
        // the apartment, and our usage doesn't care which apartment it lands in.
        _ = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);

        var cap = new WasapiCapturer { Input = input };
        try
        {
            int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL,
                IID_IMMDeviceEnumerator, out cap._enumerator);
            ThrowOnHr(hr, "CoCreateInstance(MMDeviceEnumerator)");

            cap._device = cap.ResolveDevice(input, preferredId);
            cap.DeviceId = GetId(cap._device);

            // IMMDevice.Activate (slot 3 after IUnknown).
            var devVtbl = *(IntPtr**)cap._device;
            var activate = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, uint, IntPtr, out IntPtr, int>)devVtbl[3];
            hr = activate(cap._device, IID_IAudioClient, (uint)CLSCTX_ALL, IntPtr.Zero, out cap._audioClient);
            ThrowOnHr(hr, "IMMDevice.Activate(IAudioClient)");

            // IAudioClient.GetMixFormat (slot 8).
            var acVtbl = *(IntPtr**)cap._audioClient;
            var getMixFormat = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)acVtbl[8];
            hr = getMixFormat(cap._audioClient, out cap._mixFormat);
            ThrowOnHr(hr, "GetMixFormat");

            // Read WAVEFORMATEX header to extract sample rate + channel count.
            var fmt = (WaveFormatEx*)cap._mixFormat;
            cap.SampleRate = (int)fmt->nSamplesPerSec;
            cap.Channels = fmt->nChannels;
            int bitsPerSample = fmt->wBitsPerSample;

            // The device format is almost always IEEE float32 at 48 kHz for
            // modern Windows sound cards; if it's something else we'll still
            // read samples but the math may be off. Log so we can diagnose.
            if (bitsPerSample != 32)
            {
                Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[wasapi] non-float format detected (bits={bitsPerSample}); will read raw");
            }

            // The capture loop sleeps briefly between drained packets, well
            // below this buffer's overflow window.
            long hnsBuffer = 200 * ReftimesPerMs;
            // IAudioClient.Initialize (slot 3).
            var initialize = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)acVtbl[3];
            hr = initialize(cap._audioClient, AUDCLNT_SHAREMODE_SHARED,
                input ? 0u : AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuffer, 0, cap._mixFormat, IntPtr.Zero);
            ThrowOnHr(hr, "IAudioClient.Initialize");

            // IAudioClient.GetService (slot 14) - get IAudioCaptureClient.
            var getService = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)acVtbl[14];
            hr = getService(cap._audioClient, IID_IAudioCaptureClient, out cap._captureClient);
            ThrowOnHr(hr, "GetService(IAudioCaptureClient)");

            // IAudioMeterInformation off the same IMMDevice. Same Activate slot
            // as IAudioClient. Read separately each frame for the calibrated
            // post-volume peak that survives the stream going silent. If
            // activation fails (rare; some virtual endpoints don't expose it)
            // we leave _meterInfo at zero and fall back to in-window peak.
            try
            {
                int mhr = activate(cap._device, IID_IAudioMeterInformation, (uint)CLSCTX_ALL, IntPtr.Zero, out cap._meterInfo);
                if (mhr < 0)
                {
                    cap._meterInfo = IntPtr.Zero;
                }
            }
            catch
            {
                cap._meterInfo = IntPtr.Zero;
            }

            // IAudioClient.Start (slot 10).
            var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)acVtbl[10];
            hr = start(cap._audioClient);
            ThrowOnHr(hr, "IAudioClient.Start");
            cap._started = true;

            return cap;
        }
        catch
        {
            cap.Dispose();
            throw;
        }
    }

    /// <summary>Id of the endpoint <see cref="Open"/> would bind right now; empty when none resolves.</summary>
    public string TargetId(bool input, string preferredId)
    {
        try
        {
            var dev = ResolveDevice(input, preferredId);
            try
            { return GetId(dev); }
            finally
            { ReleaseCom(ref dev); }
        }
        catch
        {
            return "";
        }
    }

    private IntPtr ResolveDevice(bool input, string preferredId)
    {
        var enumVtbl = *(IntPtr**)_enumerator;
        if (preferredId.Length > 0)
        {
            // IMMDeviceEnumerator.GetDevice (slot 5) also resolves an unplugged
            // endpoint, so the pick wins only while IMMDevice.GetState (slot 6)
            // reports it active.
            var getDevice = (delegate* unmanaged[Stdcall]<IntPtr, char*, out IntPtr, int>)enumVtbl[5];
            int ghr;
            IntPtr picked;
            fixed (char* id = preferredId)
            {
                ghr = getDevice(_enumerator, id, out picked);
            }
            if (ghr >= 0 && picked != IntPtr.Zero)
            {
                var getState = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)(*(IntPtr**)picked)[6];
                if (getState(picked, out var state) >= 0 && state == DEVICE_STATE_ACTIVE)
                {
                    return picked;
                }
                ReleaseCom(ref picked);
            }
        }

        // IMMDeviceEnumerator.GetDefaultAudioEndpoint (vtable slot 4 after
        // 3 IUnknown entries).
        var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, out IntPtr, int>)enumVtbl[4];
        int hr = getDefault(_enumerator, input ? eCapture : eRender, eConsole, out var device);
        ThrowOnHr(hr, "GetDefaultAudioEndpoint");
        return device;
    }

    private static string GetId(IntPtr device)
    {
        // IMMDevice.GetId (slot 5); the string is CoTaskMem-owned.
        var getId = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)(*(IntPtr**)device)[5];
        if (getId(device, out var str) < 0 || str == IntPtr.Zero)
        {
            return "";
        }
        try
        { return Marshal.PtrToStringUni(str) ?? ""; }
        finally
        { CoTaskMemFree(str); }
    }

    public int Read(out float[] samples)
    {
        // IAudioCaptureClient.GetNextPacketSize (slot 5), GetBuffer (slot 3),
        // ReleaseBuffer (slot 4). Pull one packet at a time; the analysis
        // ring buffer handles accumulation.
        var ccVtbl = *(IntPtr**)_captureClient;
        var getNextPacketSize = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)ccVtbl[5];
        int hr = getNextPacketSize(_captureClient, out uint packetFrames);
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
        {
            Invalidated = true;
        }
        if (hr != 0 || packetFrames == 0)
        {
            samples = Array.Empty<float>();
            return 0;
        }

        var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out uint, out uint, out long, out long, int>)ccVtbl[3];
        hr = getBuffer(_captureClient, out IntPtr pData, out uint framesRead, out uint flags, out _, out _);
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
        {
            Invalidated = true;
        }
        if (hr != 0 || framesRead == 0)
        {
            samples = Array.Empty<float>();
            return 0;
        }

        int sampleCount = (int)framesRead * Channels;
        if (_scratch.Length < sampleCount)
        {
            _scratch = new float[sampleCount];
        }

        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
        {
            // WASAPI tells us the packet is silent; skip the copy.
            Array.Clear(_scratch, 0, sampleCount);
        }
        else
        {
            // IEEE float32 samples interleaved by channel.
            fixed (float* dst = _scratch)
            {
                System.Buffer.MemoryCopy((void*)pData, dst, sampleCount * sizeof(float), sampleCount * sizeof(float));
            }
        }

        var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)ccVtbl[4];
        releaseBuffer(_captureClient, framesRead);

        samples = _scratch;
        return (int)framesRead;
    }

    /// <summary>
    /// Returns the calibrated post-volume peak [0,1] of the captured
    /// endpoint, or null if IAudioMeterInformation is unavailable. Same value
    /// the Windows volume mixer indicator uses; works even when WASAPI
    /// loopback emits no packets (silence).
    /// </summary>
    public float? GetPeak()
    {
        if (_meterInfo == IntPtr.Zero) return null;
        try
        {
            // IAudioMeterInformation.GetPeakValue (vtable slot 3 after
            // IUnknown's QI/AddRef/Release).
            var vtbl = *(IntPtr**)_meterInfo;
            var getPeak = (delegate* unmanaged[Stdcall]<IntPtr, out float, int>)vtbl[3];
            int hr = getPeak(_meterInfo, out float peak);
            if (hr < 0) return null;
            if (peak < 0f) peak = 0f;
            else if (peak > 1f) peak = 1f;
            return peak;
        }
        catch
        {
            return null;
        }
    }

    private static void ThrowOnHr(int hr, string where)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{where} failed with HRESULT 0x{hr:X8}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_started && _audioClient != IntPtr.Zero)
            {
                var vtbl = *(IntPtr**)_audioClient;
                var stop = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[11];
                stop(_audioClient);
            }
        }
        catch { }
        _started = false;

        if (_mixFormat != IntPtr.Zero) { CoTaskMemFree(_mixFormat); _mixFormat = IntPtr.Zero; }
        ReleaseCom(ref _meterInfo);
        ReleaseCom(ref _captureClient);
        ReleaseCom(ref _audioClient);
        ReleaseCom(ref _device);
        ReleaseCom(ref _enumerator);
    }

    private static void ReleaseCom(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try
        {
            var vtbl = *(IntPtr**)p;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            release(p);
        }
        catch { }
        p = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}
