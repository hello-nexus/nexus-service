using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.QSeries;

/// <summary>
/// Keeps <c>adb reverse tcp:{servicePort} tcp:{tunnelPort}</c> alive on an
/// attached HYTE Q60 / Q80 panel so its Android shell
/// (<c>com.hellonexus.qshell</c>) can load the Nexus panel SPA from
/// <c>http://localhost:{servicePort}</c>. The host-side target is the dedicated
/// panel tunnel listener (<see cref="Nexus.Service.Panel.PanelTunnelMonitor"/>)
/// when it bound, so inbound activity there attributably proves the physical
/// panel is alive; the device-side port the panel connects to never changes.
/// The reverse is owned by the host adb-server; if that server dies the
/// reverse evaporates and the panel WebSocket goes silent.
///
/// USB-FFS adbd is fragile: cycling the host adb-server mid-stream can wedge the
/// device daemon into <c>offline</c>, which can't be cleared from the host without
/// root. Recovery: per-tick <c>pnputil /restart-device</c> for a device stuck
/// offline (a USB-level reset that restarts device-side adbd), plus an optional
/// promote to adb-over-TCP when the panel has a LAN IP (dormant on stock touch-less
/// units that can't enter WiFi creds), persisted to
/// <c>&lt;data-root&gt;/Nexus/devices/transports/qseries-transports.json</c>.
///
/// Every pass is idempotent: an already-applied reverse / already-promoted device
/// is left alone, and adb-server-down / device-detached turn into no-ops that
/// recover on a later tick.
/// </summary>
public sealed class QSeriesPortWatcher : BackgroundService
{
    /// <summary>
    /// Keep-alive cadence. 10 s is shorter than the panel's reconnect backoff cap
    /// (30 s) so a dropped reverse is re-applied before a user-visible stall.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>ro.product.model</c> values that mark a Q-series panel. THICC_Q_Series is
    /// a legacy pre-release model name, kept so a firmware downgrade still matches.
    /// </summary>
    private static readonly HashSet<string> QSeriesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "HYTE_Q60_Display",
        "HYTE_Q80_Display",
        "THICC_Q_Series",
    };

    /// <summary>
    /// MediaTek USB vendor id - the Q60/Q80 panel's SoC, seen when the Android
    /// panel is in adb mode. Generic to MediaTek, so it's a presence hint used
    /// alongside the Q-series cooler's own VID/PID, never an identity proof.
    /// </summary>
    private const int MediaTekAdbVendorId = 0x0E8D;

    /// <summary>
    /// How long a Q-series serial must stay adb-<c>offline</c> before a USB reset.
    /// Longer than the natural ~5–15 s offline blip while adb-server re-handshakes,
    /// so a recovery already underway isn't churned.
    /// </summary>
    private static readonly TimeSpan OfflineRecoveryThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum gap between USB resets for one instance - stops a physically-dead
    /// device from being reset every tick. 2 min lets one reset's recovery complete.
    /// </summary>
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    private readonly int _servicePort;
    /// <summary>Device-side reverse spec - the port qshell connects to on the
    /// panel's loopback. Never changes: qshell hardcodes it.</summary>
    private readonly string _deviceSpec;
    /// <summary>Host-side reverse target: the tunnel listener when it bound,
    /// else the main service port (legacy mapping).</summary>
    private readonly string _hostSpec;
    private readonly AdbClient _client;
    private readonly QSeriesTransportStore _transportStore;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly IConfigStore _configStore;

    /// <summary>
    /// Tunnel liveness monitor; when active, its inbound-activity timestamp is
    /// the escalation gate's liveness signal. Null in tests / when the tunnel
    /// port failed to bind - the record-based legacy gate applies then.
    /// </summary>
    private readonly Nexus.Service.Panel.PanelTunnelMonitor? _tunnelMonitor;

    /// <summary>
    /// Panel registry, read-only here, to check whether the Q-series panel has
    /// re-contacted the service (its record's <c>LastSeenAt</c>) after a host
    /// restart - the legacy liveness gate for the escalation reboot, used only
    /// when <see cref="_tunnelMonitor"/> is inactive. Any local client's GET of
    /// the device record bumps <c>LastSeenAt</c>, so this signal cannot tell
    /// the physical panel from an open desktop dashboard.
    /// </summary>
    private readonly PanelDeviceRegistry _panelDevices;

    private readonly IAdbDeviceRegistry? _deviceRegistry;

    // Registry is keyed by package name, so one panel per host is tracked; null when unregistered.
    private string? _registeredSerial;

    /// <summary>Serials with an applied reverse, so the steady-state path stays quiet.</summary>
    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials whose reverse was force-refreshed (remove + re-add) this run. The
    /// first tick after a (re)start tears the reverse down and re-adds it to clear a
    /// soft wedge - an entry <c>adb reverse --list</c> still shows but that passes no
    /// traffic; later ticks keep the quiet rebind. Cleared on adb-server death and
    /// on detach.
    /// </summary>
    private readonly HashSet<string> _reverseRefreshedThisRun = new(StringComparer.Ordinal);

    /// <summary>Serials we've already logged "no LAN IP" for, so a USB-only unit
    /// doesn't repeat that line every tick. Cleared on adb-server death.</summary>
    private readonly HashSet<string> _lanIpUnavailableLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// In-memory mirror of the on-disk transport store (USB serial -> promoted TCP
    /// transport). Mutated in place, never reassigned, so readonly.
    /// </summary>
    private readonly Dictionary<string, QSeriesTransportRecord> _promoted;

    /// <summary>
    /// Serials ever seen as Q-series. An <c>offline</c> entry's model field is
    /// unreliable, so re-identify by membership here. Cleared only on unplug.
    /// </summary>
    private readonly HashSet<string> _knownQSeriesSerials = new(StringComparer.Ordinal);

    /// <summary>
    /// First time a serial was seen <c>offline</c> this run. Cleared when it returns
    /// online or leaves the adb list.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _offlineSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Offline serials whose USB instance proved non-MediaTek (a phone, not the
    /// panel), excluded from offline recovery so the per-tick pass never USB-resets
    /// someone's phone. Cleared on detach so a replug reclassifies.
    /// </summary>
    private readonly HashSet<string> _offlineNonQSeriesSerials = new(StringComparer.Ordinal);

    /// <summary>Serial -> last USB instance-id lookup time; the lookup spawns
    /// powershell, so it runs on the recovery-threshold cadence, not per tick.
    /// Cleared when the serial returns online or detaches.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastInstanceIdLookupBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Signature last logged while Q-series hardware was present with no online
    /// Q-series visible to adb; null while one is online. Gates the diagnostic to
    /// one line per state change instead of per tick.
    /// </summary>
    private string? _adbVisibilitySignature;

    /// <summary>
    /// Last <c>pnputil /restart-device</c> time per USB instance id (the granularity
    /// pnputil acts on). A different replugged device gets its own cooldown.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastRecoveryByInstanceId = new(StringComparer.Ordinal);

    public QSeriesPortWatcher(int servicePort, HardwarePresence presence, PanelDeviceRegistry panelDevices, DeviceControlGate gate, IConfigStore configStore, IAdbDeviceRegistry? deviceRegistry = null, Nexus.Service.Panel.PanelTunnelMonitor? tunnelMonitor = null)
        : this(servicePort, presence, panelDevices, gate, configStore, new QSeriesTransportStore(), deviceRegistry, tunnelMonitor) { }

    /// <summary>Test seam: inject a store pointing at a tmp path.</summary>
    public QSeriesPortWatcher(int servicePort, HardwarePresence presence, PanelDeviceRegistry panelDevices, DeviceControlGate gate, IConfigStore configStore, QSeriesTransportStore transportStore, IAdbDeviceRegistry? deviceRegistry = null, Nexus.Service.Panel.PanelTunnelMonitor? tunnelMonitor = null)
    {
        _servicePort = servicePort;
        _presence = presence;
        _panelDevices = panelDevices;
        _gate = gate;
        _configStore = configStore;
        _deviceRegistry = deviceRegistry;
        _tunnelMonitor = tunnelMonitor;
        _deviceSpec = $"tcp:{servicePort}";
        _hostSpec = tunnelMonitor?.Port is int tunnelPort ? $"tcp:{tunnelPort}" : $"tcp:{servicePort}";
        _client = new AdbClient();
        _transportStore = transportStore;
        _promoted = _transportStore.Load();
    }

#if WINDOWS
    /// <summary>Guards the Unsubscribe in StopAsync against a failed Subscribe.</summary>
    private bool _powerEventsSubscribed;
#endif

    public override Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _powerEventsSubscribed = true;
            }
            catch (Exception ex)
            {
                ServiceLog.Info($"[qseries-port-watcher] failed to subscribe to power events: {ex.GetType().Name}: {ex.Message}");
            }
        }
