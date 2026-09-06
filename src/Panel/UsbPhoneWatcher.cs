using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

public sealed class UsbPhoneWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    // Q-series model strings owned by QSeriesPortWatcher; skip those serials.
    private static readonly HashSet<string> QSeriesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "HYTE_Q60_Display",
        "HYTE_Q80_Display",
        "THICC_Q_Series",
    };

    // Common Android OEM USB vendor IDs used as a presence gate. This list covers major
    // OEMs; uncommon vendors pass through to false-negative (acceptable - the real
    // identity check is the adb model + panel package query).
    private static readonly int[] AndroidOemVendorIds =
    {
        0x04E8, // Samsung
        0x18D1, // Google / AOA accessory
        0x2717, // Xiaomi
        0x22D9, // OPPO / OnePlus
        0x22B8, // Motorola
        0x1004, // LG
        0x0FCE, // Sony
        0x12D1, // Huawei
        0x0BB4, // HTC
        0x0B05, // Asus
        // 0x0E8D (MediaTek) intentionally excluded: it is the Q-series panel's
        // own ADB-interface VID, so including it would make the presence gate
        // fire on a Q-series host with no phone attached. Q-series is owned by
        // QSeriesPortWatcher; a MediaTek-based phone still presents its OEM VID.
    };

    private const string PanelPackage = "com.hellonexus.panel";
    private const string PanelComponent = "com.hellonexus.panel/.MainActivity";
    // Device-side reverse remote: the Android app's fixed panel endpoints.
    private const int DeviceHttpPort = 9400;
    private const int DeviceHttpsPort = 9443;

    private readonly int _hostHttpPort;
    private readonly int _hostHttpsPort;
    private readonly AdbClient _client;
    private readonly HardwarePresence _presence;

    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reverseRefreshedThisRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _launchedThisRun = new(StringComparer.Ordinal);
    // Serials confirmed to have the panel app; skips the per-tick pm-list shell.
    private readonly HashSet<string> _panelAppConfirmedBySerial = new(StringComparer.Ordinal);

    // Each logged once per run to suppress repeated noise on a phone-less host.
    private bool _adbNotFoundLogged;
    private bool _serverUnavailableLogged;

    public UsbPhoneWatcher(int servicePort, HardwarePresence presence)
    {
        _hostHttpPort = servicePort;
        // Mirrors Program.cs httpsPort derivation.
        _hostHttpsPort = servicePort == 9400 ? 9443 : servicePort + 443;
        _presence = presence;
        _client = new AdbClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(0.5), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[usb-phone-watcher] tick failed: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Skip adb only when USB enumeration is populated AND contains no Android OEM VID.
        // An empty enumeration means the platform enumerator is unavailable (macOS
        // system_profiler returns [] with a phone connected); blocking on empty would
        // prevent the watcher from engaging on macOS entirely.
        var anyAndroidPresent = false;
        foreach (var vid in AndroidOemVendorIds)
        {
            if (_presence.UsbPresent(vid))
            {
                anyAndroidPresent = true;
                break;
            }
        }
        if (!anyAndroidPresent && _presence.AnyUsbEnumerated())
        {
            return;
        }

        IEnumerable<DeviceData> devices;
        try
        {
            devices = await _client.GetDevicesAsync(ct);
        }
        catch (SocketException) when (!ct.IsCancellationRequested)
        {
            _reverseAppliedBySerial.Clear();
            _reverseRefreshedThisRun.Clear();
            if (TryStartAdbServer())
            {
                try
                {
                    devices = await _client.GetDevicesAsync(ct);
                }
                catch
                {
                    // start-server returned but socket still refused - retry next tick.
                    if (!_serverUnavailableLogged)
                    {
                        _serverUnavailableLogged = true;
                        ServiceLog.Info("[usb-phone-watcher] adb-server unavailable after start attempt; will retry");
                    }
                    return;
                }
            }
            else
            {
                if (!_serverUnavailableLogged)
                {
                    _serverUnavailableLogged = true;
                    ServiceLog.Info("[usb-phone-watcher] adb-server not reachable and start-server failed; will retry when phone is present");
                }
                return;
            }
        }
        catch
        {
            _reverseAppliedBySerial.Clear();
            _reverseRefreshedThisRun.Clear();
            throw;
        }
        // A successful connection resets the once-flag so a later failure re-logs.
        _serverUnavailableLogged = false;

        var deviceList = devices.ToList();
        var seenSerials = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            // Not a USB phone: TCP transports (serial contains ':') and local
            // emulators (serial "emulator-NNNN").
            if (device.Serial.Contains(':', StringComparison.Ordinal)) continue;
            if (device.Serial.StartsWith("emulator-", StringComparison.Ordinal)) continue;
            // ArtInChip streamed-panel boards are owned by StreamedPanelCoordinator;
            // probing them here races the persistent stream shell over a USB link
            // that corrupts under concurrent adb ops.
            if (device.Serial.StartsWith("d211_", StringComparison.Ordinal)) continue;
            if (IsQSeriesModel(device.Model)) continue;

            if (!_panelAppConfirmedBySerial.Contains(device.Serial))
            {
                if (!await HasPanelAppAsync(device, ct)) continue;
                _panelAppConfirmedBySerial.Add(device.Serial);
            }

            seenSerials.Add(device.Serial);

            await EnsureReverseAsync(device, ct);
            await EnsureLaunchAsync(device, ct);
        }

        foreach (var key in _reverseAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseAppliedBySerial.Remove(key);
        }
        foreach (var key in _reverseRefreshedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseRefreshedThisRun.Remove(key);
        }
        foreach (var key in _launchedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _launchedThisRun.Remove(key);
        }
        foreach (var key in _panelAppConfirmedBySerial.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _panelAppConfirmedBySerial.Remove(key);
        }
    }

    private async Task<bool> HasPanelAppAsync(DeviceData device, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(device, $"pm list packages {PanelPackage}", receiver, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: pm list packages failed: {ex.GetType().Name}");
            return false;
        }
        return receiver.ToString().Contains(PanelPackage, StringComparison.Ordinal);
    }

    private async Task EnsureReverseAsync(DeviceData device, CancellationToken ct)
    {
        if (!_reverseRefreshedThisRun.Contains(device.Serial))
        {
            _reverseRefreshedThisRun.Add(device.Serial);
            _reverseAppliedBySerial.Remove(device.Serial);
            foreach (var devicePort in new[] { DeviceHttpPort, DeviceHttpsPort })
            {
                var remoteSpec = $"tcp:{devicePort}";
                try
                {
                    await _client.RemoveReverseForwardAsync(device, remoteSpec, ct);
                    ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: force-refreshed reverse on first sighting (removed {remoteSpec})");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: reverse pre-refresh remove no-op for {remoteSpec}: {ex.GetType().Name}");
                }
            }
        }

        // remote = device-side endpoint (Android app's fixed ports 9400/9443).
        // local = host-side endpoint (service's actual listen ports).
        var allApplied = true;
        foreach (var (devicePort, hostPort) in new[] { (DeviceHttpPort, _hostHttpPort), (DeviceHttpsPort, _hostHttpsPort) })
        {
            var remoteSpec = $"tcp:{devicePort}";
            var localSpec = $"tcp:{hostPort}";
            try
            {
                await _client.CreateReverseForwardAsync(device, remoteSpec, localSpec, true, ct);
            }
            catch (Exception ex)
            {
                allApplied = false;
                _reverseAppliedBySerial.Remove(device.Serial);
                ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: reverse apply failed for {remoteSpec}->{localSpec}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (allApplied && !_reverseAppliedBySerial.ContainsKey(device.Serial))
        {
            ServiceLog.Info($"[usb-phone-watcher] reverse applied: {device.Serial} ({device.Model}) tcp:{DeviceHttpPort}->{_hostHttpPort} + tcp:{DeviceHttpsPort}->{_hostHttpsPort}");
            _reverseAppliedBySerial[device.Serial] = true;
        }
    }

    private async Task EnsureLaunchAsync(DeviceData device, CancellationToken ct)
    {
        if (_launchedThisRun.Contains(device.Serial)) return;

        _launchedThisRun.Add(device.Serial);
        var receiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(
                device,
                // MAIN/LAUNCHER are spelled out because the wrapper now targets
                // Android 16 (API 36), whose safer-intents rules stop an
                // intent with no action from matching an exported component's
                // filters. Explicit -n still selects the component; the action
                // and category only make the match legal, and are a no-op on
                // older releases.
                $"am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER "
                    + $"-n {PanelComponent} --activity-single-top --ez nexus_usb true",
                receiver,
                ct);
            ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: launched panel app ({receiver.ToString().Trim()})");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _launchedThisRun.Remove(device.Serial);
            ServiceLog.Info($"[usb-phone-watcher] {device.Serial}: am start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryStartAdbServer()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            if (!_adbNotFoundLogged)
            {
                _adbNotFoundLogged = true;
                ServiceLog.Info("[usb-phone-watcher] adb not found in PATH or common SDK locations; cannot start adb-server");
            }
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "start-server",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(8_000))
            {
                try { p.Kill(true); } catch { }
                ServiceLog.Info("[usb-phone-watcher] adb start-server timed out after 8s");
                return false;
            }
            if (p.ExitCode != 0)
            {
                ServiceLog.Info($"[usb-phone-watcher] adb start-server exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            ServiceLog.Info("[usb-phone-watcher] adb-server (re)started");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[usb-phone-watcher] adb start-server threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsQSeriesModel(string? model)
    {
        if (model is null) return false;
        return QSeriesModels.Contains(model.Replace(" ", "_"));
    }
}
