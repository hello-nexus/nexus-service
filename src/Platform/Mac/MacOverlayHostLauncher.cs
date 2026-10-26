using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Nexus.Service.Auth;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// macOS implementation of <see cref="IOverlayHost"/>. Spawns the Swift
/// sidecar <c>nexus-overlay-helper</c> from
/// <c>Nexus.app/Contents/MacOS/</c>, which renders one transparent
/// borderless NSWindow + WKWebView per NSScreen. Mirrors the
/// <see cref="PanelOverlayHostLauncher"/> Windows path: spawn on enable,
/// kill on disable, restart on crash with linear backoff.
///
/// Communication with the helper:
/// * Boot: CLI args carry the service port + auth token.
/// * Assignment changes: a stdin line pokes the helper to refetch the kiosk assignments.
/// * Stdin EOF kills the helper - guarantees the sidecar dies if the
///   service crashes without calling Stop.
/// </summary>
public sealed class MacOverlayHostLauncher : IOverlayHost
{
    private readonly TokenService _tokens;
    private readonly object _lock = new();
    private static int _servicePort = 9400;

    private Process? _process;
    private DateTime _lastSpawnUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private volatile bool _stopRequested;

    public MacOverlayHostLauncher(TokenService tokens)
    {
        _tokens = tokens;
    }

    public bool IsRunning => _process is { HasExited: false };

    public static void Configure(int servicePort) => _servicePort = servicePort;

    public bool Start()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        lock (_lock)
        {
            if (IsRunning) return true;
            var helperPath = ResolveHelperPath();
            if (helperPath is null || !File.Exists(helperPath))
            {
                Console.Error.WriteLine($"[overlay-helper] helper not found at '{helperPath ?? "<null>"}'; the .app bundle's build-app.sh must compile and copy nexus-overlay-helper. Panel kiosks disabled.");
                return false;
            }

            try
            {
                _stopRequested = false;
                _lastSpawnUtc = DateTime.UtcNow;
                var psi = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(helperPath)!,
                    // RedirectStandardInput so the helper can detect parent
                    // death via stdin EOF. Stdout/stderr stay attached to
                    // the service so helper logs land in the same console.
                    RedirectStandardInput = true,
                };
                psi.ArgumentList.Add($"--port={_servicePort}");
                psi.ArgumentList.Add($"--token={_tokens.Token}");

                _process = Process.Start(psi);
                if (_process is not null)
                {
                    _process.EnableRaisingEvents = true;
                    _process.Exited += OnExited;
                    Console.WriteLine($"[overlay-helper] started pid {_process.Id}");
                }
                return IsRunning;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-helper] spawn failed: {ex.Message}");
                return false;
            }
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        Process? proc;
        lock (_lock)
        {
            proc = _process;
            _process = null;
        }
        if (proc is null) return;
        try { proc.Exited -= OnExited; } catch { }
        try
        {
            if (!proc.HasExited)
            {
                // Closing stdin signals EOF to the helper, which terminates
                // on its own. Fall back to Kill if it doesn't exit promptly.
                try { proc.StandardInput.Close(); } catch { }
                if (!proc.WaitForExit(1500))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-helper] stop failed: {ex.Message}");
        }
        try { proc.Dispose(); } catch { }
    }

    /// <summary>
    /// Pokes the helper to refetch /displays/assignments and reconcile its
    /// kiosk windows. The stdin pipe is already open for parent-death EOF
    /// detection; a line on it is the push channel.
    /// </summary>
    public void NotifyDisplayAssignmentsChanged()
    {
        lock (_lock)
        {
            var proc = _process;
            if (proc is null || proc.HasExited) return;
            try
            {
                proc.StandardInput.WriteLine("assignments-changed");
                proc.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-helper] assignments poke failed: {ex.Message}");
            }
        }
    }

    /// <summary>Pushes an identify to the helper as one stdin line; the payload is single-line JSON.</summary>
    public void NotifyIdentify(string payloadJson)
    {
        lock (_lock)
        {
            var proc = _process;
            if (proc is null || proc.HasExited) return;
            try
            {
                proc.StandardInput.WriteLine("identify " + payloadJson);
                proc.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-helper] identify push failed: {ex.Message}");
            }
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_stopRequested) return;

        var since = DateTime.UtcNow - _lastSpawnUtc;
        if (since < TimeSpan.FromSeconds(30))
        {
            _consecutiveFailures++;
            if (_consecutiveFailures > 3)
            {
                Console.Error.WriteLine($"[overlay-helper] giving up after {_consecutiveFailures} rapid failures");
                return;
            }
        }
        else
        {
            _consecutiveFailures = 0;
        }
        Console.WriteLine($"[overlay-helper] exited (code {_process?.ExitCode}); restarting");
        _process = null;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(2000);
                if (_stopRequested) return;
                Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-helper] respawn failed: {ex.Message}");
            }
        });
    }

    internal static string? ResolveHelperPath()
    {
        // .app bundle layout: AppContext.BaseDirectory is
        // Nexus.app/Contents/MacOS/. Helpers live alongside the main
        // binary so TCC attributes any permission grants to the Nexus
        // bundle, not to a separate identity.
        var dir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, "nexus-overlay-helper");
    }
}
