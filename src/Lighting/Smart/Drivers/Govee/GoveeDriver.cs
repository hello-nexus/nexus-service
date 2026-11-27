using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

/// <summary>
/// Govee driver (Wi-Fi devices with "LAN Control" enabled in the Govee Home
/// app - there is no token pairing). Discovery via multicast scan, control via
/// UDP JSON, realtime per-segment streaming via the binary razer/DreamView
/// command on RGBIC models. The LAN API exposes no capability metadata, so
/// the realtime segment count comes from a per-SKU table captured at pair
/// time and degrades to averaged single-color for unknown bulb-class models.
/// </summary>
public sealed class GoveeDriver : ILightDriver, IDisposable
{
    // DreamView streaming cadence; the strips' own LED refresh is ~20 ms, so
    // pushing faster than ~30 fps buys nothing and risks flooding.
    private const int RazerIntervalMs = 33;
    private const int ControlIntervalMs = 100;
    private const int ProbeTimeoutMs = 2000;
    private const int ScanTimeoutMs = 2500;
    private const int PingTimeoutMs = 1500;
    // Must stay well under the strip's own realtime timeout so a
    // paused stream re-enters before the strip has already left.
    private const int RazerFrameGapMs = 30_000;
    // Driver-owned probe while armed; the provider's reachability poll runs only with a dashboard open.
    private const int RazerProbeMs = 10_000;
    // Extra enables right after entry cover a lost or ignored one; each resets the strip's frame buffer, so never later.
    private const int RazerConfirmCount = 2;
    private const int RazerConfirmGapMs = 1_000;
    private const int RazerMissesToDisarm = 2;

