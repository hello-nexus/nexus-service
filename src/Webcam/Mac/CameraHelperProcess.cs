using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Webcam.Mac;

/// <summary>
/// Launches the bundled camera helper sidecar with all three standard pipes
/// redirected. The helper is Swift (VideoToolbox decode + CMIO sink feed) for
/// the same reason as the audio helper: Apple frameworks fight Native AOT.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class ProcessCameraHelperLauncher : ICameraHelperLauncher
{
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "nexus-camera-helper");

    public ICameraHelperProcess Launch()
    {
        if (!File.Exists(HelperPath))
        {
            throw new InvalidOperationException(
                $"camera helper missing at {HelperPath}; this build does not include the camera extension, so the virtual camera is unavailable");
        }

        var psi = new ProcessStartInfo
        {
            FileName = HelperPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("camera helper failed to start");
        return new ProcessCameraHelper(process);
    }
}

/// <summary>
/// Real-process implementation of the helper seam. Stderr is drained on a
/// background task so the pipe never fills; lines flow to the service log and
/// a bounded tail is kept for error surfacing.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class ProcessCameraHelper : ICameraHelperProcess
{
    private const int MaxStderrLines = 32;
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrLock = new();

    internal ProcessCameraHelper(Process process)
    {
        _process = process;
        _ = Task.Run(DrainStandardErrorAsync);
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public string StandardErrorSnapshot
    {
        get
        {
            lock (_stderrLock)
            {
                return string.Join("; ", _stderr);
            }
        }
    }

    public ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
        _process.StandardOutput.ReadLineAsync(cancellationToken);

    public void CloseStandardInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // pipe already broken
        }
    }

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit((int)KillGrace.TotalMilliseconds);
            }
        }
        catch
        {
            // already gone
        }
    }

    public void Dispose()
    {
        try
        {
            _process.Dispose();
        }
        catch
        {
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                ServiceLog.Info($"[webcam] helper: {line}");
                lock (_stderrLock)
                {
                    _stderr.Enqueue(line);
                    if (_stderr.Count > MaxStderrLines)
                        _stderr.Dequeue();
                }
            }
        }
        catch
        {
            // pipe closed during teardown
        }
    }
}
