using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// USB liquid-cooler / AIO / fan-hub provider backed by the
/// <a href="https://github.com/liquidctl/liquidctl">liquidctl</a> CLI - the same
/// universal driver library CoolerControl shells out to. Covers NZXT Kraken,
/// Corsair Commander/AIOs, EVGA CLC, Aquacomputer, and the rest of liquidctl's
/// device list without Nexus reimplementing each USB protocol. Reads via
/// <c>liquidctl --json status</c>; controls via <c>liquidctl --address … set
/// &lt;channel&gt; speed &lt;duty&gt;</c>.
///
/// Subprocess-based (AOT-safe, no native deps) through <see cref="ShellExecutor"/>.
/// When liquidctl isn't installed every method degrades to empty - the provider
/// is inert, never throws. Channel ids are <c>liquidctl:&lt;addr&gt;:&lt;channel&gt;</c>;
/// the composite routes by that prefix. These are USB coolers with no BIOS
/// fallback, so <see cref="ReleaseFan"/> is a no-op and calibration is skipped.
/// </summary>
public sealed class LinuxLiquidctlProvider : IFanControlProvider, ICoolingProvider
{
    public const string IdPrefix = "liquidctl:";

    private readonly Func<string> _listJson;
    private readonly Func<string> _statusAllJson;
    private readonly Func<LiquidctlListEntry, string> _statusOneJson;
    private readonly Func<bool> _bridgeActive;
    private readonly Func<long> _nowMs;
    private readonly Func<string, string, int, bool> _setSpeed; // (address, channel, duty) -> applied?
    private readonly bool _forceAvailable;
    private readonly IConfigStore? _config;
    private readonly Func<string?> _nativeHidPath;

    private readonly object _lock = new();
    // Rebuilt wholesale and published atomically (assigned, never mutated in
    // place) so a concurrent Drive() never observes a half-cleared map.
    private Dictionary<string, (string Address, string Channel)> _control = new();
    private readonly HashSet<string> _warned = new();
    private bool? _present;

    // Each status read is a subprocess spawn; one curve tick reads fans + temps.
    // Cache the parse briefly so a tick spawns it once.
    private const long SnapshotTtlMs = 1500;
    // Enumeration only; hotplug is rare.
    private const long ListTtlMs = 10000;
    private const int MaxParallelReads = 4;
    private List<LiquidDevice>? _snapshot;
    private long _snapshotAtMs = long.MinValue;
    private LiquidctlOwnership.Policy _snapshotPolicy;
    // address -> entry as of the latest refresh, so Drive can tell a withheld address without waiting on a refresh.
    private Dictionary<string, LiquidctlListEntry> _known = new(StringComparer.Ordinal);

    // Held across a refresh's subprocesses so _lock stays free for Drive(); guards the fields below.
    private readonly object _refreshLock = new();
    private readonly HashSet<string> _skipLogged = new(StringComparer.Ordinal);
    private List<LiquidctlListEntry>? _list;
    private long _listAtMs = long.MinValue;

    public LinuxLiquidctlProvider(IConfigStore config, RgbBridge bridge, KrakenHub krakenHub)
    {
        _config = config;
        _nativeHidPath = () => krakenHub.ClaimedHidPath;
        _listJson = () => ShellExecutor.Run("liquidctl", 8000, "--json", "list");
        _statusAllJson = () => ShellExecutor.Run("liquidctl", 8000, "--json", "status");
        _statusOneJson = e => ShellExecutor.Run("liquidctl", 8000, LiquidctlOwnership.StatusArgs(e));
        _nowMs = () => Environment.TickCount64;
        _bridgeActive = () => bridge.IsActive;
        // Verify the write landed (exit 0) instead of assuming it - a rejected
        // `set` (permissions, wrong channel) must not be reported as applied.
        // NOTE: a few liquidctl devices need `liquidctl initialize` once per
        // boot before `set` is accepted; omitted here because it mutates device
        // state and can't be verified on this bench - revisit with hardware.
        _setSpeed = (addr, chan, duty) => ShellExecutor.RunExit("liquidctl", 8000,
            "--address", addr, "set", chan, "speed", duty.ToString(CultureInfo.InvariantCulture)) == 0;
        _forceAvailable = false;
    }

    // Injected command seams for tests (no real liquidctl needed).
    // The all-devices statusJson doubles as the list; per-device reads return it filtered by address.
    internal LinuxLiquidctlProvider(Func<string> statusJson, Func<string, string, int, bool> setSpeed,
        IConfigStore? config = null, Func<bool>? bridgeActive = null, Func<long>? nowMs = null, Func<string?>? nativeHidPath = null)
        : this(statusJson, statusJson, e => statusJson(), setSpeed, config, bridgeActive ?? (() => false), nowMs, nativeHidPath)
    {
    }

