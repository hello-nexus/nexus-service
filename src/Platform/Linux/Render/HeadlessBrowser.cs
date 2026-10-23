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

    internal static List<string> Arguments(string profileDir) => new()
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
        Action<string>? log, CancellationToken ct)
    {
        var shellArgs = new List<string> { "-c", FdShim, browserPath };
        shellArgs.AddRange(Arguments(profileDir));
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
        var browser = new HeadlessBrowser(process, cdp, browserPath);
        _ = DrainStderrAsync(process, log);
        try
        {
            var version = await cdp.SendAsync("Browser.getVersion", ct: ct, timeoutMs: 20000).ConfigureAwait(false);
            var product = version.TryGetProperty("product", out var p) ? p.GetString() : "?";
            log?.Invoke($"headless browser up: {product} pid {process.Id} ({browserPath})");
            return browser;
        }
        catch
        {
            browser.Dispose();
            throw;
        }
    }

    private static async Task DrainStderrAsync(Process process, Action<string>? log)
    {
        var logged = 0;
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
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