#endif
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the stack finish boot before poking the adb-server (the
        // host may be booting the bundled adb.exe alongside us).
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
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
                // The clean-host idle case (no Q-series ⇒ no adb-server) is gated out
                // in TickAsync before any adb call, so reaching here means a Q-series
                // is present but its adb path genuinely failed - a real error.
                ServiceLog.Error($"[qseries-port-watcher] tick failed: {ex.GetType().Name}: {ex.Message}");
            }

            // The timeout carries the keep-alive cadence for work that has no
            // signal; a wake returns early so a setting change applies now.
            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Panel counted as connected at OS shutdown when the tunnel saw inbound
    /// bytes this recently. Comfortably above the WebSocket keepalive interval,
    /// so a live socket always qualifies.
    /// </summary>
    private static readonly TimeSpan ShutdownRebootSilence = TimeSpan.FromSeconds(90);

    /// <summary>Construction time; anchors the shutdown-reboot silence check
    /// when the tunnel saw no bytes at all this run.</summary>
    private readonly DateTimeOffset _runStartedAt = DateTimeOffset.UtcNow;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (_powerEventsSubscribed)
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
            catch { }
            _powerEventsSubscribed = false;
        }
#endif
        if (Lifecycle.HostShutdown.IsOsShutdown)
        {
            // Best-effort: runs before base.StopAsync cancels the tick loop
            // (awaiting an in-flight tick could eat the SCM shutdown allowance),
            // so it races the tick's collection mutations on another thread and
            // any failure is swallowed rather than aborting shutdown handling.
            // SystemEvents has no shutdown-specific mode (Suspend/Resume only),
            // so the sleep-with-host coupling needs this separate hook here.
            try { TrySleepPanelsForHostPowerDown(); }
            catch { }
            try { TryRebootStrandedPanelsForShutdown(); }
            catch { }
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// A panel already stranded at OS shutdown (splash-latched or adbd-wedged)
    /// stays stranded across the host restart, because the wedge survives host
    /// reboots; rebooting it now makes host and panel cold-boot in parallel and
    /// the per-attach pass (reverse, am start, HOME pin) picks it up when both
    /// return. A panel with recent tunnel activity is left alone: its qshell
    /// keeps the WebView mounted across the restart and reconnects in seconds,
    /// where a reboot would cost the full cold bootstrap on every OS restart.
    /// Without the tunnel signal (legacy mapping) stranded and healthy are
    /// indistinguishable, so nothing is rebooted. The signal is host-global,
    /// not per-serial, so on a multi-panel host one live panel shields a
    /// stranded sibling; single-panel installs are the field norm. Budget: OS
    /// shutdown allows ~5s total (WaitToKillServiceTimeout), so each reboot
    /// call is capped short.
    /// </summary>
    private void TryRebootStrandedPanelsForShutdown()
    {
        if (_knownQSeriesSerials.Count == 0) return;
        if (_tunnelMonitor?.IsActive != true) return;

        // No bytes at all this run anchors on run start: a shutdown moments
        // after service start must not reboot a panel whose reconnect is still
        // in flight (Windows Update restart chains).
        var last = _tunnelMonitor.LastInboundActivityUnixMs;
        var referenceMs = last > 0 ? last : _runStartedAt.ToUnixTimeMilliseconds();
        var silenceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - referenceMs;
        if (silenceMs < ShutdownRebootSilence.TotalMilliseconds) return;

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null) return;
        foreach (var serial in _knownQSeriesSerials.ToArray())
        {
            if (QSeriesTransport.IsTcpSerial(serial)) continue;
            var ok = RunAdb(adbPath, $"-s {serial} reboot", out var err, timeoutMs: 1_500);
            ServiceLog.Info(ok
                ? $"[qseries-port-watcher] {serial}: panel silent at OS shutdown; rebooting it so host and panel cold-boot in parallel"
                : $"[qseries-port-watcher] {serial}: shutdown reboot failed: {err}");
        }
    }

    /// <summary>KEYCODE_SLEEP: forces the display asleep, unlike KEYCODE_POWER
    /// (26) which toggles and so can't be issued idempotently on every
    /// attach/resume.</summary>
    private const int KeyeventSleep = 223;

    /// <summary>KEYCODE_WAKEUP: forces the display awake.</summary>
    private const int KeyeventWakeup = 224;

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Suspend runs inline: the machine stops once every subscriber returns,
        // so handing off to the thread pool races the suspend and usually loses
        // the keyevent. SendKeyeventBestEffort is capped at 1.5 s per serial and
        // swallows its own failures, which bounds what this holds the shared
        // SystemEvents pump thread for. Resume has no such deadline, so it hands
        // off (the shape PowerEventListener uses for its resume-only handler).
        if (e.Mode == PowerModes.Suspend)
        {
            TrySleepPanelsForHostPowerDown();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _ = Task.Run(TryRestorePanelsForHostResume);
        }
    }
