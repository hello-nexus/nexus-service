using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux audio beat detector backed by ffmpeg (PulseAudio default monitor).
/// Reads raw interleaved float32 stereo PCM at 44100 Hz from ffmpeg's stdout and feeds it
/// to the shared <see cref="AudioAnalyser"/> pipeline that powers AudioState.
///
/// Windows uses <see cref="WasapiLoopbackBeatsProvider"/> (native WASAPI
/// loopback, no subprocess). macOS uses <see cref="MacAudioBeatsProvider"/>
/// (native CoreAudio AudioQueue, no subprocess). This provider is the
/// fallback path for Linux where neither WASAPI nor CoreAudio is available.
/// </summary>
public sealed class BeatsProvider : IBeatsProvider
{
    private const int SampleRate = AudioAnalyser.SampleRate;
    private const int Channels = 2;
    private const int WindowSize = AudioAnalyser.WindowSize;
    private const int BytesPerSample = 4;             // float32
    private const int WindowBytes = WindowSize * Channels * BytesPerSample;

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
            if (_running)
            {
                return;
            }

            _running = true;
        }

        try
        {
            _cts = new CancellationTokenSource();
            StartFfmpeg();
            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[beats] failed to start: {ex.Message}");
            lock (_lock)
            { _running = false; }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        try
        { _cts?.Cancel(); }
        catch { }
        KillProcess();
        try
        { _cts?.Dispose(); }
        catch { }
        _cts = null;

        // Zero shared audio uniforms so shaders fall back to idle on stop.
        _analyser.Reset();
    }

    public void Dispose() => Stop();

    private void StartFfmpeg()
    {
        var ffmpegPath = FfmpegResolver.Path
            ?? throw new InvalidOperationException("ffmpeg not found on PATH or in bundled locations");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        // PulseAudio default monitor: captures the default sink. PipeWire
        // boxes ship a pulseaudio shim, so this works there too.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("pulse");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("default");

        // Output: raw interleaved float32 stereo PCM to stdout.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_f32le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(SampleRate.ToString());
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(Channels.ToString());
        psi.ArgumentList.Add("-");

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg Process.Start returned null");
        FfmpegTracker.Track(_proc.Id);

        // Drain stderr so the pipe doesn't stall.
        _ = Task.Run(async () =>
        {
            try
            {
                while (_proc is { HasExited: false })
                {
                    var line = await _proc.StandardError.ReadLineAsync();
                    if (line is null)
                    {
                        break;
                    }
                    // Surface ffmpeg errors to the service log.
                    if (line.Length > 0)
                    {
                        Console.Error.WriteLine($"[beats] ffmpeg: {line}");
                    }
                }
            }
            catch { /* swallow */ }
        });
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        if (_proc is null)
        {
            return;
        }

        var buffer = new byte[WindowBytes];
        var stream = _proc.StandardOutput.BaseStream;
        var interleaved = new float[WindowSize * Channels];
        var samples = new float[WindowSize];
        var left = new float[WindowSize];
        var right = new float[WindowSize];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Read a full analysis window.
                int read = 0;
                while (read < WindowBytes)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, WindowBytes - read), ct);
                    if (n <= 0)
                    {
                        return;   // ffmpeg exited
                    }

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
                try
                { OnBeat?.Invoke(); }
                catch { /* swallow subscriber errors */ }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* pipe closed */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[beats] capture loop crashed: {ex.Message}");
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_proc is not null)
            {
                FfmpegTracker.Untrack(_proc.Id);
                if (!_proc.HasExited)
                {
                    _proc.Kill(entireProcessTree: true);
                    _proc.WaitForExit(1000);
                }
            }
        }
        catch { }
        try
        { _proc?.Dispose(); }
        catch { }
        _proc = null;
    }
}