    // Bulb / single-zone models on the official LAN list (H60xx lamp class):
    // control-only, averaged to one color.
    private static readonly HashSet<string> SingleColorSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        "H6002", "H6003", "H6004", "H6006", "H6008", "H6009", "H600A", "H600D", "H6010", "H6011", "H601A",
    };

    // Per-IC color counts for razer frames on models where the count is known;
    // everything else uses the default below (matches OpenRGB's behavior for
    // unknown SKUs). Refine as hardware feedback comes in.
    private static readonly Dictionary<string, int> RazerSegmentsBySku = new(StringComparer.OrdinalIgnoreCase)
    {
        ["H619A"] = 20, ["H619B"] = 20, ["H619C"] = 20, ["H619D"] = 20, ["H619E"] = 20, ["H619Z"] = 20,
        ["H618A"] = 20, ["H618C"] = 20, ["H618E"] = 20, ["H618F"] = 20,
    };
    private const int DefaultRazerSegments = 20;

    private readonly GoveeLanClient _client;
    // Parsed Extra payloads, keyed by device id, invalidated on change.
    private readonly ConcurrentDictionary<string, (string Raw, GoveeExtra Parsed)> _extras = new();
    // Devices in razer (realtime) mode. The enable is sent on entry plus a short
    // bounded confirm, never on a timer: every enable resets the strip's
    // frame buffer and shows as a black flash. Entry state is cleared by a static
    // frame, a frame gap, or repeated status misses / an off status (power cycle,
    // Wi-Fi drop), so the next zone frame re-enters with the full turn-on,
    // brightness, enable sequence.
    private readonly ConcurrentDictionary<string, RazerEntry> _razerEntries = new();

    // One instance per entry into realtime mode. Clears compare the instance, so a
    // stale probe or ping result cannot remove an entry armed after it started.
    private sealed class RazerEntry
    {
        public long LastFrameAt;
        public readonly CancellationTokenSource Stop = new();
        public long NextConfirmAt;
        public int ConfirmsLeft = RazerConfirmCount;
        public int Misses;
    }

    private readonly int _scanTimeoutMs;
    private readonly int _probeTimeoutMs;
    private readonly int _pingTimeoutMs;
    private readonly int _frameGapMs;
    private readonly int _probeMs;
    private readonly int _confirmGapMs;

    public GoveeDriver(GoveeLanClient client)
        : this(client, ScanTimeoutMs, ProbeTimeoutMs, PingTimeoutMs) { }

    /// <summary>Timeout-overriding ctor for tests. The LAN API has no negative
    /// reply - "not reachable" is only ever a timeout - so the discovery and
    /// pair-failure paths cost the full window on every run.</summary>
    internal GoveeDriver(GoveeLanClient client, int scanTimeoutMs, int probeTimeoutMs, int pingTimeoutMs,
        int frameGapMs = RazerFrameGapMs, int probeMs = RazerProbeMs, int confirmGapMs = RazerConfirmGapMs)
    {
        _frameGapMs = frameGapMs;
        _probeMs = probeMs;
        _confirmGapMs = confirmGapMs;
        _client = client;
        _scanTimeoutMs = scanTimeoutMs;
        _probeTimeoutMs = probeTimeoutMs;
        _pingTimeoutMs = pingTimeoutMs;
    }

    public string Brand => "govee";

    public int MinIntervalMs(SmartLight dev) => GetExtra(dev)?.Razer == true ? RazerIntervalMs : ControlIntervalMs;

    // Each Govee device is its own controller.
    public string RateLimitKey(SmartLight dev) => dev.Id;

    public async Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
    {
        var found = await _client.ScanAsync(_scanTimeoutMs, ct).ConfigureAwait(false);
        var result = new List<DiscoveredLight>(found.Count);
        foreach (var d in found)
            result.Add(new DiscoveredLight(Brand, d.Ip, DisplayName(d.Sku), d.Device));
        return result;
    }

    public async Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
    {
        // The streaming/control paths need an IPv4 literal (raw UDP, no DNS
        // per frame) - resolve a user-typed hostname once, here.
        var host = await LanHost.ResolveIpv4Async(target.Host, ct).ConfigureAwait(false);
        if (host is null)
            return new PairResult { Ok = false, Error = "host-not-found" };

        // No token exchange - "pairing" verifies the device actually answers on
        // the LAN (i.e. LAN Control is on) and captures its SKU capabilities.
        GoveeDeviceInfo? info = null;
        try { info = await _client.ProbeAsync(host, _probeTimeoutMs, ct).ConfigureAwait(false); }
        catch { /* fall through to the status probe */ }

        string sku;
        string device;
        if (info is not null)
        {
            sku = info.Sku;
            device = info.Device;
        }
        else
        {
            // Unicast scan filtered? A devStatus answer still proves LAN
            // Control is enabled; capabilities fall back to the SKU-less default.
            var status = await _client.StatusAsync(host, _probeTimeoutMs, ct).ConfigureAwait(false);
            if (status is null)
                return new PairResult { Ok = false, Error = "lan-control" };
            sku = "";
            device = target.StableKey is { Length: > 0 } k ? k : host;
        }

        var extra = new GoveeExtra
        {
            Sku = sku,
            // Unknown SKU (status-only pairing) defaults to control-only: a
            // bulb fed razer packets it ignores would show dead effects with
            // no fallback, while a strip on colorwc still works (averaged).
            Razer = sku.Length > 0 && !SingleColorSkus.Contains(sku),
            Segments = RazerSegmentsBySku.TryGetValue(sku, out var n) ? n : DefaultRazerSegments,
        };

        return new PairResult
        {
            Ok = true,
            Devices = new List<SmartLightConfig>
            {
                new()
                {
                    Id = $"govee:{device}",
                    Brand = Brand,
                    Name = target.Name is { Length: > 0 } && target.Name != Brand ? target.Name : DisplayName(sku),
                    Host = host,
                    StableKey = device,
                    Token = "",
                    Extra = JsonSerializer.Serialize(extra, GoveeJsonContext.Default.GoveeExtra),
                    Enabled = true,
                },
            },
        };
    }

    private static string DisplayName(string sku) => sku is { Length: > 0 } ? $"Govee {sku}" : "Govee";

    public LightFramePlan PlanFrames(SmartLight dev)
    {
        var extra = GetExtra(dev);
        if (extra?.Razer != true)
            return new LightFramePlan(16, AverageToSingle: true);
        // Strip ICs lie on a line: sample the canvas left→right at mid-height so
        // effects/screen-mirror map along the strip instead of a square grid.
        var n = Math.Max(1, extra.Segments);
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++) { u[i] = n > 1 ? (float)i / (n - 1) : 0.5f; v[i] = 0.5f; }
        // The realtime ("razer"/DreamView) mode drops a manual color ~60s without
        // frames, so a static color must be streamed, not sent once.
        return new LightFramePlan(n, AverageToSingle: false, u, v, StaticNeedsStreaming: true);
    }

    public async Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
    {
        var extra = GetExtra(dev);

        if (frame.Zones is { } zones && frame.On && extra?.Razer == true)
        {
            var now = Environment.TickCount64;
            if (_razerEntries.TryGetValue(dev.Id, out var entry) && now - Volatile.Read(ref entry.LastFrameAt) > _frameGapMs)
            {
                // Paused, disabled or asleep long enough for the strip to have left
                // realtime mode on its own.
                Disarm(dev.Id, entry);
                entry = null;
            }
            if (entry is null)
            {
                // Cold entry (first frame, or after a power-off turned the strip
                // off). Firmware scales razer output by power+brightness, so set
                // those first. The strip ignores a razer enable that arrives while
                // its Wi-Fi/firmware is still waking from the power-on: bench shows
                // ~300ms gaps after turn-on let the enable take, while sending them
                // back-to-back leaves it stuck in its built-in scene.
                await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                await _client.BrightnessAsync(dev.Host, 100, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: true), ct).ConfigureAwait(false);
                entry = new RazerEntry { NextConfirmAt = Environment.TickCount64 + _confirmGapMs };
                _razerEntries[dev.Id] = entry;
                _ = ProbeWhileArmedAsync(dev, entry);
            }
            else if (entry.ConfirmsLeft > 0 && now >= entry.NextConfirmAt)
            {
                entry.ConfirmsLeft--;
                entry.NextConfirmAt = now + _confirmGapMs;
                await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: true), ct).ConfigureAwait(false);
            }
            Volatile.Write(ref entry.LastFrameAt, now);
            var count = Math.Min(zones.Length / 3, Math.Max(1, extra.Segments));
            await _client.RazerAsync(dev.Host,
                GoveePackets.RazerFrameBase64(zones, count, frame.Brightness01, gradient: true), ct).ConfigureAwait(false);
            return;
        }

        // Static path - leave realtime mode first so colorwc shows again.
        if (_razerEntries.TryRemove(dev.Id, out var left))
        {
            left.Stop.Cancel();
            await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: false), ct).ConfigureAwait(false);
        }

        if (!frame.On)
        {
            await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
            return;
        }
        await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
        await _client.BrightnessAsync(dev.Host,
            Math.Clamp((int)Math.Round(frame.Brightness01 * 100), 1, 100), ct).ConfigureAwait(false);
        await _client.ColorAsync(dev.Host, frame.R, frame.G, frame.B, kelvin: 0, ct).ConfigureAwait(false);
    }

    public async Task IdentifyAsync(SmartLight dev, CancellationToken ct)
    {
        // No identify command in the LAN API - blink by toggling power, then
        // restore the state the device reported before the blink.
        var status = await _client.StatusAsync(dev.Host, 1500, ct).ConfigureAwait(false);
        var wasOn = status?.OnOff != 0;
        for (var i = 0; i < 2; i++)
        {
            await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        if (!wasOn) await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(SmartLight dev, CancellationToken ct)
    {
        _razerEntries.TryGetValue(dev.Id, out var entry);
        return await ObserveStatusAsync(dev, entry, _pingTimeoutMs, ct).ConfigureAwait(false);
    }

    // One status query against the entry it started with (never a newer one). A
    // reply saying the strip is off, or any reply after a miss (a reboot shorter
    // than the probe spacing), or two consecutive misses disarm it.
    private async Task<bool> ObserveStatusAsync(SmartLight dev, RazerEntry? entry, int timeoutMs, CancellationToken ct)
    {
        GoveeReplyData? reply;
        try { reply = await _client.StatusAsync(dev.Host, timeoutMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { reply = null; }
        if (entry is not null)
        {
            if (reply is null)
            {
                if (Interlocked.Increment(ref entry.Misses) >= RazerMissesToDisarm)
                    Disarm(dev.Id, entry);
            }
            else if (reply.OnOff == 0 || Interlocked.Exchange(ref entry.Misses, 0) > 0)
            {
                Disarm(dev.Id, entry);
            }
        }
        return reply is not null;
    }

    private void Disarm(string id, RazerEntry entry)
    {
        if (((ICollection<KeyValuePair<string, RazerEntry>>)_razerEntries).Remove(new(id, entry)))
            entry.Stop.Cancel();
    }

    private bool IsCurrent(string id, RazerEntry entry)
        => _razerEntries.TryGetValue(id, out var cur) && ReferenceEquals(cur, entry);

    private async Task ProbeWhileArmedAsync(SmartLight dev, RazerEntry entry)
    {
        var ct = entry.Stop.Token;
        try
        {
            while (IsCurrent(dev.Id, entry))
            {
                await Task.Delay(_probeMs, ct).ConfigureAwait(false);
                if (!IsCurrent(dev.Id, entry)) return;
                // Frames stopped without a static frame: the gap rule owns re-entry.
                if (Environment.TickCount64 - Volatile.Read(ref entry.LastFrameAt) > _frameGapMs)
                {
                    Disarm(dev.Id, entry);
                    return;
                }
                await ObserveStatusAsync(dev, entry, _pingTimeoutMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { Disarm(dev.Id, entry); }
    }

    public void Dispose()
    {
        foreach (var kv in _razerEntries)
            Disarm(kv.Key, kv.Value);
    }

    private GoveeExtra? GetExtra(SmartLight dev)
    {
        if (string.IsNullOrEmpty(dev.Extra)) return null;
        if (_extras.TryGetValue(dev.Id, out var cached) && cached.Raw == dev.Extra) return cached.Parsed;
        try
        {
            var parsed = JsonSerializer.Deserialize(dev.Extra, GoveeJsonContext.Default.GoveeExtra);
            if (parsed is null) return null;
            _extras[dev.Id] = (dev.Extra, parsed);
            return parsed;
        }
        catch { return null; }
    }
}
