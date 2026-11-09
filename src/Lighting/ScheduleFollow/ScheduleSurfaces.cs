using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Corsair.XeneonEdge;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Lighting.ScheduleFollow;

/// <summary>External DDC monitors. Slow writes, spaced out, since some monitors are slow and wear their NVRAM.</summary>
public sealed class DdcScheduleSurface : IScheduleSurface
{
    /// <summary>How often the monitor list is refreshed while following.</summary>
    public const long ScanGapMs = 60_000;

    private readonly DisplayBrightnessController _displays;
    private readonly Func<long> _clock;
    private long _lastScanMs = long.MinValue / 2;
    private List<string> _present = new();

    public DdcScheduleSurface(DisplayBrightnessController displays, Func<long>? clock = null)
    {
        _displays = displays;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public double RatePerSecond => ScheduleFollowMath.SlowRatePerSecond;
    public long MinWriteGapMs => ScheduleFollowWorker.DdcMinWriteGapMs;

    public IReadOnlyList<string> PresentKeys()
    {
        var now = _clock();
        if (now - _lastScanMs >= ScanGapMs)
        {
            _lastScanMs = now;
            _present = _displays.ListDisplays().Displays
                .Where(d => d.Capabilities.Brightness
                    && d.BrightnessControl.Supported
                    && d.BrightnessControl.ControlPath != DisplayBrightnessControlPaths.Y70
                    && d.Id.Length > 0)
                .Select(d => d.Id)
                .ToList();
        }
        return _present;
    }

    public int? Slider(string key) => _displays.GetSlider(key);
    public void RememberSlider(string key, int percent) => _displays.RememberSlider(key, percent);
    public Task<int?> ReadHardwareAsync(string key, CancellationToken ct) => Task.FromResult(_displays.GetBrightness(key));

    public async Task<bool> WriteAsync(string key, int level, CancellationToken ct) =>
        (await _displays.ApplyLevelAsync(key, level, ct).ConfigureAwait(false)).Status == DisplayBrightnessWriteStatuses.Applied;
}

/// <summary>The Corsair Xeneon Edge, keyed by its display id. Scales the register the web's Brightness slider writes (not Backlight).</summary>
public sealed class XeneonScheduleSurface : IScheduleSurface
{
    private readonly PanelDeviceRegistry _registry;
    private readonly XeneonEdgeOrientationWorker _xeneon;
    private readonly IConfigStore _store;

    public XeneonScheduleSurface(PanelDeviceRegistry registry, XeneonEdgeOrientationWorker xeneon, IConfigStore store)
    {
        _registry = registry;
        _xeneon = xeneon;
        _store = store;
    }

    public double RatePerSecond => ScheduleFollowMath.SlowRatePerSecond;
    public long MinWriteGapMs => ScheduleFollowWorker.DdcMinWriteGapMs;

    public IReadOnlyList<string> PresentKeys() => _registry.List()
        .Where(r => string.Equals(r.Capabilities?.Family, KnownPanelDisplays.XeneonEdgeFamily, StringComparison.Ordinal) && !string.IsNullOrEmpty(r.DisplayId))
        .Select(r => r.DisplayId!)
        .ToList();

    public int? Slider(string key) =>
        _store.Load().Devices.DisplaySliderBrightness.TryGetValue(key, out var v) ? v : null;

    public void RememberSlider(string key, int percent) =>
        _store.Update(s => s.Devices.DisplaySliderBrightness[key] = Math.Clamp(percent, 0, 100));

    public async Task<int?> ReadHardwareAsync(string key, CancellationToken ct) =>
        (await _xeneon.ReadSettingsAsync(ct).ConfigureAwait(false))?.Brightness;

    public async Task<bool> WriteAsync(string key, int level, CancellationToken ct) =>
        await _xeneon.SetControlAsync(XeneonEdgeControl.Brightness, level, ct).ConfigureAwait(false) is not null;
}

/// <summary>The Kraken LCD. Its snapshot already holds the user's slider; the hub scales what it writes.</summary>
public sealed class KrakenScheduleSurface : IScheduleSurface
{
    private readonly KrakenHub _hub;

    public KrakenScheduleSurface(KrakenHub hub) => _hub = hub;

    public double RatePerSecond => ScheduleFollowMath.FastRatePerSecond;
    public long MinWriteGapMs => 1000;

    public IReadOnlyList<string> PresentKeys() =>
        _hub.IsConnected && _hub.HasLcd ? new[] { ScheduleFollowKeys.KrakenLcd } : Array.Empty<string>();

    public int? Slider(string key) => _hub.Snapshot.LcdBrightness;
    public void RememberSlider(string key, int percent) { }
    public Task<int?> ReadHardwareAsync(string key, CancellationToken ct) => Task.FromResult<int?>(null);
    public Task<bool> WriteAsync(string key, int level, CancellationToken ct) => Task.FromResult(_hub.ApplySchedule());
}

/// <summary>Streamed-panel LCDs, one key per panel device. The coordinator scales at the point the level reaches the transport.</summary>
public sealed class PanelLcdScheduleSurface : IScheduleSurface
{
    private readonly StreamedPanelCoordinator _panels;

    public PanelLcdScheduleSurface(StreamedPanelCoordinator panels) => _panels = panels;

    public double RatePerSecond => ScheduleFollowMath.FastRatePerSecond;
    public long MinWriteGapMs => 1000;

    public IReadOnlyList<string> PresentKeys() =>
        _panels.DimmablePanelIds().Select(id => ScheduleFollowKeys.PanelLcdPrefix + id).ToList();

    private static string Id(string key) => key.Substring(ScheduleFollowKeys.PanelLcdPrefix.Length);

    public int? Slider(string key) => _panels.Slider(Id(key));
    public void RememberSlider(string key, int percent) { }
    public Task<int?> ReadHardwareAsync(string key, CancellationToken ct) => Task.FromResult<int?>(null);
    public Task<bool> WriteAsync(string key, int level, CancellationToken ct) => Task.FromResult(_panels.ApplyBrightness(Id(key)));
}