#endif

    /// <summary>
    /// Called inline on Windows suspend, and at OS shutdown from StopAsync
    /// (SystemEvents has no shutdown mode of its own). Sends the sleep keyevent
    /// to every known Q-series serial when SleepWithHost is on; a resume without
    /// a USB reseat is restored by <see cref="TryRestorePanelsForHostResume"/>,
    /// and a reseat is instead picked up by the per-attach reassert once the
    /// device re-enumerates.
    /// </summary>
    private void TrySleepPanelsForHostPowerDown()
    {
        try
        {
            if (_knownQSeriesSerials.Count == 0) return;
            if (!_configStore.Load().QSeries.SleepWithHost) return;
            Interlocked.Exchange(ref _displayRecordStale, 1);
            foreach (var serial in _knownQSeriesSerials.ToArray())
            {
                // One panel is known by both its USB serial and, once promoted,
                // <ip>:5555. The TCP leg dials the LAN while the host's network
                // stack is going down, so it burns the full timeout for a
                // duplicate of what the USB leg already sent.
                if (QSeriesTransport.IsTcpSerial(serial)) continue;
                SendKeyeventBestEffort(serial, KeyeventSleep);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[qseries-port-watcher] sleep-on-suspend failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Re-applies the stored screen state on resume: wakes unless the
    /// user had the screen off already.</summary>
    private void TryRestorePanelsForHostResume()
    {
        try
        {
            // Resume races the USB stack re-enumerating, so this one-shot can
            // miss. Marking the record stale first, then announcing, hands the
            // state to the tick loop's readback-verified retry, which is what
            // actually guarantees it lands.
            Interlocked.Exchange(ref _displayRecordStale, 1);
            AnnounceDisplayChange();

            if (_knownQSeriesSerials.Count == 0) return;
            var qseries = _configStore.Load().QSeries;
            if (!qseries.SleepWithHost) return;
            var keycode = qseries.ScreenOff ? KeyeventSleep : KeyeventWakeup;
            foreach (var serial in _knownQSeriesSerials.ToArray())
            {
                if (QSeriesTransport.IsTcpSerial(serial)) continue;
                SendKeyeventBestEffort(serial, keycode);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[qseries-port-watcher] restore-on-resume failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Bypasses the tick thread that owns the transport: the host is suspending
    /// or shutting down, so waiting for the next tick misses the window. The
    /// resulting interleave with an in-flight tick is tolerable for one small
    /// shell command; the USB-FFS wedges on record come from bulk transfers
    /// (screencap, APK installs). The timeout shares the OS shutdown allowance
    /// (WaitToKillServiceTimeout) with TryRebootStrandedPanelsForShutdown, so
    /// both must fit. A bare serial routes by serial through the adb-server
    /// rather than a transport id captured on an earlier tick.
    /// </summary>
    private void SendKeyeventBestEffort(string serial, int keycode)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1_500));
            var device = new DeviceData { Serial = serial };
            _client.ExecuteShellCommandAsync(device, $"input keyevent {keycode}", cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[qseries-port-watcher] {serial}: keyevent {keycode} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // A power hook drove the screen from the SystemEvents thread, so the
        // record no longer matches the panel and the diff below would skip the
        // very command needed to undo it.
        if (Interlocked.Exchange(ref _displayRecordStale, 0) == 1)
        {
            _lastAppliedBySerial.Clear();
        }

        // Cross-thread signal from POST /qseries/rotation or /qseries/display:
        // force every attached serial to re-apply this tick instead of waiting
        // for a re-attach. The retry deadline resets with the latch, or a serial
        // that already exhausted its window would re-latch after one retry.
        if (Interlocked.Exchange(ref _displayDirty, 0) == 1)
        {
            _displayAppliedThisRun.Clear();
            _displayFirstFailureBySerial.Clear();
        }

        // Nexus Control off skips the whole pass: no reverse-tunnel or am-start
        // churn. Existing reverse forwards are left alone rather than torn down,
        // since a proactive adb teardown call risks the USB-FFS wedge documented
        // for this device class.
        if (!_gate.IsEnabled("qseries"))
        {
            return;
        }

        // Don't probe adb unless a Q-series unit is plausibly attached: its cooler
        // (VID_3402&PID_0400/0403) or the panel's MediaTek adb interface (VID_0E8D)
        // on USB, or a TCP-promoted device we still maintain. Otherwise
        // GetDevicesAsync spawns and polls a dead adb-server every tick on every
        // host that has no Q-series at all.
        if (_promoted.Count == 0
            && !_presence.UsbPresent(QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.Q60ProductId, QSeriesCoolerProtocol.Q80ProductId)
            && !_presence.UsbPresent(MediaTekAdbVendorId))
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
            // adb-server is dead. AdvancedSharpAdbClient talks to an existing server
            // over TCP and won't spawn one; shell out to `adb start-server` and retry
            // once (a still-failing start is picked up next tick).
            _reverseAppliedBySerial.Clear();
            _reverseRefreshedThisRun.Clear();
            _lanIpUnavailableLogged.Clear();
            if (TryStartAdbServer())
            {
                try
                {
                    devices = await _client.GetDevicesAsync(ct);
                }
                catch
                {
                    // start-server returned but the socket still refused - retry next tick.
                    throw;
                }
            }
            else
            {
                throw;
            }
        }
        catch
        {
            _reverseAppliedBySerial.Clear();
            _reverseRefreshedThisRun.Clear();
            _lanIpUnavailableLogged.Clear();
            throw;
        }

        var deviceList = devices.ToList();

        // Recovery pass first so a successful USB-reset's re-enumeration is visible
        // to the reverse-port pass below.
        await TryRecoverOfflineQSeriesDevicesAsync(deviceList, ct);

        // One-shot `adb connect` per persisted promotion not already in the device
        // list. Cheap, and self-heals a transient TCP drop without a USB attach.
        if (_promoted.Count > 0)
        {
            await ReconnectMissingTransportsAsync(deviceList, ct);
            deviceList = (await _client.GetDevicesAsync(ct)).ToList();
        }

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);

        // Promote any not-yet-promoted online USB Q-series to TCP before applying the
        // reverse, so it lands on the transport that survives USB hiccups.
        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;
            // Remember the serial so a later offline pass can identify it without the
            // (then-unreliable) model field.
            _knownQSeriesSerials.Add(device.Serial);
            if (QSeriesTransport.IsTcpSerial(device.Serial)) continue;
            if (_promoted.ContainsKey(device.Serial)) continue;
            await TryPromoteToTcpAsync(device, ct);
        }

        // A just-promoted device's TCP transport appears on the next tick; no
        // re-fetch here.

        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;

            seenSerials.Add(device.Serial);

            // An in-flight APK install owns the USB-FFS transport. Running the
            // per-tick adb passes (reverse, am start, clock, reboot) concurrently
            // races the install stream and fails it; skip them until it finishes.
            if (_deviceRegistry?.TryGet(QshellPackage)?.InstallInProgress == true) continue;

            // A reseat re-enumerates with the same serial but a new transport id. The
            // 10 s poll often misses the brief offline window, so a transport-id change
            // is the reliable reseat signal: on change, reboot to reset the degraded
            // USB-FFS link (re-launching qshell alone doesn't clear it).
            var transportId = device.TransportId;
            if (!string.IsNullOrEmpty(transportId))
            {
                var reseated = _transportIdBySerial.TryGetValue(device.Serial, out var lastTransportId)
                    && lastTransportId != transportId;
                _transportIdBySerial[device.Serial] = transportId;
                if (reseated)
                {
                    // The panel re-enumerated, so it may have rebooted back to
                    // user_rotation 0 without a tick ever seeing it offline. The
                    // confirmed-state record cannot be trusted across that.
                    _lastAppliedBySerial.Remove(device.Serial);
                    _displayAppliedThisRun.Remove(device.Serial);
                }
                if (reseated && await TryRebootOnReseatAsync(device, lastTransportId!, transportId, ct))
                    continue; // device is rebooting; skip the reverse/qshell passes this tick
            }

            await EnsureReverseAsync(device, ct);
            await EnsureQshellForegroundAsync(device, ct);
            await ReassertPanelDisplayAsync(device, ct);
            RegisterDeviceTarget(device.Serial);
            await SyncDeviceClockAsync(device, ct);
            await TryEscalateRebootAsync(device, ct);
        }

        LogAdbVisibility(deviceList, seenSerials);

        // Per-serial state for serials that left the adb list. A re-attach re-logs
        // the applied reverse and force-refreshes it again.
        foreach (var key in _reverseAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseAppliedBySerial.Remove(key);
        }
        foreach (var key in _reverseRefreshedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseRefreshedThisRun.Remove(key);
        }
        // A re-attach is a fresh first sighting (it may return on the OEM launcher or
        // a stale splash, needing an am start).
        foreach (var key in _qshellFirstSeenThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _qshellFirstSeenThisRun.Remove(key);
        }
        // Re-pin the default HOME on a re-attach (a reboot drops the pin on Android 11).
        foreach (var key in _homePinnedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _homePinnedThisRun.Remove(key);
        }
        foreach (var key in _homePinFirstFailureBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _homePinFirstFailureBySerial.Remove(key);
        }
        // user_rotation does not survive a panel reboot (brightness does), so a
        // re-attach re-applies. Dropping the confirmed-state record with the
        // latch is what forces that apply to push everything rather than diff
        // against a panel that may have rebooted underneath us.
        foreach (var key in _displayAppliedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _displayAppliedThisRun.Remove(key);
        }
        foreach (var key in _displayFirstFailureBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _displayFirstFailureBySerial.Remove(key);
        }
        foreach (var key in _lastAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _lastAppliedBySerial.Remove(key);
        }
        // Re-arm the grace anchor on detach. _escalationRebootCountBySerial and
        // _lastEscalationRebootBySerial are deliberately NOT cleared here - like
        // _lastQshellRebootBySerial they must survive our reboot's re-enumeration
        // so a still-stranded panel keeps its bounded attempt budget.
        foreach (var key in _firstSeenAtBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _firstSeenAtBySerial.Remove(key);
        }
        // Drop transport-id memory on detach so a re-attach is a fresh first sighting,
        // not a transport-id change against a stale value (this also stops our reboot's
        // re-enumeration from looking like a reseat).
        foreach (var key in _transportIdBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _transportIdBySerial.Remove(key);
        }
        // Re-sync the clock on a re-attach (a reboot may have reset the device clock).
        foreach (var key in _lastClockSyncBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _lastClockSyncBySerial.Remove(key);
        }

        if (_registeredSerial is not null && !seenSerials.Contains(_registeredSerial))
        {
            UnregisterDeviceTarget();
        }
    }

    /// <summary>
    /// <c>adb connect</c> each persisted promotion not already present. Failed
    /// records aren't evicted - the device may be briefly offline, and a stale IP is
    /// overwritten when the USB transport re-promotes.
    /// </summary>
    private async Task ReconnectMissingTransportsAsync(IReadOnlyCollection<DeviceData> currentDevices, CancellationToken ct)
    {
        var present = new HashSet<string>(
            currentDevices.Select(d => d.Serial ?? string.Empty).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        foreach (var record in _promoted.Values.ToList())
        {
            var tcpSerial = $"{record.IpAddress}:{record.Port}";
            if (present.Contains(tcpSerial)) continue;
            try
            {
                var result = await _client.ConnectAsync(record.IpAddress, record.Port, ct);
                ServiceLog.Info(
                    $"[qseries-port-watcher] reconnect {tcpSerial} ({record.Model}): {result?.Trim() ?? "ok"}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] reconnect {tcpSerial} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// For any Q-series serial stuck <c>offline</c> past
    /// <see cref="OfflineRecoveryThreshold"/>, resolve its USB composite parent and
    /// run <c>pnputil /restart-device</c> - a USB-level reset that restarts adbd in
    /// firmware and clears the handshake wedge. Host-side <c>adb</c> can't: the wedge
    /// is device-side and adbd can't be restarted without root. A serial never seen
    /// online (a panel whose adbd comes up wedged on a fresh boot) qualifies when
    /// its USB instance sits on the MediaTek VID; other offline devices (a phone)
    /// are classified once and skipped.
    /// </summary>
    private async Task TryRecoverOfflineQSeriesDevicesAsync(IReadOnlyCollection<DeviceData> deviceList, CancellationToken ct)
    {
        // Windows-only: pnputil is the available USB-reset path, and the wedge is the
        // one seen on the Y70 host. Other hosts would need usbreset(1) etc.
        if (!OperatingSystem.IsWindows()) return;

        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            present.Add(device.Serial);
            if (!_knownQSeriesSerials.Contains(device.Serial)
                && _offlineNonQSeriesSerials.Contains(device.Serial))
            {
                continue;
            }

            if (device.State == DeviceState.Online)
            {
                _offlineSince.Remove(device.Serial);
                _lastInstanceIdLookupBySerial.Remove(device.Serial);
                continue;
            }
            if (device.State != DeviceState.Offline) continue;

            // An adb-over-TCP serial has no USB instance to reset; recovery
            // acts on the USB transport entry only.
            if (QSeriesTransport.IsTcpSerial(device.Serial)) continue;

            if (!_offlineSince.TryGetValue(device.Serial, out var since))
            {
                _offlineSince[device.Serial] = now;
                continue;
            }
            var offlineFor = now - since;
            if (offlineFor < OfflineRecoveryThreshold) continue;

            // The lookup spawns powershell, so a serial it can't resolve (device
            // mid-re-enumeration, or a name mismatch) is retried on the threshold
            // cadence, not every tick - while _offlineSince stays truthful so the
            // logged duration accumulates.
            if (_lastInstanceIdLookupBySerial.TryGetValue(device.Serial, out var lastLookup)
                && now - lastLookup < OfflineRecoveryThreshold)
            {
                continue;
            }
            _lastInstanceIdLookupBySerial[device.Serial] = now;

            var instanceId = TryFindUsbInstanceId(device.Serial);
            if (instanceId is null)
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s but no matching USB instance id found");
                continue;
            }

            if (!_knownQSeriesSerials.Contains(device.Serial) && !IsMediaTekInstanceId(instanceId))
            {
                _offlineNonQSeriesSerials.Add(device.Serial);
                _offlineSince.Remove(device.Serial);
                continue;
            }

            if (_lastRecoveryByInstanceId.TryGetValue(instanceId, out var last)
                && now - last < RecoveryCooldown)
            {
                continue;
            }

            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s, running pnputil /restart-device {instanceId}");
            _lastRecoveryByInstanceId[instanceId] = now;
            if (RunPnputilRestartDevice(instanceId, out var pnputilOut))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart succeeded; awaiting re-enumeration ({pnputilOut})");
                // Fresh 30 s window if it fails to recover after the restart.
                _offlineSince.Remove(device.Serial);
            }
            else
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart failed: {pnputilOut}");
            }

            // Brief pause so the rest of the tick sees a partially-reconnected world.
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (TaskCanceledException) { return; }
        }

        // A re-attach gets a fresh offline-since timer and a fresh classification.
        foreach (var key in _offlineSince.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _offlineSince.Remove(key);
        }
        foreach (var key in _lastInstanceIdLookupBySerial.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _lastInstanceIdLookupBySerial.Remove(key);
        }
        _offlineNonQSeriesSerials.RemoveWhere(k => !present.Contains(k));
    }

    /// <summary>
    /// USB composite InstanceId whose tail is the adb serial
    /// (<c>USB\VID_xxxx&amp;PID_yyyy\&lt;serial&gt;</c>), via Get-PnpDevice.
    /// </summary>
    private static string? TryFindUsbInstanceId(string adbSerial)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(adbSerial)) return null;
        // Single-quote the serial so PowerShell doesn't interpolate it.
        var psScript =
            "Get-PnpDevice -Class USB " +
            "| Where-Object { $_.InstanceId -like '*\\" + adbSerial + "' } " +
            "| Select-Object -First 1 -ExpandProperty InstanceId";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psScript}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            if (!p.WaitForExit(5_000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrEmpty(stdout) ? null : stdout;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>pnputil /restart-device</c>. True on exit 0 or 3010. NexusService runs as
    /// LocalSystem, so no UAC prompt.
    /// </summary>
    private static bool RunPnputilRestartDevice(string instanceId, out string output)
    {
        output = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/restart-device \"{instanceId}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch { }
                output = "timed out after 15s";
                return false;
            }
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            var stderr = p.StandardError.ReadToEnd().Trim();
            output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout} | err: {stderr}";
            // 3010 = success + reboot-recommended (defensive; not emitted by /restart-device).
            return p.ExitCode == 0 || p.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            output = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// First-contact bootstrap: read the device WiFi IP, <c>adb tcpip 5555</c>, then
    /// <c>adb connect</c> over TCP; persist (serial, ip) so the next start skips the
    /// USB round-trip. <c>tcpip</c> must go through the host adb-server (the shell
    /// user can't trigger it), so it's shelled out like start-server.
    /// </summary>
    private async Task TryPromoteToTcpAsync(DeviceData device, CancellationToken ct)
    {
        var ip = await DiscoverDeviceIpAsync(device, ct);
        if (ip is null)
        {
            // A USB-only Q-series has no LAN IP and never will, so this fires every
            // tick - log it once per serial instead of flooding.
            if (_lanIpUnavailableLogged.Add(device.Serial))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: no LAN IP; staying on USB (suppressing repeat)");
            }
            return;
        }

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: adb.exe not found; cannot run tcpip promote");
            return;
        }

        // Restarts adbd in TCP mode; the USB transport drops from the list briefly.
        if (!RunAdb(adbPath, $"-s {device.Serial} tcpip {QSeriesTransport.DefaultAdbTcpPort}", out var tcpipErr))
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: tcpip {QSeriesTransport.DefaultAdbTcpPort} failed: {tcpipErr}");
            return;
        }

        // adbd restart settles in ~2 s (matches adb connect's own retry).
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (TaskCanceledException) { return; }

        try
        {
            var result = await _client.ConnectAsync(ip.ToString(), QSeriesTransport.DefaultAdbTcpPort, ct);
            ServiceLog.Info(
                $"[qseries-port-watcher] promoted {device.Serial} ({device.Model}) -> tcp:{ip}:{QSeriesTransport.DefaultAdbTcpPort}: {result?.Trim() ?? "ok"}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] connect {ip}:{QSeriesTransport.DefaultAdbTcpPort} failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var record = new QSeriesTransportRecord(
            Model: device.Model ?? string.Empty,
            IpAddress: ip.ToString(),
            Port: QSeriesTransport.DefaultAdbTcpPort,
            PromotedAt: DateTimeOffset.UtcNow);
        _promoted[device.Serial] = record;
        _transportStore.Save(_promoted);
    }

    /// <summary>
    /// First routable LAN IPv4 from a series of <c>adb shell</c> probes; null if none
    /// (offline mid-promote, no WiFi, link-local only).
    /// </summary>
    private async Task<System.Net.IPAddress?> DiscoverDeviceIpAsync(DeviceData device, CancellationToken ct)
    {
        // First command yielding a parseable IPv4 wins; `ip route get` picks the
        // outbound interface (wlan0 on the Q60).
        string[] commands =
        {
            "ip route get 1.1.1.1",
            "getprop dhcp.wlan0.ipaddress",
            "ip -4 addr show wlan0",
        };
        foreach (var cmd in commands)
        {
            var receiver = new ConsoleOutputReceiver();
            try
            {
                await _client.ExecuteShellCommandAsync(device, cmd, receiver, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: shell `{cmd}` failed: {ex.GetType().Name}");
                continue;
            }
            var ip = QSeriesTransport.ParseLanIPv4(receiver.ToString());
            if (ip is not null) return ip;
        }
        return null;
    }

    /// <summary>Synchronous adb.exe shell-out (used for <c>tcpip</c> and the
    /// shutdown reboot). True on exit 0.</summary>
    private static bool RunAdb(string adbPath, string arguments, out string errorOutput, int timeoutMs = 8_000)
    {
        errorOutput = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                errorOutput = $"timed out after {timeoutMs}ms";
                return false;
            }
            if (p.ExitCode != 0)
            {
                errorOutput = $"exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            errorOutput = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool IsQSeries(DeviceData device)
    {
        // The adb device-line `model:` field is the only reliable Q-series
        // discriminator; state + serial don't disambiguate.
        var model = device.Model ?? string.Empty;
        return QSeriesModels.Contains(model.Replace(" ", "_"));
    }

    /// <summary>USB instance id sits on the MediaTek VID (the Q-series panel's
    /// SoC) - the recovery-suspect test for an offline serial never seen online.</summary>
    internal static bool IsMediaTekInstanceId(string instanceId) =>
        instanceId.Contains($"VID_{MediaTekAdbVendorId:X4}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One-line adb + PnP state dump while Q-series hardware is present but no
    /// online Q-series is visible to adb - the state every rescue layer (reverse,
    /// am start, HOME pin, escalation reboot) silently waits on. Logged on state
    /// change only; a closing line marks recovery and re-arms the diagnostic.
    /// </summary>
    private void LogAdbVisibility(IReadOnlyCollection<DeviceData> deviceList, HashSet<string> onlineQSeries)
    {
        if (onlineQSeries.Count > 0)
        {
            if (_adbVisibilitySignature is not null)
            {
                _adbVisibilitySignature = null;
                ServiceLog.Info("[qseries-port-watcher] q-series visible to adb again");
            }
            return;
        }

        var signature = BuildAdbVisibilitySignature(deviceList, _presence.UsbEntriesFor(MediaTekAdbVendorId));
        if (signature == _adbVisibilitySignature) return;
        _adbVisibilitySignature = signature;
        ServiceLog.Info(
            $"[qseries-port-watcher] q-series hardware present but none online via adb; {signature}");
    }

    /// <summary>
    /// <c>adb=[serial(state,model)…] mediatek-pnp=[name pid class mfr inf…]</c>.
    /// The PnP half carries each MediaTek devnode's bound class and driver INF -
    /// enough to spot a vendor-driver misbinding (device node class
    /// AndroidUsbDeviceClass while adb stays empty) and name the
    /// <c>pnputil /delete-driver</c> target. Composite children that share the
    /// parent's product string dedupe into one row (UsbDeviceEntryBuilder), so
    /// row count is not a healthy/misbound signal on its own.
    /// </summary>
    internal static string BuildAdbVisibilitySignature(
        IEnumerable<DeviceData> adbDevices, IEnumerable<UsbDeviceEntry> mediatekEntries)
    {
        var adb = string.Join(", ", adbDevices
            .Where(d => !string.IsNullOrEmpty(d.Serial))
            .Select(d => $"{d.Serial}({d.State}{(string.IsNullOrEmpty(d.Model) ? "" : "," + d.Model)})"));
        var pnp = string.Join(", ", mediatekEntries.Select(e =>
            $"{e.Name} pid={e.ProductId:X4} class={e.Class} mfr={e.Manufacturer} inf={e.Driver}"));
        return $"adb=[{adb}] mediatek-pnp=[{pnp}]";
    }

    /// <summary>
    /// Shell out to <c>adb start-server</c> when the server is down (kill-server,
    /// fresh boot, crash) - AdvancedSharpAdbClient won't spawn it. adb.exe resolved
    /// via <see cref="Nexus.Service.Panel.AdbLocator"/> (bundled copy, then PATH/SDK).
    /// </summary>
    private static bool TryStartAdbServer()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            ServiceLog.Info("[qseries-port-watcher] adb.exe not found in PATH or common locations; cannot start adb-server");
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
                ServiceLog.Info("[qseries-port-watcher] adb start-server timed out after 8s");
                return false;
            }
            if (p.ExitCode != 0)
            {
                ServiceLog.Info($"[qseries-port-watcher] adb start-server exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            ServiceLog.Info("[qseries-port-watcher] adb-server (re)started");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[qseries-port-watcher] adb start-server threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private const string QshellPackage = "com.hellonexus.qshell";

    /// <summary>qshell package/activity; matches its AndroidManifest.xml.</summary>
    private const string QshellComponent = QshellPackage + "/.MainActivity";

    /// <summary>Substring of <c>dumpsys window mCurrentFocus</c> when qshell owns focus.</summary>
    private const string QshellFocusMarker = QshellPackage;

    /// <summary>
    /// Last <c>am start</c> time per serial. Throttles re-launch; the foreground
    /// check itself is cheap and runs every tick.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellStartBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials whose first sighting this run has been handled (escalation grace
    /// anchored, qshell ensured foreground). A qshell left running across a
    /// service restart keeps its WebView mounted; its SPA reconnects the socket
    /// on its own, so this run no longer force-stops it. Cleared on detach.
    /// </summary>
    private readonly HashSet<string> _qshellFirstSeenThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials whose default HOME has been re-pinned to qshell this run. The
    /// install-time <c>set-home-activity</c> (ApkFlasher) does not survive a panel
    /// cold boot on Android 11, so the panel comes up on the launcher chooser; the
    /// per-tick am-start masks it but never restores the default. Re-assert the pin
    /// once per attach, after qshell is confirmed installed, so the next cold boot
    /// resolves HOME without the chooser. Cleared on detach so a reboot re-pins.
    /// </summary>
    private readonly HashSet<string> _homePinnedThisRun = new(StringComparer.Ordinal);

    /// <summary>Serial -> when the pin first failed this attach; bounds the
    /// un-latch retry below. Cleared on success and on detach.</summary>
    private readonly Dictionary<string, DateTimeOffset> _homePinFirstFailureBySerial = new(StringComparer.Ordinal);


    /// <summary>
    /// Serials whose display state (orientation, brightness, screen power)
    /// has been re-asserted this attach. <c>user_rotation</c> is confirmed
    /// not to survive a panel reboot (unlike <c>set-fix-to-user-rotation</c>
    /// and <c>accelerometer_rotation</c>, which persist); brightness
    /// persistence across reboot is unconfirmed either way, and a fresh
    /// Android boot always starts the screen on - so a re-attach re-applies
    /// all three regardless. Cleared on detach and whenever
    /// <see cref="AnnounceDisplayChange"/> flags a live setting change.
    /// </summary>
    private readonly HashSet<string> _displayAppliedThisRun = new(StringComparer.Ordinal);

    /// <summary>Serial -> when the display apply first failed this attach;
    /// bounds the un-latch retry below. Cleared on success and on detach.</summary>
    private readonly Dictionary<string, DateTimeOffset> _displayFirstFailureBySerial = new(StringComparer.Ordinal);

    /// <summary>Display state last confirmed on the panel.</summary>
    private readonly record struct AppliedDisplayState(string Orientation, int Brightness, bool ScreenOff);

    /// <summary>
    /// Serial -> the state its last readback confirmed, so an apply only sends
    /// what changed. Dropped on detach, on any failed apply, and whenever the
    /// panel reboots, since the next apply must then push everything.
    /// </summary>
    private readonly Dictionary<string, AppliedDisplayState> _lastAppliedBySerial = new(StringComparer.Ordinal);


    /// <summary>
    /// Set by <see cref="AnnounceDisplayChange"/> from a request thread; 1
    /// means the next tick must clear <see cref="_displayAppliedThisRun"/>
    /// before its device loop, so a live setting change takes effect without
    /// waiting for a re-attach. Interlocked because the per-attach HashSets
    /// above are tick-thread-only and must never be touched from a request
    /// thread.
    /// </summary>
    private int _displayDirty;

    /// <summary>
    /// Set when a power hook drives the panel's screen from the SystemEvents
    /// pump thread. Interlocked because <see cref="_lastAppliedBySerial"/> it
    /// invalidates is tick-thread-only.
    /// </summary>
    private int _displayRecordStale;

    /// <summary>
    /// Wakes the tick loop out of its poll wait; the adb calls stay on the tick
    /// thread, which owns the transport. Counting, so a release while a tick is
    /// already running persists and the following wait returns at once. A
    /// consumer-reset primitive would drop that wake.
    /// </summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Called from POST /qseries/rotation or POST /qseries/display to
    /// apply a changed display setting on the next tick instead of the next
    /// physical attach.</summary>
    internal void AnnounceDisplayChange()
    {
        Interlocked.Exchange(ref _displayDirty, 1);
        // Full means a wake is already pending; the tick it triggers reads the
        // flag set above, so dropping this release loses nothing.
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Last adb transport id per serial. A change (same serial) is the reliable reseat
    /// signal the 10 s poll otherwise misses; see TickAsync.
    /// </summary>
    private readonly Dictionary<string, string> _transportIdBySerial = new(StringComparer.Ordinal);

    private static readonly TimeSpan QshellRestartThrottle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Minimum gap between reboots per serial. A reboot re-enumerates the device (new
    /// transport id), so without this a flapping connector - or the reboot's own
    /// re-attach - could reboot-loop. 2 min spans reboot + qshell bootstrap.
    /// </summary>
    private static readonly TimeSpan QshellRebootCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Grace after first sighting this run before the liveness-gated escalation
    /// reboot. Must exceed the slowest legitimate reconnect: a cold qshell
    /// bootstrap pulls the SPA + ~56 assets over the marginal USB-FFS link and
    /// takes ~2 min (per the panel recovery guide), far longer than the soft
    /// case (qshell stays up, socket reconnects in seconds). Sized above that so
    /// a cold boot / relaunch is never preempted; only a panel still silent past
    /// it is rebooted.
    /// </summary>
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromSeconds(180);

    /// <summary>Serial → last reboot time (anti-loop cooldown). NOT cleared on detach,
    /// so it survives the reboot's own re-enumeration.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellRebootBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Time a serial was first seen this run; anchors <see cref="EscalationGrace"/>.
    /// Cleared on detach (a re-attach re-arms it).
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _firstSeenAtBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serial -> escalation reboots issued this run. Bounded by
    /// <see cref="MaxEscalationRebootsPerRun"/> and spaced by
    /// <see cref="EscalationRetryCooldown"/> so a panel whose recovery reboot
    /// lands in another wedge gets further attempts, while a dead panel can't
    /// reboot-loop. NOT cleared on detach (survives the reboot's
    /// re-enumeration); a fresh service start re-allows.
    /// </summary>
    private readonly Dictionary<string, int> _escalationRebootCountBySerial = new(StringComparer.Ordinal);

    /// <summary>Serial -> last escalation reboot time; spaces retries.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastEscalationRebootBySerial = new(StringComparer.Ordinal);

    /// <summary>Minimum gap between escalation reboots for one serial.</summary>
    private static readonly TimeSpan EscalationRetryCooldown = TimeSpan.FromMinutes(30);

    /// <summary>Escalation reboots allowed per serial per service run.</summary>
    private const int MaxEscalationRebootsPerRun = 3;

    /// <summary>
    /// Serials whose last am-start reported the qshell activity does not exist:
    /// qshell is not installed (a pre-upgrade panel still on the OEM launcher).
    /// Suppresses the escalation reboot - an absent qshell is the expected
    /// pre-install state, not a USB-FFS wedge a reboot could clear.
    /// </summary>
    private readonly HashSet<string> _qshellMissingBySerial = new(StringComparer.Ordinal);

    /// <summary>Re-push the host clock to the panel this often; covers RTC drift
    /// without spamming set-time every tick.</summary>
    private static readonly TimeSpan ClockSyncInterval = TimeSpan.FromMinutes(30);

    /// <summary>Serial -> last device-clock sync time. Cleared on detach so a
    /// re-attach (which may have reset the clock on reboot) re-syncs at once.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastClockSyncBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Keep qshell foreground: dump <c>mCurrentFocus</c>; if something else holds
    /// focus, <c>am start</c> it. Covers first-attach (OEM launcher), post-USB-reset
    /// kill, user force-stop, and reboot. Does NOT force-stop a running qshell: it
    /// keeps its WebView mounted across a host restart and its SPA reconnects on
    /// its own, so a force-stop would gratuitously bounce the panel through the
    /// splash. First sighting this run only anchors the escalation grace.
    /// </summary>
    private async Task EnsureQshellForegroundAsync(DeviceData device, CancellationToken ct)
    {
        if (!_qshellFirstSeenThisRun.Contains(device.Serial))
        {
            _qshellFirstSeenThisRun.Add(device.Serial);
            _firstSeenAtBySerial[device.Serial] = DateTimeOffset.UtcNow;
        }

        // Device-side grep narrows the verbose dumpsys (busybox grep present on
        // Q-series Android 11).
        var focusReceiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(device, "dumpsys window | grep mCurrentFocus", focusReceiver, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: foreground check failed: {ex.GetType().Name}");
            return;
        }
        if (focusReceiver.ToString().Contains(QshellFocusMarker, StringComparison.Ordinal))
        {
            _qshellMissingBySerial.Remove(device.Serial);
            await ReassertQshellHomeAsync(device, ct);
            return;
        }

        // Throttle: give a just-issued am start time to take.
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellStartBySerial.TryGetValue(device.Serial, out var last)
            && now - last < QshellRestartThrottle)
        {
            return;
        }
        _lastQshellStartBySerial[device.Serial] = now;

        var startReceiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(device, $"am start -n {QshellComponent}", startReceiver, ct);
            var startOut = startReceiver.ToString().Trim();
            // "Activity class {...} does not exist" => qshell is not installed yet.
            if (startOut.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                _qshellMissingBySerial.Add(device.Serial);
            }
            else
            {
                _qshellMissingBySerial.Remove(device.Serial);
                await ReassertQshellHomeAsync(device, ct);
            }
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: qshell not in foreground, ran am start ({startOut})");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: am start qshell failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-pin qshell as the panel's default HOME (<c>cmd package
    /// set-home-activity</c>), once per attach. See <see cref="_homePinnedThisRun"/>
    /// for why the install-time pin is insufficient. Caller must have confirmed
    /// qshell is installed.
    /// </summary>
    private async Task ReassertQshellHomeAsync(DeviceData device, CancellationToken ct)
    {
        if (!_homePinnedThisRun.Add(device.Serial)) return;
        var receiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(
                device, $"cmd package set-home-activity {QshellComponent}", receiver, ct);
            var output = receiver.ToString().Trim();
            if (SetHomeActivityTook(output))
            {
                _homePinFirstFailureBySerial.Remove(device.Serial);
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: re-pinned default HOME to qshell ({output})");
            }
            else
            {
                // Early panel boot: `cmd` answers "Can't find service: package" as
                // normal output with a clean exit, so only checking for exceptions
                // latched the pin as done without it ever landing.
                RecordHomePinFailure(device.Serial, output);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordHomePinFailure(device.Serial, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Bounded pin retry: un-latch so the next tick retries, log the first
    /// failure only, and once the window elapses leave the serial latched (no
    /// further shell calls or lines until re-attach). A persistently failing pin
    /// - boot transient or not - must not shell out and log every tick until
    /// detach.
    /// </summary>
    private void RecordHomePinFailure(string serial, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_homePinFirstFailureBySerial.TryGetValue(serial, out var firstFailure))
        {
            _homePinFirstFailureBySerial[serial] = now;
            _homePinnedThisRun.Remove(serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {serial}: set-home-activity did not take ({detail}); retrying (suppressing repeats)");
            return;
        }
        // EscalationGrace spans the slowest legitimate "package service
        // not yet up" boot window.
        if (now - firstFailure >= EscalationGrace)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {serial}: set-home-activity kept failing ({detail}); giving up until re-attach");
            return;
        }
        _homePinnedThisRun.Remove(serial);
    }

    /// <summary>
    /// <c>cmd package set-home-activity</c> prints "Success" when the pin landed.
    /// Early in panel boot the package service is not yet up and the same call
    /// prints "Can't find service: package" with a clean exit.
    /// </summary>
    internal static bool SetHomeActivityTook(string output) =>
        output.Contains("Success", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-assert the stored display state (orientation, brightness, screen
    /// power), once per attach, sharing one latch and one bounded retry across
    /// all three so a single stuck setting doesn't spam three separate log
    /// lines every tick. qshell is pinned as the panel's default HOME, and
    /// Android does not rotate the launcher stack; without
    /// <c>set-fix-to-user-rotation enabled</c> the locked rotation is recorded
    /// (<c>mUserRotation</c>) but the display itself never turns. None of the
    /// apply commands print output on success, so each is read back
    /// separately to confirm it took.
    /// </summary>
    private async Task ReassertPanelDisplayAsync(DeviceData device, CancellationToken ct)
    {
        if (!_displayAppliedThisRun.Add(device.Serial)) return;

        var qseries = _configStore.Load().QSeries;
        var userRotation = ToUserRotation(qseries.Orientation);
        var brightnessByte = PercentToBrightnessByte(qseries.Brightness);
        var wantAwake = !qseries.ScreenOff;

        // Without a record of what this panel already has, its state is unknown
        // (a reboot resets user_rotation) so everything is pushed. Afterwards
        // only the settings that changed go over the wire; re-asserting the full
        // set costs several adb round trips, one of which dumps 27 KB, where a
        // lone brightness nudge needs two.
        var known = _lastAppliedBySerial.TryGetValue(device.Serial, out var last);
        var doOrientation = !known || last.Orientation != qseries.Orientation;
        var doBrightness = !known || last.Brightness != qseries.Brightness;
        var doScreen = !known || last.ScreenOff != qseries.ScreenOff;
        if (!doOrientation && !doBrightness && !doScreen) return;

        try
        {
            var commands = new List<string>(5);
            if (doOrientation)
            {
                commands.Add("settings put system accelerometer_rotation 0");
                commands.Add("cmd window set-fix-to-user-rotation enabled");
                commands.Add($"cmd window set-user-rotation lock {userRotation}");
            }
            if (doBrightness) commands.Add($"settings put system screen_brightness {brightnessByte}");
            if (doScreen) commands.Add($"input keyevent {(wantAwake ? KeyeventWakeup : KeyeventSleep)}");

            // Any output from these is a failure ("Can't find service: window"
            // early in panel boot, on a clean exit), so it carries the retry's
            // only diagnostic.
            var applyOutput = new StringBuilder();
            foreach (var command in commands)
            {
                var receiver = new ConsoleOutputReceiver();
                await _client.ExecuteShellCommandAsync(device, command, receiver, ct);
                var text = receiver.ToString().Trim();
                if (text.Length > 0) applyOutput.Append(text).Append("; ");
            }

            var rotationOutput = "";
            var rotationOk = true;
            if (doOrientation)
            {
                var rotationReadback = new ConsoleOutputReceiver();
                await _client.ExecuteShellCommandAsync(
                    device, "dumpsys window displays | grep mCurrentRotation", rotationReadback, ct);
                rotationOutput = rotationReadback.ToString().Trim();
                rotationOk = PanelOrientationTook(rotationOutput, userRotation);
            }

            var brightnessOutput = "";
            var brightnessOk = true;
            if (doBrightness)
            {
                // No /sys/class/backlight and dumpsys display exposes no
                // brightness field on this panel, so `settings get` is the only
                // readback.
                var brightnessReadback = new ConsoleOutputReceiver();
                await _client.ExecuteShellCommandAsync(
                    device, "settings get system screen_brightness", brightnessReadback, ct);
                brightnessOutput = brightnessReadback.ToString().Trim();
                brightnessOk = PanelBrightnessTook(brightnessOutput, brightnessByte);
            }

            var powerOutput = "";
            var powerOk = true;
            if (doScreen)
            {
                var powerReadback = new ConsoleOutputReceiver();
                await _client.ExecuteShellCommandAsync(
                    device, "dumpsys power | grep mWakefulness=", powerReadback, ct);
                powerOutput = powerReadback.ToString().Trim();
                powerOk = PanelScreenPowerTook(powerOutput, wantAwake);
            }

            if (rotationOk && brightnessOk && powerOk)
            {
                _displayFirstFailureBySerial.Remove(device.Serial);
                _lastAppliedBySerial[device.Serial] =
                    new AppliedDisplayState(qseries.Orientation, qseries.Brightness, qseries.ScreenOff);
                var applied = string.Join(" ", new[]
                {
                    doOrientation ? $"orientation={qseries.Orientation} (userRotation={userRotation}, {rotationOutput})" : null,
                    doBrightness ? $"brightness={qseries.Brightness}% (byte={brightnessByte}, {brightnessOutput})" : null,
                    doScreen ? $"screen={(wantAwake ? "on" : "off")} ({powerOutput})" : null,
                }.Where(p => p is not null));
                ServiceLog.Info($"[qseries-port-watcher] {device.Serial}: applied display state {applied}");
            }
            else
            {
                // The panel diverged from the record, so the next attempt pushes
                // everything rather than trusting the diff.
                _lastAppliedBySerial.Remove(device.Serial);
                RecordDisplayFailure(
                    device.Serial,
                    $"rotation-ok={rotationOk} brightness-ok={brightnessOk} power-ok={powerOk} {applyOutput}{rotationOutput}; {brightnessOutput}; {powerOutput}");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _lastAppliedBySerial.Remove(device.Serial);
            RecordDisplayFailure(device.Serial, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Bounded display-apply retry: un-latch so the next tick retries, log the
    /// first failure only, and once the window elapses leave the serial latched
    /// (no further shell calls or lines until re-attach). Early in panel boot
    /// <c>cmd</c> and <c>settings</c> answer "Can't find service: X" with a
    /// clean exit, so only checking for exceptions would latch the apply as
    /// done without it ever landing.
    /// </summary>
    private void RecordDisplayFailure(string serial, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_displayFirstFailureBySerial.TryGetValue(serial, out var firstFailure))
        {
            _displayFirstFailureBySerial[serial] = now;
            _displayAppliedThisRun.Remove(serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {serial}: display state did not take ({detail}); retrying (suppressing repeats)");
            return;
        }
        // EscalationGrace spans the slowest legitimate "Can't find service"
        // boot window.
        if (now - firstFailure >= EscalationGrace)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {serial}: display state kept failing ({detail}); giving up until re-attach");
            return;
        }
        _displayAppliedThisRun.Remove(serial);
    }

    /// <summary>180 degree flip only: Portrait maps to Android user_rotation 0,
    /// PortraitFlipped to 2. Both are portrait, so the panel resolution is
    /// unchanged and there is no axis swap to account for.</summary>
    internal static int ToUserRotation(string orientation) =>
        orientation == Nexus.Service.Models.Displays.DisplayOrientations.PortraitFlipped ? 2 : 0;

    /// <summary>0-100% mapped to the 0-255 byte `settings put system
    /// screen_brightness` expects; out-of-range input is clamped defensively
    /// even though the route already validates it.</summary>
    internal static int PercentToBrightnessByte(int pct)
    {
        var clamped = Math.Clamp(pct, 0, 100);
        return (int)Math.Round(clamped / 100.0 * 255.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// True when `settings get system screen_brightness` echoes exactly the
    /// byte just written. That command is the only brightness readback
    /// available: this panel has no /sys/class/backlight and dumpsys display
    /// exposes no brightness field.
    /// </summary>
    internal static bool PanelBrightnessTook(string settingsGetOutput, int expectedByte) =>
        settingsGetOutput.Trim() == expectedByte.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when `dumpsys power | grep mWakefulness=` reports the requested
    /// power state. KEYCODE_SLEEP/KEYCODE_WAKEUP (223/224) set the state
    /// rather than toggling it, unlike KEYCODE_POWER (26).
    /// </summary>
    internal static bool PanelScreenPowerTook(string dumpsysOutput, bool expectedAwake) =>
        dumpsysOutput.Contains(expectedAwake ? "mWakefulness=Awake" : "mWakefulness=Asleep", StringComparison.Ordinal);

    /// <summary>
    /// True when <c>dumpsys window displays | grep mCurrentRotation</c> shows
    /// the rotation the requested user_rotation should produce.
    /// <c>ROTATION_0</c> is not a substring of <c>ROTATION_180</c>, so a plain
    /// Contains distinguishes them without parsing the value. This confirms the
    /// panel reached the requested rotation, not that the commands ran: for
    /// Portrait the expected value is also the panel's boot rotation, so a
    /// silent no-op reads as success. A flip away from Portrait re-runs all
    /// three commands and is verified.
    /// </summary>
    internal static bool PanelOrientationTook(string dumpsysOutput, int userRotation) =>
        dumpsysOutput.Contains(userRotation == 0 ? "ROTATION_0" : "ROTATION_180", StringComparison.Ordinal);

    /// <summary>
    /// A reseat (same serial, new transport id) leaves a degraded USB-FFS link that
    /// strands qshell. Reboot to reset device-side adbd/FFS; the post-reboot first
    /// sighting re-anchors the grace and ensures qshell is foreground. Throttled per
    /// serial. Returns true when a reboot was issued (caller then skips this tick's
    /// reverse/qshell passes).
    /// </summary>
    private async Task<bool> TryRebootOnReseatAsync(DeviceData device, string lastTransportId, string transportId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // Within cooldown: re-arm first-sighting handling instead of rebooting again.
            _qshellFirstSeenThisRun.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (reseat) but a reboot is within cooldown; re-arming first-sighting handling instead");
            return false;
        }

        _lastQshellRebootBySerial[device.Serial] = now;
        try
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (USB re-enumeration / reseat); rebooting device to reset USB-FFS");
            await _client.RebootAsync(device, ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: reseat reboot failed: {ex.GetType().Name}: {ex.Message}; re-arming first-sighting handling");
            _qshellFirstSeenThisRun.Remove(device.Serial);
            return false;
        }
    }

    /// <summary>
    /// The Q-series panel is sealed with no NTP, so its RTC drifts (seen stuck
    /// years off), and the panel renders its clock widget from the device's own
    /// wall clock. Push the host's time + IANA timezone to it via <c>cmd
    /// alarm</c>, which the shell user can call without root. Re-synced on first
    /// sighting and every <see cref="ClockSyncInterval"/> for drift.
    /// </summary>
    private async Task SyncDeviceClockAsync(DeviceData device, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastClockSyncBySerial.TryGetValue(device.Serial, out var last)
            && now - last < ClockSyncInterval)
        {
            return;
        }
        _lastClockSyncBySerial[device.Serial] = now;

        try
        {
            // Timezone first so the wall clock lands in the right offset; the
            // host TZ is stable, but re-sending it is a cheap no-op.
            var iana = ResolveHostIanaTimeZone();
            if (iana is not null)
            {
                await _client.ExecuteShellCommandAsync(
                    device, $"cmd alarm set-timezone {iana}", new ConsoleOutputReceiver(), ct);
            }
            await _client.ExecuteShellCommandAsync(
                device, $"cmd alarm set-time {now.ToUnixTimeMilliseconds()}", new ConsoleOutputReceiver(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Forget the sync time so the next tick retries.
            _lastClockSyncBySerial.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: clock sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Host system timezone as an IANA id (<c>cmd alarm set-timezone</c> wants
    /// IANA, e.g. "America/Los_Angeles"); null when a Windows id has no IANA
    /// mapping, in which case the timezone is left as-is and only the clock set.
    /// </summary>
    private static string? ResolveHostIanaTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    }

    /// <summary>
    /// Last-resort backstop for a hard USB-FFS wedge: a host *service* restart can
    /// leave the reverse tunnel listed but passing no traffic, with no reseat to
    /// trigger <see cref="TryRebootOnReseatAsync"/>. qshell keeps its WebView up and
    /// its SPA retries the socket, but across a hard wedge that socket can never
    /// reconnect, so the panel sits on its clock failsafe. A device reboot is the
    /// only reliable clear of a hard wedge.
    ///
    /// Gated on real liveness so the common restart (tunnel fine, or a reverse
    /// remove+re-add cleared a soft wedge) never reboots. With the tunnel
    /// listener active the signal is inbound bytes on the tunnel port
    /// (WebSocket keepalive pongs included), whose only intended client is the
    /// panel's adb tunnel (caveats on <c>PanelTunnelMonitor</c>), so an open
    /// desktop dashboard cannot mask a stranded panel. Legacy
    /// fallback (tunnel port unbound): the record's <c>LastSeenAt</c>, which any
    /// local client's GET bumps - that variant risks a missed reboot while the
    /// dashboard is open, never a spurious one. Only a panel still silent past
    /// <see cref="EscalationGrace"/> is rebooted.
    ///
    /// One transitional spurious reboot is possible on the first run after the
    /// host-side tunnel port changes: a panel whose SPA reconnected through the
    /// old mapping before the first tick re-points it produces no tunnel
    /// activity and gets bounced once; it comes back on the new mapping.
    ///
    /// Up to <see cref="MaxEscalationRebootsPerRun"/> reboots per run spaced by
    /// <see cref="EscalationRetryCooldown"/> (a recovery reboot can itself land
    /// in a fresh wedge); shares <see cref="_lastQshellRebootBySerial"/> with
    /// the reseat path so they can't double-reboot across the re-enumeration.
    /// </summary>
    private async Task TryEscalateRebootAsync(DeviceData device, CancellationToken ct)
    {
        // No anchor -> not seen this run yet; nothing to escalate.
        if (!_firstSeenAtBySerial.TryGetValue(device.Serial, out var firstSeenAt)) return;
        // An in-flight adb install holds the transport; a reboot here would abort it.
        if (_deviceRegistry?.TryGet(QshellPackage)?.InstallInProgress == true) return;
        // qshell is not installed (pre-upgrade panel on the OEM launcher): a panel
        // running the OEM is the expected pre-install state, not a wedge to reboot.
        if (_qshellMissingBySerial.Contains(device.Serial)) return;
        // Screen off by request: a stopped activity cannot bump LastSeenAt, so the
        // liveness gate below reads a deliberately dark panel as a wedged one and
        // reboots it back on.
        if (_configStore.Load().QSeries.ScreenOff) return;

        var now = DateTimeOffset.UtcNow;
        var sinceFirstSeen = now - firstSeenAt;
        if (sinceFirstSeen < EscalationGrace) return;

        // Liveness gate: skip the reboot if the panel contacted the service since we
        // started watching it this run. A reboot here would bounce a recovered panel
        // through the splash.
        if (PanelContactedSince(
                _tunnelMonitor?.IsActive == true ? _tunnelMonitor.LastInboundActivityUnixMs : null,
                QSeriesPanelLastSeenUtcMs(),
                firstSeenAt.ToUnixTimeMilliseconds()))
        {
            return;
        }

        _escalationRebootCountBySerial.TryGetValue(device.Serial, out var rebootCount);
        if (!EscalationRebootPermitted(
                rebootCount,
                _lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot) ? lastReboot : null,
                _lastEscalationRebootBySerial.TryGetValue(device.Serial, out var lastEscalation) ? lastEscalation : null,
                now))
        {
            return;
        }

        try
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: panel never contacted the service {sinceFirstSeen.TotalSeconds:F0}s after first sighting; rebooting to clear a hard USB-FFS wedge or a stranded qshell (attempt {rebootCount + 1}/{MaxEscalationRebootsPerRun})");
            await _client.RebootAsync(device, ct);
            // Record the reboot only after it's issued; if RebootAsync throws (stale
            // transport id mid-re-enumeration) leave the flags unset so the next tick
            // retries instead of latching the attempt as spent.
            _lastQshellRebootBySerial[device.Serial] = now;
            _lastEscalationRebootBySerial[device.Serial] = now;
            _escalationRebootCountBySerial[device.Serial] = rebootCount + 1;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: escalation reboot failed: {ex.GetType().Name}: {ex.Message}; will retry next tick");
        }
    }

    /// <summary>
    /// Escalation reboot budget: bounded attempts per run, spaced by
    /// <see cref="EscalationRetryCooldown"/>, and never inside the shared
    /// <see cref="QshellRebootCooldown"/> window after ANY reboot (a reseat
    /// reboot may still be playing out).
    /// </summary>
    internal static bool EscalationRebootPermitted(int rebootCount, DateTimeOffset? lastAnyReboot, DateTimeOffset? lastEscalationReboot, DateTimeOffset now)
    {
        if (rebootCount >= MaxEscalationRebootsPerRun) return false;
        if (lastAnyReboot is DateTimeOffset any && now - any < QshellRebootCooldown) return false;
        if (lastEscalationReboot is DateTimeOffset escalation && now - escalation < EscalationRetryCooldown) return false;
        return true;
    }

    /// <summary>
    /// Escalation liveness decision. When the tunnel monitor is active
    /// (<paramref name="tunnelLastInboundMs"/> non-null), only tunnel activity
    /// counts - the record's <c>LastSeenAt</c> is ignored because any local
    /// client's GET bumps it (the desktop dashboard masking a stranded panel is
    /// the failure this replaces). Legacy fallback compares the record instead.
    /// </summary>
    internal static bool PanelContactedSince(long? tunnelLastInboundMs, long? recordLastSeenMs, long firstSeenAtMs)
    {
        if (tunnelLastInboundMs is long tunnelMs)
        {
            return tunnelMs >= firstSeenAtMs;
        }
        return recordLastSeenMs is long recordMs && recordMs >= firstSeenAtMs;
    }

    /// <summary>
    /// Freshest <c>LastSeenAt</c> (unix ms UTC) among Q-series panel records, or null
    /// if none exist yet. The Q-series posts <c>surface == "q60"</c> for both Q60 and
    /// Q80, and the registry keeps one single-instance record per surface, so this is
    /// the attached panel's last contact with the service.
    /// </summary>
    private long? QSeriesPanelLastSeenUtcMs() =>
        _panelDevices.List()
            .Where(d => string.Equals(d.Capabilities?.Surface, PanelSurfaces.Q60, StringComparison.Ordinal))
            .Select(d => (long?)d.LastSeenAt)
            .FirstOrDefault();

    private void RegisterDeviceTarget(string serial)
    {
        if (_deviceRegistry is null) return;
        if (_registeredSerial == serial) return;
        var target = new QSeriesAdbDeviceTarget(serial, _client);
        _deviceRegistry.Register(target);
        _registeredSerial = serial;
    }

    private void UnregisterDeviceTarget()
    {
        if (_deviceRegistry is null || _registeredSerial is null) return;
        _deviceRegistry.Unregister(QshellPackage);
        _registeredSerial = null;
    }

    private async Task EnsureReverseAsync(DeviceData device, CancellationToken ct)
    {
        // First sighting this run: remove + re-add the reverse to clear a soft wedge
        // (a tcp:{port} entry still listed but passing no traffic) at the moment a
        // (re)start creates it. Steady-state ticks keep the quiet rebind below; a hard
        // wedge this can't clear is TryEscalateRebootAsync's job.
        if (!_reverseRefreshedThisRun.Contains(device.Serial))
        {
            _reverseRefreshedThisRun.Add(device.Serial);
            _reverseAppliedBySerial.Remove(device.Serial); // re-log the apply below
            try
            {
                await _client.RemoveReverseForwardAsync(device, _deviceSpec, ct);
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: force-refreshed reverse on first sighting this run (removed {_deviceSpec})");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // No prior reverse (fresh boot) throws here - harmless; the create installs it.
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: reverse pre-refresh remove no-op: {ex.GetType().Name}");
            }
        }

        // allowRebind: true is idempotent - re-binds an existing reverse instead of
        // failing "cannot rebind existing socket" on later ticks. Arg order per
        // IAdbClient: remote (device-side) first, local (host-side) second.
        try
        {
            await _client.CreateReverseForwardAsync(device, _deviceSpec, _hostSpec, true, ct);
            if (!_reverseAppliedBySerial.TryGetValue(device.Serial, out _))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] reverse applied: {device.Serial} ({device.Model}) device {_deviceSpec} -> host {_hostSpec}");
                _reverseAppliedBySerial[device.Serial] = true;
            }
        }
        catch (Exception ex)
        {
            // Re-apply runs next tick; forget the applied flag so the recovery re-logs.
            _reverseAppliedBySerial.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] reverse apply failed for {device.Serial}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class QSeriesAdbDeviceTarget : IAdbDeviceTarget
    {
        private readonly string _serial;
        private readonly AdbClient _client;

        public QSeriesAdbDeviceTarget(string serial, AdbClient client)
        {
            _serial = serial;
            _client = client;
        }

        public string Serial => _serial;
        public string Package => QshellPackage;
        public bool InstallInProgress { get; set; }

        public async Task<string> ShellAsync(string command, CancellationToken ct)
        {
            IEnumerable<DeviceData> devices;
            try
            {
                devices = await _client.GetDevicesAsync(ct);
            }
            catch
            {
                return string.Empty;
            }

            DeviceData? found = null;
            foreach (var d in devices)
            {
                if (string.Equals(d.Serial, _serial, StringComparison.Ordinal))
                {
                    found = d;
                    break;
                }
            }

            if (found is null || string.IsNullOrEmpty(found.Value.Serial))
            {
                return string.Empty;
            }

            var receiver = new ConsoleOutputReceiver();
            await _client.ExecuteShellCommandAsync(found.Value, command, receiver, ct);
            return receiver.ToString();
        }
    }
}
