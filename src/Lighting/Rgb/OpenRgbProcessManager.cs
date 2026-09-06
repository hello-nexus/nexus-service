using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Owns the OpenRGB-headless subprocess. Locates the binary in
/// <see cref="AppContext.BaseDirectory"/>/openrgb (where the publish output drops
/// the bundled headless build), and starts/stops it on demand.
///
/// Auto-restart with exponential backoff: 1s → 2s → 4s → 8s → 16s, capped at 30s.
/// Backoff resets after a process has been running for 60+ seconds.
///
/// AOT-safe - pure System.Diagnostics.Process, no reflection.
/// </summary>
public sealed class OpenRgbProcessManager : IDisposable
{
    public const int DefaultPort = 6742;

    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Machine-scope override raising what the daemon prints, for diagnosing
    /// drivers that report their negotiated wire parameters only at debug.</summary>
    internal const string VerbosityEnvVar = "NEXUS_OPENRGB_VERBOSITY";

    private const int VersionProbeTimeoutMs = 2000;

    private static int _daemonBuildLogged;

    private readonly object _lock = new();
    private readonly string _exePath;
    private readonly Nexus.Service.Persistence.IConfigStore? _store;
    private Process? _proc;
    private DateTime _startedUtc;
    private TimeSpan _backoff = InitialBackoff;
    private CancellationTokenSource? _supervisorCts;
    private bool _disposed;

    public OpenRgbProcessManager(string? overrideExePath = null, Nexus.Service.Persistence.IConfigStore? store = null)
    {
        _exePath = overrideExePath ?? ResolveDefaultPath();
        _store = store;
    }

    public bool IsAvailable => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && File.Exists(_exePath);

    /// <summary>
    /// Maps the override to the daemon's verbosity flag, or null to leave its output
    /// alone. Only these two flags raise what reaches stdout, the channel we relay;
    /// --loglevel sets the ceiling for the daemon's own logfile instead. An argument
    /// the daemon rejects makes it print help rather than serve, so unrecognized
    /// values must not reach the command line.
    /// </summary>
    internal static string? ResolveVerbosityFlag(string? configured) =>
        configured?.Trim().ToLowerInvariant() switch
        {
            "verbose" or "v" => "-v",
            "trace" or "debug" or "vv" => "-vv",
            _ => null,
        };