    internal LinuxLiquidctlProvider(Func<string> listJson, Func<string> statusAllJson,
        Func<LiquidctlListEntry, string> statusOneJson, Func<string, string, int, bool> setSpeed,
        IConfigStore? config, Func<bool> bridgeActive, Func<long>? nowMs = null, Func<string?>? nativeHidPath = null)
    {
        _nativeHidPath = nativeHidPath ?? (() => null);
        _listJson = listJson;
        _statusAllJson = statusAllJson;
        _statusOneJson = statusOneJson;
        _bridgeActive = bridgeActive;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _setSpeed = setSpeed;
        _forceAvailable = true;
        _config = config;
    }

    private bool Available()
    {
        if (_forceAvailable)
            return true;
        if (!OperatingSystem.IsLinux())
            return false;
        // Cache only a positive probe - liquidctl may be installed after us.
        if (_present == true)
            return true;
        var ok = !string.IsNullOrWhiteSpace(ShellExecutor.Run("liquidctl", "--version"));
        if (ok) _present = true;
        return ok;
    }

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var devices = Snapshot();
        var channels = new List<FanChannel>();
        var control = new Dictionary<string, (string Address, string Channel)>();
        var curveBound = CurveModes.BoundFanIds(_config);
        foreach (var dev in devices)
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            foreach (var ch in dev.Channels)
            {
                var id = $"{deviceId}:{ch.Name}";
                control[id] = (dev.Address, ch.Name);
                channels.Add(new FanChannel
                {
                    Id = id,
                    Name = $"{dev.Description} {ch.Name}",
                    DutyPercent = ch.Duty ?? 0,
                    Rpm = ch.Rpm ?? 0,
                    Mode = curveBound.Contains(id)
                        ? FanModes.Curve
                        : (ch.Duty is not null ? FanModes.Manual : FanModes.Auto),
                    DeviceId = deviceId,
                    DeviceName = dev.Description,
                    PortLabel = ch.Name,
                });
            }
        }
        lock (_lock) _control = control; // atomic publish - no half-built map
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var devices = Snapshot();
        var sources = new List<TemperatureSource>();
        foreach (var dev in devices)
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            foreach (var t in dev.Temps)
            {
                sources.Add(new TemperatureSource
                {
                    // Keyed by sanitized label, not enumeration index, so a saved
                    // curve binding doesn't silently retarget if the driver
                    // reorders/adds a temp row.
                    Id = TempId(deviceId, t.Label),
                    Name = $"{dev.Description} {t.Label}",
                    Category = "Hub",
                    Value = t.Celsius,
                    DeviceId = deviceId,
                });
            }
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        // Re-snapshot and match by the label-derived id. Cheap enough; AIO temps
        // are read on the curve-engine tick which is already throttled.
        foreach (var dev in Snapshot())
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            foreach (var t in dev.Temps)
            {
                if (sensorId == TempId(deviceId, t.Label))
                    return t.Celsius;
            }
        }
        return null;
    }

    private static string TempId(string deviceId, string label) => $"{deviceId}:t:{Sanitize(label)}";

    public int SetFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent, persist: true);

    public void DriveFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent, persist: false);

    private int Drive(string channelId, int dutyPercent, bool persist)
    {
        dutyPercent = Math.Clamp(dutyPercent, 0, 100);
        Dictionary<string, (string Address, string Channel)> control;
        lock (_lock) control = _control;
        if (!control.TryGetValue(channelId, out var target))
        {
            // Cold cache (process restart): rebuild the (atomically published) map.
            GetFanChannels();
            lock (_lock) control = _control;
            if (!control.TryGetValue(channelId, out target))
                return dutyPercent;
        }
        if (_nativeHidPath() is { } owned && target.Address == owned)
            return dutyPercent;
        if (IsWithheldAddress(target.Address))
        {
            WarnWithheldOnce(target.Address);
            return dutyPercent;
        }
        if (_setSpeed(target.Address, target.Channel, dutyPercent))
        {
            // Record an explicit user override so it survives a restart and feeds
            // preset-derivation, mirroring WindowsFanControlProvider. The curve
            // engine's per-tick DriveFanSpeed passes persist:false so it doesn't
            // pollute the override dict.
            if (persist)
                _config?.Update(s => s.Cooling.ManualSpeeds[channelId] = dutyPercent);
        }
        else
        {
            WarnOnce(channelId);
        }
        return dutyPercent;
    }

    private bool IsWithheldAddress(string address)
    {
        var policy = CurrentPolicy();
        if (!policy.WithholdsAnything)
            return false;
        LiquidctlListEntry? entry;
        lock (_lock)
        {
            _known.TryGetValue(address, out entry);
        }
        // An address no refresh has described is refused too: a failed list must not open the family.
        return entry is null || LiquidctlOwnership.IsWithheld(entry, policy);
    }

    private void WarnWithheldOnce(string address)
    {
        lock (_lock)
        {
            if (!_warned.Add("withheld:" + address))
                return;
        }
        ServiceLog.Info($"[liquidctl] not writing to {address}: OpenRGB drives Commander Core devices");
    }

    private void WarnOnce(string channelId)
    {
        lock (_lock)
        {
            if (!_warned.Add(channelId))
                return;
        }
        Console.Error.WriteLine(
            $"[cooling] liquidctl set failed for {channelId} - the cooler rejected the write " +
            "(check udev access to its hidraw node; some devices need `liquidctl initialize`).");
    }

    // USB AIOs have no BIOS/automatic mode to hand control back to - leave the
    // last applied duty in place rather than guess a "default".
    public void ReleaseFan(string channelId) { }
    public void ReleaseAll() { }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var devices = Snapshot();
        if (devices.Count == 0)
            return Array.Empty<CoolingComponent>();
        return devices.Select(dev => new CoolingComponent
        {
            Id = IdPrefix + Sanitize(dev.Address),
            Name = dev.Description,
            Type = "MiniHub",
            Devices = dev.Channels.Select(ch => new CoolingDevice
            {
                Id = $"{IdPrefix}{Sanitize(dev.Address)}:{ch.Name}",
                Name = ch.Name,
                Type = ch.Name.StartsWith("pump", StringComparison.Ordinal) ? "Pump" : "Fan",
                Speed = ch.Duty,
                Rpm = ch.Rpm,
                Pwm = ch.Duty,
            }).ToList(),
        }).ToList();
    }

    private List<LiquidDevice> Snapshot()
    {
        var devices = SnapshotAll();
        // The native driver owns this hidraw node; a second card would write to the same cooler.
        return _nativeHidPath() is { } owned
            ? devices.Where(d => d.Address != owned).ToList()
            : devices;
    }

    private List<LiquidDevice> SnapshotAll()
    {
        if (!Available())
            return new List<LiquidDevice>();
        lock (_refreshLock)
        {
            var now = _nowMs();
            var policy = CurrentPolicy();
            lock (_lock)
            {
                if (_snapshot is not null && policy == _snapshotPolicy && now - _snapshotAtMs < SnapshotTtlMs)
                    return _snapshot;
            }
            var fresh = ReadEligible(now, policy);
            var after = CurrentPolicy();
            if (after != policy)
            {
                // The policy changed during the subprocesses: that result may have opened a now-withheld device.
                policy = after;
                fresh = ReadEligible(now, policy);
            }
            lock (_lock)
            {
                _snapshot = fresh;
                _snapshotAtMs = now;
                _snapshotPolicy = policy;
            }
            return fresh;
        }
    }

    // The persisted exclusions can lead the daemon: a brief window exists after the user un-controls the device.
    private LiquidctlOwnership.Policy CurrentPolicy()
    {
        var active = _bridgeActive();
        if (!active)
            return new LiquidctlOwnership.Policy(false, false, false);
        var names = new List<string>();
        _config?.Read(s =>
        {
            foreach (var ex in s.Devices.OpenRgbDetectorExclusions.Values)
                names.Add(ex.DetectorName);
        });
        return LiquidctlOwnership.BuildPolicy(true, names);
    }

    // Caller holds _refreshLock. Withholding always reads per device: the all-devices read prints nothing if any one device errors.
    private List<LiquidDevice> ReadEligible(long now, LiquidctlOwnership.Policy policy)
    {
        if (!policy.WithholdsAnything)
        {
            _skipLogged.Clear();
            ClearWithheldWarnings(null);
            var all = ParseStatus(_statusAllJson());
            if (all.Count > 0)
                PublishKnown(all.Select(d => new LiquidctlListEntry(d.Address, null, null, d.Description)));
            return all;
        }

        var list = ReadList(now);
        if (list.Count == 0)
            return new List<LiquidDevice>();
        PublishKnown(list);

        var eligible = new List<LiquidctlListEntry>();
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (!LiquidctlOwnership.IsWithheld(entry, policy))
            {
                eligible.Add(entry);
                continue;
            }
            skipped.Add(entry.Address);
            if (_skipLogged.Add(entry.Address))
                ServiceLog.Info($"[liquidctl] not polling {entry.Address} ({entry.Description}): OpenRGB drives Commander Core devices");
        }
        _skipLogged.IntersectWith(skipped);
        ClearWithheldWarnings(skipped);
        return ReadPerDevice(eligible);
    }

    private void ClearWithheldWarnings(HashSet<string>? stillWithheld)
    {
        lock (_lock)
        {
            _warned.RemoveWhere(k => k.StartsWith("withheld:", StringComparison.Ordinal)
                && (stillWithheld is null || !stillWithheld.Contains(k.Substring("withheld:".Length))));
        }
    }

    private void PublishKnown(IEnumerable<LiquidctlListEntry> entries)
    {
        var map = new Dictionary<string, LiquidctlListEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
            map[e.Address] = e;
        lock (_lock) _known = map;
    }

    private List<LiquidDevice> ReadPerDevice(List<LiquidctlListEntry> eligible)
    {
        var results = new List<LiquidDevice>[eligible.Count];
        Parallel.For(0, eligible.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads }, i =>
        {
            var found = new List<LiquidDevice>();
            try
            {
                foreach (var d in ParseStatus(_statusOneJson(eligible[i])))
                {
                    if (string.Equals(d.Address, eligible[i].Address, StringComparison.Ordinal))
                        found.Add(d);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[liquidctl] status read for {eligible[i].Address} failed: {ex.Message}");
            }
            results[i] = found;
        });
        return results.SelectMany(r => r).ToList();
    }

    // Enumeration only. A failed or empty read is never cached.
    private List<LiquidctlListEntry> ReadList(long now)
    {
        if (_list is not null && now - _listAtMs < ListTtlMs)
            return _list;
        var parsed = LiquidctlOwnership.ParseList(_listJson());
        if (parsed.Count == 0)
        {
            _list = null;
            return parsed;
        }
        _list = parsed;
        _listAtMs = now;
        return parsed;
    }

    public static bool IsLiquidctlId(string id)
        => !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ── parsing (pure, unit-tested) ──

    internal sealed record LiquidDevice(string Address, string Description,
        List<LiquidChannel> Channels, List<LiquidTemp> Temps);
    internal sealed record LiquidChannel(string Name, int? Rpm, int? Duty);
    internal sealed record LiquidTemp(string Label, float Celsius);

    /// <summary>
    /// Parse <c>liquidctl --json status</c> output. Each device's flat status
    /// list is folded into named channels (fan / fanN / pump) by pairing the
    /// "… speed" (rpm) and "… duty" (%) rows, plus any "… temperature" rows.
    /// </summary>
    internal static List<LiquidDevice> ParseStatus(string json)
    {
        var result = new List<LiquidDevice>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return result; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var devEl in doc.RootElement.EnumerateArray())
            {
                var address = GetStr(devEl, "address");
                var description = GetStr(devEl, "description");
                if (string.IsNullOrEmpty(address))
                    continue;

                // Channel name -> (rpm, duty), preserving first-seen order.
                var chans = new List<string>();
                var rpm = new Dictionary<string, int>();
                var duty = new Dictionary<string, int>();
                var temps = new List<LiquidTemp>();

                if (devEl.TryGetProperty("status", out var statusEl)
                    && statusEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in statusEl.EnumerateArray())
                    {
                        var key = GetStr(row, "key");
                        if (string.IsNullOrEmpty(key) || !row.TryGetProperty("value", out var valEl))
                            continue;
                        if (!TryNumber(valEl, out var num))
                            continue;
                        var keyLower = key.ToLowerInvariant();

                        if (keyLower.Contains("temp"))
                        {
                            temps.Add(new LiquidTemp(key, (float)num));
                            continue;
                        }
                        var channel = ChannelName(keyLower);
                        if (channel is null)
                            continue;
                        if (!rpm.ContainsKey(channel) && !duty.ContainsKey(channel))
                            chans.Add(channel);
                        if (keyLower.Contains("duty"))
                            duty[channel] = (int)Math.Round(num);
                        else if (keyLower.Contains("speed") || keyLower.Contains("rpm"))
                            rpm[channel] = (int)Math.Round(num);
                    }
                }

                var channelList = chans
                    .Select(c => new LiquidChannel(c,
                        rpm.TryGetValue(c, out var r) ? r : null,
                        duty.TryGetValue(c, out var d) ? d : null))
                    .ToList();
                result.Add(new LiquidDevice(address, string.IsNullOrEmpty(description) ? address : description,
                    channelList, temps));
            }
        }
        return result;
    }

    // "Fan speed" -> "fan", "Fan 1 duty" -> "fan1", "Pump speed" -> "pump".
    // Returns null for non-fan/pump rows (voltages, firmware, etc.).
    private static string? ChannelName(string keyLower)
    {
        if (keyLower.Contains("pump"))
            return "pump";
        var fan = keyLower.IndexOf("fan", StringComparison.Ordinal);
        if (fan < 0)
            return null;
        var digits = new string(keyLower.Skip(fan + 3)
            .SkipWhile(c => c == ' ')
            .TakeWhile(char.IsDigit)
            .ToArray());
        return "fan" + digits;
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool TryNumber(JsonElement el, out double value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static string Sanitize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
