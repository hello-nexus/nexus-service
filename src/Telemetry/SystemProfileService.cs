using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Attaches a system profile to the anonymous telemetry person (PostHog
/// <c>$set</c>): the same hardware shown under System Specs (cpu / gpu / ram /
/// motherboard / storage / …) plus the recognized devices currently connected
/// - both the Nexus/HYTE devices we drive AND named USB peripherals we merely
/// identify, in a single <c>devices</c> list. No PII (no serials, no machine
/// name).
///
/// Lifecycle: warms up (30 s cadence) until the first ready snapshot lands -
/// device connection + USB enumeration aren't instant at boot - then settles to
/// a 15-min re-check. Each send is deduped: a <c>$identify</c> only goes out
/// when the profile actually changed, so unchanged refreshes cost zero events.
/// Gated by the same opt-out - nothing is gathered or sent while opted out.
/// </summary>
internal sealed class SystemProfileService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromMinutes(15);
    private const int MaxDevices = 80;
    private static readonly TimeSpan LayoutSnapshotInterval = TimeSpan.FromHours(24);
    private const int MaxWidgetTypes = 200;
    private const int MaxWidgetTypeLength = 132;

    private readonly ITelemetry _telemetry;
    private readonly IConfigStore _store;
    private readonly SystemSpecsCollector _specs;
    private readonly DeviceManager _devices;
    private readonly ILightingDeviceProvider _lighting;
    private readonly IFanControlProvider _fans;
    private string? _lastSignature;
    private DateTimeOffset _lastLayoutSnapshot = DateTimeOffset.MinValue;

    public SystemProfileService(ITelemetry telemetry, IConfigStore store,
        SystemSpecsCollector specs, DeviceManager devices,
        ILightingDeviceProvider lighting, IFanControlProvider fans)
    {
        _telemetry = telemetry;
        _store = store;
        _specs = specs;
        _devices = devices;
        _lighting = lighting;
        _fans = fans;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            bool settled;
            try { settled = await SendAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[profile] {ex.GetType().Name}: {ex.Message}");
                settled = false;
            }
            // Keep the fast cadence until a ready snapshot lands; then back off.
            delay = settled ? SteadyInterval : WarmupInterval;
        }
    }

    /// <returns>True once "settled" - a ready snapshot was processed, or the user
    /// opted out - so the caller can drop to the slow cadence. False means
    /// not-ready-yet; retry soon.</returns>
    private async Task<bool> SendAsync(CancellationToken ct)
    {
        if (!_store.Load().Telemetry.CollectAnonymousData)
            return true; // opted out - re-check slowly, don't gather.

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);

        // One list: handler-backed devices we drive (connected) ∪ any USB device
        // the OS names (recognized even if unsupported).
        var devices = _devices.GetAll()
            .Where(d => d.Connected)
            .Select(d => d.Name)
            .Concat(_devices.GetUsbDevices().Select(u => u.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Stable order so a reordered enumeration doesn't look like a change
            // (otherwise the dedupe re-sends $identify every refresh).
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDevices)
            .ToArray();

        // Devices + enumeration aren't ready instantly at boot. Don't send an
        // empty snapshot - wait for the next warmup tick.
        if (string.IsNullOrWhiteSpace(specs.Processor) && devices.Length == 0)
            return false;

        var props = new List<(string, object?)> { ("version", BuildInfo.Version), ("os", TelemetryPlatform.OsTag()) };
        Add(props, "os_build", specs.OsBuild);
        Add(props, "cpu", specs.Processor);
        Add(props, "gpu", specs.GraphicsCard);
        Add(props, "ram", specs.Memory);
        if (ParseRamGb(specs.Memory) is int ramGb) props.Add(("ram_amount", ramGb));
        Add(props, "motherboard", specs.Motherboard);
        Add(props, "storage", specs.Storage);
        if (ParseStorageGb(specs.Storage) is int storageGb) props.Add(("storage_amount", storageGb));
        Add(props, "monitor", specs.Monitor);
        Add(props, "network", specs.NetworkCard);
        Add(props, "sound", specs.SoundCard);
        props.Add(("devices", devices));

        var settings = _store.Load();
        // A provider can be mid-enumeration or absent on a headless box.
        // Telemetry must never be the thing that throws - but it must not
        // assert a wrong number either: person properties are last-write-wins,
        // so reporting 0 for a transient failure would leave that install
        // reading "no lighting devices" until the next tick, and the flap
        // would churn the dedupe signature into an extra $identify each time.
        // Null omits the property instead.
        int? lightingCount = null;
        try { lightingCount = _lighting.GetAll().Devices.Count; }
        catch (Exception ex) { Console.Error.WriteLine($"[profile] lighting count: {ex.GetType().Name}: {ex.Message}"); }
        int? fanCount = null;
        try { fanCount = _fans.GetFanChannels().Count; }
        catch (Exception ex) { Console.Error.WriteLine($"[profile] fan count: {ex.GetType().Name}: {ex.Message}"); }

        var usage = BuildUsageProperties(settings, lightingCount, fanCount);
        if (usage is not null) props.AddRange(usage);

        // The census above says what a machine runs right now; this event is
        // the same data over time, so widget adoption is readable as a trend.
        // Once a day is plenty for something that changes by hand.
        // The throttle lives in process memory, so this also fires once on the
        // first ready snapshot after every service start. A restart-heavy
        // machine therefore emits more than one a day: treat the event as a
        // sample, never as a per-day unit.
        if (usage is not null && DateTimeOffset.UtcNow - _lastLayoutSnapshot >= LayoutSnapshotInterval)
        {
            _lastLayoutSnapshot = DateTimeOffset.UtcNow;
            _telemetry.Capture(TelemetryEvents.LayoutSnapshot, usage.ToArray());
        }

        // Dedupe: only emit when the profile actually changed.
        var signature = string.Join("|", props.Select(p => p.Item1 + "=" + Stringify(p.Item2)));
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _telemetry.Identify(props.ToArray());
        }
        return true;
    }

    /// <summary>
    /// Usage shape, all of it counts and enum values - never a user-authored
    /// string. Widget TYPE keys ("clock", "media", "app:com.example.thing")
    /// are ours or an app id; a widget's title, a renamed device, and a panel's
    /// DisplayName are user text and deliberately never leave the machine.
    /// </summary>
    /// <returns>Null when the settings snapshot could not be walked cleanly;
    /// the caller then omits the usage half rather than reporting zeros.</returns>
    internal static List<(string, object?)>? BuildUsageProperties(
        NexusSettings settings, int? lightingCount, int? fanCount)
    {
        var widgetTypes = new SortedSet<string>(StringComparer.Ordinal);
        var widgetCount = 0;
        var pageCount = 0;
        var surfaces = new SortedSet<string>(StringComparer.Ordinal);

        // IConfigStore.Load hands back the LIVE settings instance, not a copy,
        // and the panel registry and the Nexus 2 importer mutate these
        // collections in place from other threads. A concurrent mutation
        // throws mid-enumeration; skip this tick rather than send a partial
        // census that would overwrite a good one.
        try
        {
            foreach (var panel in settings.PanelDevices.Values)
            {
                var layout = panel.Layout;
                if (layout is null) continue;
                if (IsSafeToken(layout.Surface)) surfaces.Add(layout.Surface);
                foreach (var page in layout.Pages)
                {
                    pageCount++;
                    foreach (var w in page.Widgets)
                    {
                        if (string.IsNullOrWhiteSpace(w.Type)) continue;
                        widgetCount++;
                        if (widgetTypes.Count < MaxWidgetTypes && IsReportableWidgetType(w.Type))
                            widgetTypes.Add(w.Type);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        // Which pillars are OFF is the signal; every flag defaults true, so an
        // empty list is the overwhelmingly common case and costs nothing.
        var featuresOff = new List<string>(4);
        if (!settings.Features.Lighting) featuresOff.Add("lighting");
        if (!settings.Features.Cooling) featuresOff.Add("cooling");
        if (!settings.Features.Monitoring) featuresOff.Add("monitoring");
        if (!settings.Features.Diagnostics) featuresOff.Add("diagnostics");

        var result = new List<(string, object?)>();
        // Omitted rather than zeroed when the provider read failed.
        if (lightingCount is { } lc) result.Add(("lighting_devices", lc));
        if (fanCount is { } fc) result.Add(("cooling_channels", fc));
        result.AddRange(new List<(string, object?)>
        {
            ("panel_devices", settings.PanelDevices.Count),
            ("panel_surfaces", surfaces.ToArray()),
            ("widget_count", widgetCount),
            ("widget_page_count", pageCount),
            ("widget_types", widgetTypes.ToArray()),
            // "advanced" here does NOT mean the user chose it: the v15
            // migration seeded every pre-existing install to "advanced", so
            // only DashboardModeChanged distinguishes a real switch.
            ("lighting_mode", settings.Ui.LightingDashboardMode),
            ("cooling_mode", settings.Ui.CoolingDashboardMode),
            ("features_off", featuresOff.ToArray()),
        });
        return result;
    }

    /// <summary>
    /// A widget type only ships if it is one of ours (a plain lowercase slug)
    /// or a valid SDK app id. PanelWidgetDto.Type is preserved verbatim for
    /// unknown values and is written from two unvalidated sources - the panel
    /// PATCH route and the Nexus 2 importer - so the send boundary re-checks
    /// it rather than trusting client discipline to keep user text out.
    /// </summary>
    internal static bool IsReportableWidgetType(string type)
    {
        if (type.Length > MaxWidgetTypeLength) return false;
        const string appPrefix = "app:";
        if (type.StartsWith(appPrefix, StringComparison.Ordinal))
            return Nexus.Service.Widgets.AppIds.IsValid(type[appPrefix.Length..]);
        return IsSafeToken(type);
    }

    /// <summary>Lowercase ASCII slug: a-z, 0-9, '-' and '_'. Anything else is
    /// assumed to be user text and dropped.</summary>
    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxWidgetTypeLength) return false;
        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Stringify(object? value) => value switch
    {
        null => "",
        // Unit-separator delimiter so a device name containing the delimiter
        // can't collide two entries into one (a missed change).
        string[] a => string.Join('\u001f', a),
        _ => value.ToString() ?? "",
    };

    private static void Add(List<(string, object?)> props, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            props.Add((key, value.Trim()));
    }

    // "32 GB DDR5-6000 (...)" -> 32. The first GB figure is the total.
    internal static int? ParseRamGb(string memory)
    {
        var m = Regex.Match(memory, @"(\d+(?:\.\d+)?)\s*GB", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v)
            : null;
    }

    // Sum the capacity of each drive in "1.82 TB Model + 931 GB Model" (reported
    // in GiB to match the storage string). Each drive segment leads with its
    // capacity, so take only the FIRST figure per segment - a model name that
    // embeds a size (e.g. "WD Blue SN570 1TB") must not be double-counted.
    internal static int? ParseStorageGb(string storage)
    {
        double total = 0;
        var any = false;
        foreach (var drive in storage.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(drive, @"(\d+(?:\.\d+)?)\s*(TB|GB)", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                total += m.Groups[2].Value.Equals("TB", StringComparison.OrdinalIgnoreCase) ? v * 1024 : v;
                any = true;
            }
        }
        return any ? (int)Math.Round(total) : null;
    }
}
