using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.Render;

public sealed class HeadlessBrowser : IDisposable
{
    internal const string FdShim = "exec \"$0\" \"$@\" 3<&0 4>&1 </dev/null >/dev/null";

    private const int StderrLinesLogged = 20;

    private readonly Process _process;
    private int _disposed;

    private HeadlessBrowser(Process process, CdpPipeClient cdp, string browserPath)
    {
        _process = process;
        Cdp = cdp;
        BrowserPath = browserPath;
    }

    public CdpPipeClient Cdp { get; }
    public string BrowserPath { get; }
    public int ProcessId => _process.Id;
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited || Cdp.IsClosed;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public GpuTier Tier { get; private init; }

    /// <summary>
    /// Best effort: set when browser stderr reports the GPU process dying or being unusable. Chromium
    /// does not promise to print these, so a false value proves nothing.
    /// </summary>
    public bool GpuProcessFailed => Volatile.Read(ref _gpuProcessFailed) != 0;
    private int _gpuProcessFailed;

    private static readonly string[] GpuFailureMarkers =
    {
        "GPU process exited unexpectedly", "GPU process isn't usable", "Exiting GPU process",
    };

    internal static bool IsGpuFailureLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        foreach (var marker in GpuFailureMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Forgets markers seen so far; a tier accepted by the probe is judged only on what follows.</summary>
    public void ClearGpuFailure() => Volatile.Write(ref _gpuProcessFailed, 0);

    /// <summary>Kills the browser tree without waiting; reap with <see cref="Dispose"/> off the caller's path.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private const string WebGlRendererProbe =
        "(() => { const c = document.createElement('canvas');"
        + " const gl = c.getContext('webgl2') || c.getContext('webgl'); if (!gl) return 'none';"
        + " const e = gl.getExtension('WEBGL_debug_renderer_info');"
        + " return String(e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)); })()";

    /// <summary>
    /// The renderer string the page's WebGL context actually got. A GPU flag is only a request:
    /// Chromium drops to SwiftShader or llvmpipe when the driver is missing, so the flags alone prove nothing.
    /// </summary>
    public async Task<string> ProbeRendererAsync(CancellationToken ct)
    {
        var created = await Cdp.SendAsync("Target.createTarget", w =>
        {
            w.WriteString("url", "about:blank");
            w.WriteNumber("width", 64);
            w.WriteNumber("height", 64);
            w.WriteBoolean("newWindow", true);
        }, ct: ct).ConfigureAwait(false);
        var targetId = created.GetProperty("targetId").GetString();
        try
        {
            var attached = await Cdp.SendAsync("Target.attachToTarget", w =>
            {
                w.WriteString("targetId", targetId);
                w.WriteBoolean("flatten", true);
            }, ct: ct).ConfigureAwait(false);
            var session = attached.GetProperty("sessionId").GetString();
            var result = await Cdp.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", WebGlRendererProbe);
                w.WriteBoolean("returnByValue", true);
            }, session, ct: ct, timeoutMs: 15000).ConfigureAwait(false);
            return result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v)
                ? v.GetString() ?? "none"
                : "none";
        }
        finally
        {
            if (targetId is not null && !Cdp.IsClosed)
            {
                Cdp.Post("Target.closeTarget", w => w.WriteString("targetId", targetId));
            }
        }
    }

    internal static List<string> Arguments(string profileDir, GpuTier tier = GpuTier.Software)
    {
        var args = BaseArguments(profileDir);
        // The trailing about:blank stays last.
        args.InsertRange(args.Count - 1, GpuTiers.Arguments(tier));
        return args;
    }

    private static List<string> BaseArguments(string profileDir) => new()
    {
        "--headless=new",
        "--remote-debugging-pipe",
        $"--user-data-dir={profileDir}",
        "--no-first-run",
        "--no-default-browser-check",

        "--password-store=basic",
        "--disable-extensions",
        "--disable-sync",
        "--mute-audio",
        "--hide-scrollbars",

        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
        "--autoplay-policy=no-user-gesture-required",
        "about:blank",
    };

    public static async Task<HeadlessBrowser> LaunchAsync(string browserPath, string profileDir,
        Func<string, List<string>, (string File, List<string> Args)> wrapSpawn,
        Action<string>? log, CancellationToken ct, GpuTier tier = GpuTier.Software)
    {
        var shellArgs = new List<string> { "-c", FdShim, browserPath };
        shellArgs.AddRange(Arguments(profileDir, tier));
        var (file, args) = wrapSpawn("/bin/sh", shellArgs);

        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        var process = Process.Start(psi) ?? throw new IOException($"could not start {browserPath}");
        var cdp = new CdpPipeClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        var browser = new HeadlessBrowser(process, cdp, browserPath) { Tier = tier };
        _ = DrainStderrAsync(process, log, browser);
        try
        {
            var version = await cdp.SendAsync("Browser.getVersion", ct: ct, timeoutMs: 20000).ConfigureAwait(false);
            var product = version.TryGetProperty("product", out var p) ? p.GetString() : "?";
            log?.Invoke($"headless browser up: {product} pid {process.Id} ({browserPath})");
            return browser;
        }
        catch
        {
            browser.Kill();
            _ = Task.Run(browser.Dispose);
            throw;
        }
    }

    private static async Task DrainStderrAsync(Process process, Action<string>? log, HeadlessBrowser browser)
    {
        var logged = 0;
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (IsGpuFailureLine(line))
                {
                    Volatile.Write(ref browser._gpuProcessFailed, 1);
                }
                if (logged < StderrLinesLogged && !string.IsNullOrWhiteSpace(line))
                {
                    logged++;
                    log?.Invoke($"browser: {line}");
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Cdp.Dispose();
        try
        {
            if (!_process.WaitForExit(3000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
        }
        _process.Dispose();
    }
}
