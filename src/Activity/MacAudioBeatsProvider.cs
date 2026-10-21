using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS audio capture: spawns the bundled <c>nexus-audio-helper</c> Swift
/// sidecar, which uses ScreenCaptureKit to grab the system audio output and
/// pipes interleaved float32 stereo PCM at 44100 Hz to its stdout. We read from the pipe
/// in <see cref="AudioAnalyser.WindowSize"/>-sample windows and feed the
/// shared analyser, identical to the Linux ffmpeg path and the Windows WASAPI
/// path downstream.
///
/// Why a sidecar instead of P/Invoke: SCK is Swift / Objective-C only and
/// has main-actor + autorelease semantics that fight Native AOT. Keeping it
/// in a tiny Swift binary lets us call Apple APIs the way Apple expects and
/// keeps the C# side a normal stdin/stdout pipe consumer.
///
/// TCC: the helper triggers a Screen Recording permission prompt on first
/// run. macOS attributes the prompt to the parent bundle (Nexus.app)
/// because the helper lives under <c>Contents/MacOS</c>. If the helper
/// binary is missing (dev runs from <c>bin/Debug</c> without
/// <c>build-helper.sh</c> having been run) the provider logs a clear error
/// and stays inactive - shaders fall back to their idle animation.
/// </summary>
public sealed class MacAudioBeatsProvider : IBeatsProvider
{
    private const int SampleRate = AudioAnalyser.SampleRate;
    private const int WindowSize = AudioAnalyser.WindowSize;
    private const int BytesPerSample = sizeof(float);
    private const int WindowBytes = WindowSize * 2 * BytesPerSample;

    private readonly AudioAnalyser _analyser = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Process? _proc;
    private Task? _captureTask;
    private bool _running;

    public event Action? OnBeat;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }

        try
        {
            _cts = new CancellationTokenSource();
            StartHelper();
            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[mac-audio] start failed: {ex.Message}");
            lock (_lock) { _running = false; }
        }
    }

    public void Stop()
    {
        Task? captureTask;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            captureTask = _captureTask;
            cts = _cts;
            _captureTask = null;
            _cts = null;
        }

        // Cancel + kill first so the capture loop sees EOF on the pipe and
        // exits, then drain the task so any subsequent Start() doesn't race
        // a zombie loop holding the old _proc handle.
        try { cts?.Cancel(); } catch { }
        KillProcess();
        try { captureTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { cts?.Dispose(); } catch { }

        _analyser.Reset();
    }

    public void Dispose() => Stop();

    private void StartHelper()
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "nexus-audio-helper");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                $"audio helper missing at {helperPath} - run Bundled/macos/audio-helper/build-helper.sh or rebuild via the .app bundle");
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");

        // Drain stderr so the helper's log lines surface in the service log
        // and the pipe doesn't fill up.
        _ = Task.Run(async () =>
        {
            try
            {
                while (_proc is { HasExited: false })
                {
                    var line = await _proc.StandardError.ReadLineAsync();
                    if (line is null) break;
                    if (line.Length > 0)
                    {
                        Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[mac-audio] helper: {line}");
                    }
                }
            }
            catch { /* swallow */ }
        });

        Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log(
            $"[mac-audio] helper started (pid {_proc.Id}); awaiting samples on stdout");
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        if (_proc is null) return;

        var buffer = new byte[WindowBytes];
        var stream = _proc.StandardOutput.BaseStream;
        var interleaved = new float[WindowSize * 2];
        var samples = new float[WindowSize];
        var left = new float[WindowSize];
        var right = new float[WindowSize];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = 0;
                while (read < WindowBytes)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, WindowBytes - read), ct);
                    if (n <= 0) return; // helper exited
                    read += n;
                }

                Buffer.BlockCopy(buffer, 0, interleaved, 0, WindowBytes);
                for (int i = 0; i < WindowSize; i++)
                {
                    float l = interleaved[i * 2];
                    float r = interleaved[i * 2 + 1];
                    left[i] = l;
                    right[i] = r;
                    samples[i] = (l + r) * 0.5f;
                }

                _analyser.Analyse(samples, null, left, right);
                try { OnBeat?.Invoke(); } catch { /* swallow subscriber errors */ }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* pipe closed */ }
        catch (Exception ex)
        {
            Nexus.Service.Lighting.Engine.Gpu.GpuContext.Log($"[mac-audio] capture loop crashed: {ex.Message}");
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_proc is not null)
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill(entireProcessTree: true);
                    _proc.WaitForExit(1000);
                }
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }
}