    /// <summary>
    /// Kill any OpenRGB-headless processes left behind by a crashed or force-killed
    /// service. Called on startup before spawning a new instance.
    /// </summary>
    public static void CleanupOrphans()
    {
        try
        {
            var name = OperatingSystem.IsWindows() ? "OpenRGB-headless" : "openrgb-headless";
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                    ServiceLog.Info($"[openrgb-proc] killed orphan PID {proc.Id}");
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _proc is { HasExited: false };
            }
        }
    }

    public string ExePath => _exePath;

    /// <summary>
    /// Resolved path: <c>{AppContext.BaseDirectory}/openrgb/&lt;binary&gt;</c>. The
    /// bundled binary name differs per platform - Windows ships
    /// <c>OpenRGB-headless.exe</c>, macOS <c>OpenRGB-headless</c>, and Linux the
    /// lowercase <c>openrgb-headless</c> (which is also what <see cref="CleanupOrphans"/>
    /// greps for off-Windows).
    /// </summary>
    public static string ResolveDefaultPath()
    {
        var name = OperatingSystem.IsWindows() ? "OpenRGB-headless.exe"
                 : OperatingSystem.IsLinux() ? "openrgb-headless"
                 : "OpenRGB-headless";
        return Path.Combine(AppContext.BaseDirectory, "openrgb", name);
    }

    /// <summary>
    /// Service-owned OpenRGB config directory, kept separate from any
    /// user-installed OpenRGB. <c>%ProgramData%\Nexus\openrgb-config</c> on
    /// Windows. On Linux and macOS <see cref="Environment.SpecialFolder.CommonApplicationData"/>
    /// resolves to <c>/usr/share</c>, which is root-owned (and read-only on
    /// immutable distros like Bazzite), so headless OpenRGB couldn't write its
    /// config there - use the per-user XDG config dir (Linux) / Application
    /// Support dir (macOS) instead.
    /// </summary>
    public static string ResolveConfigDir()
    {
        string baseDir;
        if (OperatingSystem.IsLinux())
        {
            baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            if (string.IsNullOrEmpty(baseDir))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                baseDir = string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, ".config");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // CommonApplicationData is /usr/share on macOS too (root-owned);
            // OpenRGB-headless can't write its config there, so use the per-user
            // Application Support dir.
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetTempPath();
        }
        else
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }
        }

        return Path.Combine(baseDir, "Nexus", "openrgb-config");
    }

    /// <summary>Test seam for the macOS split documented on
    /// <see cref="AlwaysDisabledDetectors"/>. Static initializers
    /// run in declaration order, so <see cref="DisabledDetectors"/> is declared
    /// after the array it reads.</summary>
    internal static string[] BuildDisabledDetectors(bool macOS)
    {
        var list = new System.Collections.Generic.List<string>(AlwaysDisabledDetectors);
        if (!macOS)
        {
            list.Insert(1, "HYTE Nexus");
        }
        return list.ToArray();
    }

    /// <summary>
    /// OpenRGB detectors nexus-service keeps disabled because it drives those
    /// devices directly over their own transport. The keeb rides raw HID, where
    /// the COM-port "first open wins" guard the serial hubs rely on does NOT
    /// apply - two stacks could hold the HID handle and fight over the LEDs - so
    /// the detector MUST be disabled here. Names match the
    /// <c>REGISTER_*_DETECTOR</c> strings in nexus-rgb/openrgb-headless verbatim
    /// (HYTEKeyboardControllerDetect.cpp -> "HYTE Keeb TKL";
    /// LianLiControllerDetect.cpp -> "Lian Li Uni Hub - SL Infinity";
    /// CorsairICueLinkControllerDetect.cpp -> "Corsair iCUE Link System Hub";
    /// NZXTHue2ControllerDetect.cpp -> "NZXT Kraken 2024 ELITE Series RGB").
    /// The Kraken is the same raw-HID case as the keeb: OpenRGB's Hue 2 controller
    /// opens the cooler's vendor HID interface, which is the one KrakenHub drives,
    /// and the two stacks then interleave writes on the same pipe. OpenRGB also
    /// reads its fan chain as zero LEDs (its accessory table has no 0x1B entry),
    /// so nothing is lost by keeping it off.
    /// "HYTE Nexus" (HYTENexusControllerDetect.cpp) covers exactly the THICC Q60
    /// and the Nexus Portal NP50. On Windows and Linux both are driven natively
    /// over CDC serial: on Windows the native workers open the COM port at
    /// startup and OpenRGB's later open fails, but a Linux tty has no such
    /// guard, so both stacks wrote frames to the same port and a static color
    /// flashed whenever the two interleaved. macOS has no native NP50 or
    /// Q-series discovery, so the detector stays enabled there.
    /// Unknown names in this list are ignored, so disabling the iCUE Link detector
    /// is a safe no-op on bundled builds that predate that controller.
    ///
    /// "HID LampArray Device" is disabled for a different reason: it is a generic
    /// detector matching the standard LampArray HID usage on any vendor, so a
    /// board that exposes both a vendor protocol and a LampArray collection is
    /// detected twice - once by its real controller with full zones, and once as
    /// a near-empty duplicate on the other HID interface. Upstream registers it
    /// unconditionally with no check for an existing controller, so the dedupe
    /// has to happen here.
    /// </summary>
    private static readonly string[] AlwaysDisabledDetectors = {
        "HYTE Keeb TKL", "Lian Li Uni Hub - SL Infinity", "Corsair iCUE Link System Hub",
        "NZXT Kraken 2024 ELITE Series RGB", "HID LampArray Device",
        // Nollie controllers are driven natively; names match the
        // REGISTER_HID_DETECTOR strings in openrgb-headless.
        "Nollie 32CH", "Nollie 16CH", "Nollie 8CH", "Nollie 1CH", "Nollie 28 12", "Nollie 28 L1",
        "Nollie 28 L2", "Nollie 32_OS2", "Nollie 16_OS2", "Nollie 8_OS2", "Nollie 1_OS2",
        "Nollie 32_OS2_1", "Nollie 16_OS2_1", "Nollie 8_OS2_1", "Prism8 8_OS2_1", "Nollie 1_OS2_1",
    };

    private static readonly string[] DisabledDetectors = BuildDisabledDetectors(OperatingSystem.IsMacOS());

    /// <summary>
    /// Detector names the user excluded by turning Nexus Control off for every
    /// card of the device. Written as disabled + <c>placeholder_only</c> so the
    /// fork reports a zero-LED presence dummy instead of claiming the hardware.
    /// Null when settings could not be read - the caller then leaves the
    /// on-disk placeholder state untouched rather than re-enabling detectors
    /// whose exclusions still exist.
    /// </summary>
    private System.Collections.Generic.IReadOnlyCollection<string>? ResolvePlaceholderDetectors()
    {
        if (_store is null)
        {
            return Array.Empty<string>();
        }
        try
        {
            var exclusions = _store.Load().Devices.OpenRgbDetectorExclusions;
            if (exclusions.Count == 0)
            {
                return Array.Empty<string>();
            }
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (var kv in exclusions)
            {
                if (!string.IsNullOrEmpty(kv.Value.DetectorName))
                {
                    names.Add(kv.Value.DetectorName);
                }
            }
            return names;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merge our detector denylist into the OpenRGB config's
    /// <c>Detectors.detectors</c> map, preserving anything OpenRGB itself wrote.
    /// OpenRGB reads this on startup (ResourceManager.cpp) and skips disabled
    /// detectors. User exclusions additionally land in the service-owned
    /// <c>Detectors.placeholder_only</c> array (the fork registers a zero-LED
    /// presence dummy for those); names dropped from that array since the last
    /// launch get their detector re-enabled. Best-effort: a failure here just
    /// means OpenRGB might surface a zombie entry, which
    /// CompositeLightingDeviceProvider also strips.
    /// </summary>
    internal static void EnsureDetectorOverrides(string configDir, System.Collections.Generic.IReadOnlyCollection<string>? placeholderOnlyDetectors)
    {
        try
        {
            var path = Path.Combine(configDir, "OpenRGB.json");
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(path))
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            if (root["Detectors"] is not System.Text.Json.Nodes.JsonObject detectors)
            {
                detectors = new System.Text.Json.Nodes.JsonObject();
                root["Detectors"] = detectors;
            }
            if (detectors["detectors"] is not System.Text.Json.Nodes.JsonObject map)
            {
                map = new System.Text.Json.Nodes.JsonObject();
                detectors["detectors"] = map;
            }

            var changed = false;
            foreach (var name in DisabledDetectors)
            {
                var alreadyDisabled = map[name] is System.Text.Json.Nodes.JsonValue v
                    && v.TryGetValue<bool>(out var b) && b == false;
                if (!alreadyDisabled)
                {
                    map[name] = false;
                    changed = true;
                }
            }

            // placeholder_only is wholly service-owned: any name present last
            // launch but gone now was un-ignored, so its detector re-enables.
            // Skipped entirely (state left as-is) when the exclusion list could
            // not be read, so a settings hiccup never re-enables detectors
            // whose exclusions still exist.
            if (placeholderOnlyDetectors is not null)
            {
                var previous = new System.Collections.Generic.List<string>();
                if (detectors["placeholder_only"] is System.Text.Json.Nodes.JsonArray prevArr)
                {
                    foreach (var node in prevArr)
                    {
                        if (node is System.Text.Json.Nodes.JsonValue pv && pv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                        {
                            previous.Add(s);
                        }
                    }
                }
                var desired = new System.Collections.Generic.HashSet<string>(placeholderOnlyDetectors, StringComparer.Ordinal);
                foreach (var name in previous)
                {
                    if (!desired.Contains(name) && Array.IndexOf(DisabledDetectors, name) < 0)
                    {
                        map[name] = true;
                        changed = true;
                    }
                }
                foreach (var name in placeholderOnlyDetectors)
                {
                    var alreadyDisabled = map[name] is System.Text.Json.Nodes.JsonValue v
                        && v.TryGetValue<bool>(out var b) && b == false;
                    if (!alreadyDisabled)
                    {
                        map[name] = false;
                        changed = true;
                    }
                }
                if (!desired.SetEquals(previous))
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var name in placeholderOnlyDetectors)
                    {
                        arr.Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(name));
                    }
                    detectors["placeholder_only"] = arr;
                    changed = true;
                }
            }

            if (changed)
            {
                File.WriteAllText(path, root.ToJsonString());
                ServiceLog.Info($"[openrgb-proc] detector overrides written ({DisabledDetectors.Length} first-party, {(placeholderOnlyDetectors is null ? "unchanged" : placeholderOnlyDetectors.Count.ToString())} placeholder-only) in {path}");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[openrgb-proc] detector-override write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Log which bundled daemon build is running, once per service lifetime. The
    /// daemon writes its commit id only to its own logfile, so a service log
    /// otherwise cannot say which fork build produced a device list. --version
    /// prints and exits, so this costs one short-lived process at first start.
    /// </summary>
    private static void LogDaemonBuildOnce(string exePath)
    {
        if (Interlocked.Exchange(ref _daemonBuildLogged, 1) != 0)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return;
            }

            // Read before waiting: a full pipe buffer would deadlock the exit.
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(VersionProbeTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ServiceLog.Info("[openrgb-proc] daemon version probe timed out");
                return;
            }

            ServiceLog.Info(
                $"[openrgb-proc] bundled daemon {ParseVersionField(stdout, "Version:")}"
                + $" commit {ParseVersionField(stdout, "Git Commit ID")}"
                + $" branch {ParseVersionField(stdout, "Git Branch")}");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[openrgb-proc] daemon version probe failed: {ex.Message}");
        }
    }

    /// <summary>Pull one tab-padded field out of the daemon's --version banner.</summary>
    internal static string ParseVersionField(string output, string label)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(label, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[label.Length..].Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "unknown";
    }

    // Restore the executable bit on the bundled binary (Content copy / archive
    // round-trips drop it on Unix). No-op on Windows / if the file is missing.
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* best effort - launch will surface the real error */ }
    }

    /// <summary>
    /// Start the subprocess if it isn't already running. Idempotent. Safe to
    /// call from any thread.
    /// </summary>
    public void Start()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_proc is { HasExited: false })
            {
                return;
            }

            CleanupProcessLocked();
            CleanupOrphans();

            // Use a service-owned config directory so we don't collide with any
            // user-installed OpenRGB. Lives under %ProgramData%\Nexus on Windows.
            var configDir = ResolveConfigDir();
            try
            { Directory.CreateDirectory(configDir); }
            catch { /* will fail loudly when OpenRGB itself tries */ }

            // Keep OpenRGB from claiming devices nexus-service drives directly
            // or the user excluded. Re-asserted on every (re)launch so a
            // supervisor restart can't run an instance that re-grabs them.
            EnsureDetectorOverrides(configDir, ResolvePlaceholderDetectors());

            // Registrations for hardware no detector can match on its own (QMK
            // boards, E1.31 devices). Re-asserted per launch for the same
            // reason as the overrides above.
            if (_store is not null)
            {
                try { OpenRgbManualDeviceConfig.Write(configDir, _store.Load().Devices.OpenRgbManualDevices); }
                catch (Exception ex) { ServiceLog.Warn($"[openrgb-proc] manual-device write skipped: {ex.GetType().Name}: {ex.Message}"); }
            }

            // The MSBuild Content copy (and tar/zip round-trips) drop the
            // executable bit on Linux/macOS - restore it or Process.Start fails
            // with EACCES and RGB silently never comes up.
            EnsureExecutable(_exePath);
            LogDaemonBuildOnce(_exePath);

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--server-port");
            psi.ArgumentList.Add(DefaultPort.ToString());
            psi.ArgumentList.Add("--noautoconnect");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(configDir);
            psi.ArgumentList.Add("--loglevel");
            psi.ArgumentList.Add("error");

            var verbosity = ResolveVerbosityFlag(Environment.GetEnvironmentVariable(VerbosityEnvVar));
            if (verbosity is not null)
            {
                ServiceLog.Info($"[openrgb-proc] {VerbosityEnvVar} set: relaying daemon output at '{verbosity}'");
                psi.ArgumentList.Add(verbosity);
            }

            try
            {
                var proc = Process.Start(psi);
                if (proc is null)
                {
                    return;
                }

                _proc = proc;
                _startedUtc = DateTime.UtcNow;

#if WINDOWS
                // Tie the headless server's lifetime to ours: an abrupt Nexus.exe
                // exit reaps it via the kill-job instead of orphaning it (it would
                // otherwise keep openrgb\*.dll locked against an OTA file swap).
                Nexus.Service.Lifecycle.ChildProcessJob.Assign(proc);
#endif

                // Drain stdout/stderr so the OS pipe buffers don't fill up
                _ = Task.Run(() => DrainStreamAsync(proc.StandardOutput, "stdout"));
                _ = Task.Run(() => DrainStreamAsync(proc.StandardError, "stderr"));

                // Replace the previous CTS so Stop() can cancel only the current
                // supervisor - and dispose the old one to avoid leaks.
                var oldCts = _supervisorCts;
                _supervisorCts = new CancellationTokenSource();
                try
                { oldCts?.Dispose(); }
                catch { }

                var token = _supervisorCts.Token;
                _ = Task.Run(() => SupervisorAsync(proc, token));
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[openrgb-proc] failed to launch {_exePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stop the subprocess. Idempotent. Sends a kill (no graceful shutdown signal;
    /// the headless server holds no on-disk state, so kill is safe).
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _supervisorCts;
            _supervisorCts = null;
            CleanupProcessLocked();
            _backoff = InitialBackoff;
        }
        try
        { cts?.Cancel(); }
        catch { }
        try
        { cts?.Dispose(); }
        catch { }
    }

    private void CleanupProcessLocked()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(500);
            }
        }
        catch { /* swallow - best effort */ }
        try
        { _proc?.Dispose(); }
        catch { }
        _proc = null;
    }

    private async Task SupervisorAsync(Process proc, CancellationToken ct)
    {
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        var uptime = DateTime.UtcNow - _startedUtc;
        ServiceLog.Warn($"[openrgb-proc] subprocess exited code={proc.ExitCode} after {uptime.TotalSeconds:F0}s");

        // Reset backoff if it lived long enough to be considered "stable"
        TimeSpan backoff;
        lock (_lock)
        {
            if (uptime > StableUptime)
            {
                _backoff = InitialBackoff;
            }

            backoff = _backoff;
            // Bump for next time
            _backoff = TimeSpan.FromMilliseconds(Math.Min(_backoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));
        }

        try
        { await Task.Delay(backoff, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Re-check disposed AND cancellation under the lock so we don't race with Stop().
        // Start() itself re-checks, but bailing here saves a process spawn we'd just kill.
        bool shouldRestart;
        lock (_lock)
        {
            shouldRestart = !_disposed && !ct.IsCancellationRequested;
        }
        if (shouldRestart)
        {
            Start();
        }
    }

    private static async Task DrainStreamAsync(StreamReader reader, string label)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (IsKnownNoise(line))
                {
                    continue;
                }

                // Relay the child's own output: its stdout is informational, its
                // stderr a warning. Neither is a Nexus failure.
                if (label == "stderr")
                    ServiceLog.Warn($"[openrgb-proc/{label}] {line}");
                else
                    ServiceLog.Info($"[openrgb-proc/{label}] {line}");
            }
        }
        catch { /* pipe closed on shutdown */ }
    }

    /// <summary>
    /// Suppress upstream OpenRGB log lines that are expected on every run and
    /// don't indicate a real problem.
    /// </summary>
    private static bool IsKnownNoise(string line)
    {
        // Client disconnect - fires every time the bridge closes the TCP socket.
        if (line.Contains("recv_select failed receiving magic"))
        {
            return true;
        }
        // Optional sound card detector that always fails on machines without an AE-5.
        if (line.Contains("[Creative SoundBlaster AE-5]"))
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
