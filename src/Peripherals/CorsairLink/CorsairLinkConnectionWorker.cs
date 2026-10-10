using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Discovers every iCUE LINK System Hub and runs one session per hub: open its
/// command interface, take it into software mode, and poll speed/temperature
/// telemetry while connected.
/// </summary>
public sealed class CorsairLinkConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PollMs = 2000;
    private const int MaxConsecutiveFailures = 3;
    // Each failed init flips the hub to software mode and back; repeated failures back off to this.
    private const int MaxInitRetryMs = 60_000;
    // Longer than iCUE's telemetry read interval.
    private const int ListenMs = 2500;
    // Longer than iCUE's keep-alive ping interval to its Bragi devices.
    private const long QuietAfterOtherHostMs = 60_000;
    // A slotless or empty chain re-detects once it has stayed that way this long.
    private const long BrokenBeforeRedetectMs = 10_000;
    // Re-detection re-powers the whole chain, so a device that never maps cannot cycle it continuously.
    private const long AutoRedetectCooldownMs = 10 * 60_000;
    // A replacement hub takes the unqualified ids only after the recorded one stays
    // missing this long, so a second hub enumerating first at boot cannot claim them.
    private const long AdoptReplacementAfterMs = 30_000;

    private readonly IHidEnumerator _hid;
    private readonly CorsairLinkHubs _hubs;
    private readonly CorsairLinkLightingDeviceProvider _lighting;
    private readonly CorsairLinkCoolingProvider _cooling;
    private readonly CorsairLinkLcd _lcd;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly IConflictDetector? _conflicts;
    // One pump LCD is supported; hub sessions race to attach it.
    private readonly object _lcdLock = new();
    // Keyed by hub, so a session restarted after a USB drop keeps the cooldown and the failed-fix memory.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AutoRedetect> _redetectState = new(StringComparer.Ordinal);
    private long? _primaryMissingSinceMs;

    public CorsairLinkConnectionWorker(
        IHidEnumerator hid,
        CorsairLinkHubs hubs,
        CorsairLinkLightingDeviceProvider lighting,
        CorsairLinkCoolingProvider cooling,
        CorsairLinkLcd lcd,
        DeviceControlGate gate,
        HardwarePresence presence,
        IConfigStore store,
        IConflictDetector? conflicts = null)
    {
        _hid = hid;
        _hubs = hubs;
        _lighting = lighting;
        _cooling = cooling;
        _lcd = lcd;
        _gate = gate;
        _presence = presence;
        _store = store;
        _conflicts = conflicts;
    }

    internal readonly record struct PresentHub(string Key, string Path, bool HasSerial);

    // A running vendor app takes the hub once Nexus lets go, paused or not;
    // switching the hub to hardware mode would cut that app off.
    private bool HandBack() =>
        _gate.PausedByApp("corsair") is null
        && !(DeviceControlPolicy.ConflictAppFor("corsair") is { } app && _conflicts?.IsAppRunning(app) == true);

    private static string Tag(CorsairLinkHub hub) => hub.Number == 1 ? "[corsair]" : $"[corsair hub {hub.Number}]";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first listen blocks; yield so it does not hold up host startup.
        await Task.Yield();
        var sessions = new Dictionary<string, Task>(StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ended = new List<string>();
                foreach (var (key, task) in sessions) if (task.IsCompleted) ended.Add(key);
                foreach (var key in ended) sessions.Remove(key);

                if (_gate.IsEnabled("corsair")
                    && _presence.UsbPresent(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId))
                {
                    var present = FindHubs();
                    // Null while a sole unrecorded hub waits out AdoptReplacementAfterMs.
                    var primaryKey = present.Count > 0 ? ResolvePrimaryKey(present) : null;
                    if (primaryKey != null)
                    {
                        foreach (var p in present)
                        {
                            if (sessions.ContainsKey(p.Key)) continue;
                            // Registered before the session starts so the next hub's number skips it.
                            var hub = CreateHub(p, primaryKey);
                            _hubs.Add(hub);
                            var redetect = _redetectState.GetOrAdd(p.Key, _ => new AutoRedetect());
                            sessions[p.Key] = Task.Run(() => RunHubAsync(hub, p.Path, redetect, stoppingToken), stoppingToken);
                        }
                    }
                }
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        try { await Task.WhenAll(sessions.Values).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private string? ResolvePrimaryKey(IReadOnlyList<PresentHub> present)
    {
        var keys = new List<string>(present.Count);
        foreach (var p in present) keys.Add(p.Key);
        var stored = _store.Load().Devices.Corsair.PrimaryHubKey;
        var recordedMissing = !string.IsNullOrEmpty(stored) && !keys.Contains(stored);
        var now = Environment.TickCount64;
        _primaryMissingSinceMs = recordedMissing ? _primaryMissingSinceMs ?? now : null;
        var adoptSole = _hubs.All.Count == 0 && now - (_primaryMissingSinceMs ?? now) >= AdoptReplacementAfterMs;
        var pick = PickPrimaryKey(stored, keys, adoptSole);
        if (pick != null && pick != stored) _store.Update(s => s.Devices.Corsair.PrimaryHubKey = pick);
        return pick;
    }

    /// <summary>
    /// The hub keeping the unqualified ids: the recorded one when present, else the
    /// lowest key the first time. When the recorded hub is gone a sole hub takes over
    /// once <paramref name="adoptSole"/> allows (a replaced hub keeps the user's
    /// layouts and curves); null means wait. With several hubs and the recorded one
    /// gone, none holds the unqualified ids.
    /// </summary>
    internal static string? PickPrimaryKey(string? stored, IReadOnlyList<string> present, bool adoptSole)
    {
        foreach (var k in present) if (k == stored) return stored;
        if (string.IsNullOrEmpty(stored))
        {
            var pick = present[0];
            foreach (var k in present) if (string.CompareOrdinal(k, pick) < 0) pick = k;
            return pick;
        }
        if (present.Count > 1) return stored;
        return adoptSole ? present[0] : null;
    }

    internal CorsairLinkHub CreateHub(PresentHub hub, string primaryKey)
    {
        if (hub.Key == primaryKey) return new CorsairLinkHub();
        var used = new HashSet<int>();
        foreach (var h in _hubs.All) used.Add(h.Number);
        var number = 2;
        while (used.Contains(number)) number++;
        return new CorsairLinkHub($"corsair:{ShortKey(hub.Key, hub.HasSerial)}:", number);
    }

    // The USB serial's tail; a hub without one is keyed by a hash of its HID path.
    internal static string ShortKey(string key, bool isSerial)
    {
        if (isSerial)
        {
            var alnum = new System.Text.StringBuilder(key.Length);
            foreach (var c in key) if (char.IsAsciiLetterOrDigit(c)) alnum.Append(char.ToUpperInvariant(c));
            if (alnum.Length >= 8) return alnum.ToString(alnum.Length - 8, 8);
        }
        uint hash = 2166136261;
        foreach (var c in key) hash = (hash ^ c) * 16777619;
        return hash.ToString("X8");
    }

    private List<PresentHub> FindHubs()
    {
        var result = new List<PresentHub>();
        foreach (var info in _hid.Find(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId))
        {
            if (info.UsagePage != CorsairLinkProtocol.VendorUsagePage || info.Usage != CorsairLinkProtocol.VendorUsage) continue;
            var hasSerial = !string.IsNullOrEmpty(info.Serial);
            result.Add(new PresentHub(hasSerial ? info.Serial! : info.Path, info.Path, hasSerial));
        }
        return result;
    }

    private bool StillPresent(string path)
    {
        foreach (var p in FindHubs()) if (p.Path == path) return true;
        return false;
    }

    private async Task RunHubAsync(CorsairLinkHub hub, string path, AutoRedetect redetect, CancellationToken stoppingToken)
    {
        var ownsLcd = false;
        try
        {
            var initRetryMs = ConnectPollMs;
            // When another program last answered on the hub; null once Nexus holds it again.
            long? otherHostHeardMs = null;
            while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("corsair"))
            {
                // Unplugged: the session ends and a new one starts when the hub returns.
                if (!StillPresent(path)) return;
                // forInput: interrupt-IN reads (the hub answers every write with a report).
                var device = _hid.Open(path, forInput: true);
                if (device == null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var listenStartMs = Environment.TickCount64;
                if (CorsairLinkHub.HearsAnotherHost(device, ListenMs))
                {
                    device.Dispose();
                    if (otherHostHeardMs is null)
                    {
                        ServiceLog.Info($"{Tag(hub)} another program is driving the hub; waiting for it to stop");
                    }
                    otherHostHeardMs = Environment.TickCount64;
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                // Listens back to back until the hub has been silent long enough: a ping
                // falling between listens would otherwise go unheard.
                if (otherHostHeardMs is { } heard && Environment.TickCount64 - heard < QuietAfterOtherHostMs)
                {
                    device.Dispose();
                    // A failed read returns before the listen window ends; pace that path.
                    if (Environment.TickCount64 - listenStartMs < ListenMs)
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                    continue;
                }

                hub.Attach(device);

                if (!hub.Initialize())
                {
                    if (hub.ForeignHostSeen)
                    {
                        if (otherHostHeardMs is null)
                        {
                            ServiceLog.Warn($"{Tag(hub)} another program answered during initialize; releasing the hub");
                        }
                        otherHostHeardMs = Environment.TickCount64;
                        hub.Detach(handBack: false);
                        continue;
                    }
                    // Detach clears Firmware, so read it before. Empty means the
                    // firmware read itself never landed a full reply - the hub went
                    // silent (or answered short) before the device-list read.
                    var fw = hub.State.Firmware;
                    ServiceLog.Warn($"{Tag(hub)} initialize failed, retrying in {initRetryMs / 1000}s (fw={(string.IsNullOrEmpty(fw) ? "none" : fw)})");
                    hub.Detach(handBack: HandBack());
                    await Task.Delay(initRetryMs, stoppingToken).ConfigureAwait(false);
                    initRetryMs = Math.Min(initRetryMs * 2, MaxInitRetryMs);
                    continue;
                }
                initRetryMs = ConnectPollMs;
                otherHostHeardMs = null;

                ServiceLog.Info($"{Tag(hub)} connected fw={hub.State.Firmware} devices={hub.State.Devices.Count}");
                if (hub.State.HasLcd)
                {
                    lock (_lcdLock)
                    {
                        if (!_lcd.HasDevice)
                        {
                            _lcd.DiscoverAndAttach(hub.State.Devices, _hid);
                            ownsLcd = _lcd.HasDevice;
                        }
                    }
                }
                _lighting.OnHubStateUpdated();
                _cooling.ReassertControl(hub);
                try
                {
                    await PollConnectedAsync(hub, redetect, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    if (hub.ForeignHostSeen)
                    {
                        ServiceLog.Warn($"{Tag(hub)} another program answered on the hub; stopped writing to it");
                        otherHostHeardMs = Environment.TickCount64;
                    }
                    var handBack = HandBack() && !hub.ForeignHostSeen;
                    if (ownsLcd)
                    {
                        lock (_lcdLock) _lcd.Detach(handBack);
                        ownsLcd = false;
                    }
                    hub.Detach(handBack);
                    ServiceLog.Info($"{Tag(hub)} disconnected");
                    _lighting.OnHubStateUpdated();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"{Tag(hub)} session error: {ex.Message}");
        }
        finally
        {
            // An exception can end the session between attach and the poll loop's own cleanup.
            try
            {
                var handBack = HandBack() && !hub.ForeignHostSeen;
                if (ownsLcd)
                {
                    lock (_lcdLock) _lcd.Detach(handBack);
                }
                hub.Detach(handBack);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"{Tag(hub)} cleanup failed: {ex.Message}");
            }
            _hubs.Remove(hub);
            _lighting.OnHubStateUpdated();
        }
    }

    private sealed class AutoRedetect
    {
        public long? BrokenSinceMs;
        public long? LastMs;
        // The broken channel set a re-detect already failed to map; never auto-retried.
        public string? Unfixed;
    }

    private static string BrokenSignature(CorsairLinkHub hub) =>
        hub.State.Devices.Count == 0 ? "empty" : string.Join(',', hub.State.UnmappedChannels);

    private async Task PollConnectedAsync(CorsairLinkHub hub, AutoRedetect redetect, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("corsair") && !hub.ForeignHostSeen)
        {
            var now = Environment.TickCount64;
            var broken = hub.State.Devices.Count == 0 || hub.State.UnmappedChannels.Count > 0;
            redetect.BrokenSinceMs = broken ? redetect.BrokenSinceMs ?? now : null;
            var auto = redetect.BrokenSinceMs is { } since && now - since >= BrokenBeforeRedetectMs
                && BrokenSignature(hub) != redetect.Unfixed
                && (redetect.LastMs is not { } last || now - last >= AutoRedetectCooldownMs);
            if (hub.RedetectRequested || auto)
            {
                var reason = auto
                    ? hub.State.Devices.Count == 0 ? "empty chain" : $"no slot for channel(s) {BrokenSignature(hub)}"
                    : "requested";
                if (auto) redetect.LastMs = now;
                ServiceLog.Info($"{Tag(hub)} re-detecting the chain ({reason})");
                var ok = await hub.RedetectAsync(stoppingToken).ConfigureAwait(false);
                var line = $"{Tag(hub)} re-detect {(ok ? "done" : "failed")}: devices={hub.State.Devices.Count} unmapped={hub.State.UnmappedChannels.Count}";
                if (ok) ServiceLog.Info(line); else ServiceLog.Warn(line);
                var stillBroken = hub.State.Devices.Count == 0 || hub.State.UnmappedChannels.Count > 0;
                redetect.Unfixed = stillBroken ? BrokenSignature(hub) : null;
                redetect.BrokenSinceMs = null;
                failures = 0;
                _cooling.ReassertControl(hub);
            }
            else if (hub.Poll())
            {
                failures = 0;
                _cooling.ReassertControl(hub);
            }
            else
            {
                failures++;
                if (failures >= MaxConsecutiveFailures) break;
            }
            _lighting.OnHubStateUpdated();
            await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
        }
    }
}
