using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>The Flex screens as an ISlv3LcdDiscovery: each unit's screen, found through the receiver discovery.</summary>
public sealed class FlexScreenDiscovery : ISlv3LcdDiscovery
{
    private readonly IFlexDiscovery _units;

    public FlexScreenDiscovery(IFlexDiscovery units)
    {
        _units = units;
    }

    public IReadOnlyList<Slv3LcdPortInfo> Discover()
    {
        var result = new List<Slv3LcdPortInfo>();
        foreach (var u in _units.Discover())
        {
            if (u.ScreenPath.Length > 0 && u.ScreenSerial.Length > 0)
            {
                result.Add(new Slv3LcdPortInfo { PortName = u.ScreenPath, Serial = u.ScreenSerial, ProductId = u.ScreenPid });
            }
        }
        return result;
    }
}

/// <summary>Owns the screen hub of the Flex fans; a second <see cref="Slv3LcdHub"/> next to the wireless one.</summary>
public sealed class FlexLcdHub
{
    public FlexLcdHub(Slv3LcdHub hub)
    {
        Hub = hub;
    }

    public Slv3LcdHub Hub { get; }
}

/// <summary>The Flex screens as an <see cref="IFanLcdScreens"/> family.</summary>
public sealed class FlexScreens : IFanLcdScreens
{
    public const string Handler = "lianli-flex";

    private readonly FlexLcdHub _lcd;
    private readonly FlexHub _flex;
    private readonly FanLcdPanelOwnership _panelOwnership = new();
    private readonly System.Func<string, int>? _sortIndexOf;
    private readonly System.Func<long> _nowMs;
    private sealed class RestartState
    {
        public required string Mac { get; init; }
        public required long StartedMs { get; init; }
        public bool SawGone;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RestartState> _restarts = new(StringComparer.OrdinalIgnoreCase);
    private const int VersionRetryMs = 30_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _versionTried = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A restarting screen is hidden from the content loops, and nothing is written to it, until
    /// discovery has seen it leave the bus and come back. Discovery polls slower than the reboot
    /// takes, so after <see cref="RestartQuietMs"/> a screen that is listed again also counts as
    /// back; <see cref="RestartGiveUpMs"/> ends the wait whatever happens. The new content loop
    /// then opens the screen once and applies brightness and rotation first.
    /// </summary>
    public const int RestartQuietMs = 3500;
    public const int RestartGiveUpMs = 20_000;

    public FlexScreens(FlexLcdHub lcd, FlexHub flex, System.Func<long>? nowMs = null, System.Func<string, int>? sortIndexOf = null)
    {
        _sortIndexOf = sortIndexOf;
        _lcd = lcd;
        _flex = flex;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    private bool Restarting(string serial)
    {
        if (!_restarts.TryGetValue(serial, out var state))
        {
            return false;
        }
        var now = _nowMs();
        var listed = _lcd.Hub.Screens.Any(s => string.Equals(s.Serial, serial, StringComparison.OrdinalIgnoreCase));
        if (!listed)
        {
            state.SawGone = true;
        }
        var back = listed && (state.SawGone || now - state.StartedMs >= RestartQuietMs);
        if (back || now - state.StartedMs >= RestartGiveUpMs)
        {
            _restarts.TryRemove(serial, out _);
            _flex.EndLcdRestart(state.Mac);
            return false;
        }
        return true;
    }

    /// <summary>The screen's firmware string once read; empty before.</summary>
    public string VersionOf(string serial) => _versions.TryGetValue(serial, out var v) ? v : "";

    /// <summary>Reads the firmware string of every open screen that has none yet.</summary>
    internal void RefreshVersions()
    {
        foreach (var s in _lcd.Hub.Screens)
        {
            if (_versions.ContainsKey(s.Serial) || Restarting(s.Serial))
            {
                continue;
            }
            // A screen that does not answer is asked again only every VersionRetryMs.
            if (_versionTried.TryGetValue(s.Serial, out var tried) && _nowMs() - tried < VersionRetryMs)
            {
                continue;
            }
            if (!_lcd.Hub.OpenSerials().Contains(s.Serial, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            _versionTried[s.Serial] = _nowMs();
            var v = _lcd.Hub.WithOpenTransport(s.Serial, t => t is IFlexScreenVersion f ? f.ReadVersion() : null);
            if (!string.IsNullOrEmpty(v))
            {
                _versions[s.Serial] = v;
            }
        }
    }

    public string HandlerId => Handler;

    public string LogTag => "lianli-flex-lcd";

    public IReadOnlyList<string> AttachedSerials()
    {
        foreach (var serial in _restarts.Keys)
        {
            Restarting(serial);
        }
        var serials = new List<string>();
        foreach (var s in _lcd.Hub.Screens)
        {
            if (!Restarting(s.Serial))
            {
                serials.Add(s.Serial);
            }
        }
        return serials;
    }

    public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.FlexLcdScreens;

    public void PrepareFrames()
    {
    }

    public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept)
    {
        if (!Restarting(serial))
        {
            _lcd.Hub.SyncBrightness(serial, settings, ref applied, ref lockSlept);
        }
    }

    public bool SetRotation(string serial, byte rotation)
    {
        if (Restarting(serial) || !_lcd.Hub.SetRotation(serial, HardwareRotation(serial, rotation)))
        {
            return false;
        }
        _panelOwnership.Repush(serial, jpeg => PushImage(serial, jpeg));
        return true;
    }

    /// <summary>The value written to the panel for a stored rotation: a right-attached SL-INF turns it a half turn.</summary>
    public byte HardwareRotation(string serial, byte rotation)
    {
        foreach (var u in _flex.Units)
        {
            if (string.Equals(u.ScreenSerial, serial, StringComparison.OrdinalIgnoreCase))
            {
                return HardwareRotationFor(u.Family, u.Info.FanNum, rotation);
            }
        }
        return rotation;
    }

    internal static byte HardwareRotationFor(FlexFamily family, int fanNum, byte rotation) =>
        family == FlexFamily.SlInfFlex && fanNum >= 10 ? (byte)((rotation + 2) % 4) : rotation;

    public bool PushImage(string serial, byte[] jpeg) => !Restarting(serial) && _lcd.Hub.PushImageToSerial(serial, FlexJpeg.WithJfif(jpeg));

    public void Release(string serial)
    {
    }

    public void SetPanelOwned(string serial, bool owned) => _panelOwnership.Set(serial, owned);

    public bool PushPanelFrame(string serial, byte[] jpeg) => _panelOwnership.Push(serial, jpeg, () => PushImage(serial, jpeg));

    public string PanelFamily => "lianli-flex-lcd";

    public int PanelFps => 25;

    public string PanelName(string serial)
    {
        foreach (var u in _flex.Units)
        {
            if (string.Equals(u.ScreenSerial, serial, StringComparison.OrdinalIgnoreCase))
            {
                return FlexNames.UnitName(_flex.Units, u, mac => _sortIndexOf?.Invoke(mac) ?? 0);
            }
        }
        return "Lian Li LCD Flex";
    }

    /// <summary>
    /// Reboots the screen controller through its receiver and drops the screen handle so the next
    /// use re-opens it after it re-enumerates. Content, rotation and brightness are re-applied by
    /// the streaming worker when the screen returns.
    /// </summary>
    public bool Restart(string mac)
    {
        var unit = _flex.Find(mac);
        if (unit is null || unit.ScreenSerial.Length == 0)
        {
            return false;
        }
        if (!_flex.RebootLcd(mac, RestartGiveUpMs))
        {
            return false;
        }
        _restarts[unit.ScreenSerial] = new RestartState { Mac = unit.Mac, StartedMs = _nowMs() };
        _versions.TryRemove(unit.ScreenSerial, out _);
        _versionTried.TryRemove(unit.ScreenSerial, out _);
        _lcd.Hub.Forget(unit.ScreenSerial, "restart");
        return true;
    }
}

/// <summary>Publishes the attached Flex screens and forgets departed ones, like the wireless screen worker.</summary>
public sealed class FlexLcdConnectionWorker : BackgroundService
{
    private const int PollMs = 2000;

    private readonly FlexLcdHub _lcd;
    private readonly FlexScreens _screens;
    private readonly DeviceControlGate _gate;

    public FlexLcdConnectionWorker(FlexLcdHub lcd, FlexScreens screens, DeviceControlGate gate)
    {
        _lcd = lcd;
        _screens = screens;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-flex-lcd] connection worker error: {ex.Message}");
            }
            await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
        }
    }

    internal void Tick()
    {
        var hub = _lcd.Hub;
        if (!_gate.IsEnabled(FlexScreens.Handler))
        {
            foreach (var serial in hub.OpenSerials())
            {
                hub.Forget(serial, "Nexus Control off");
            }
            hub.UpdateScreens(Array.Empty<Slv3LcdScreenInfo>());
            return;
        }
        var ports = hub.Discover();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var screens = new List<Slv3LcdScreenInfo>(ports.Count);
        foreach (var p in ports)
        {
            if (!string.IsNullOrWhiteSpace(p.Serial))
            {
                seen.Add(p.Serial);
                screens.Add(new Slv3LcdScreenInfo { Serial = p.Serial, Position = -1 });
            }
        }
        foreach (var serial in hub.OpenSerials())
        {
            if (!seen.Contains(serial))
            {
                hub.Forget(serial, "left the bus");
            }
        }
        hub.UpdateScreens(screens);
        _screens.RefreshVersions();
    }
}

/// <summary>Opens, polls and drops receivers while the Flex handler's control gate is on.</summary>
public sealed class FlexConnectionWorker : BackgroundService
{
    private const int DiscoverMs = 2000;
    private const int TickMs = 100;

    private readonly FlexHub _hub;
    private readonly DeviceControlGate _gate;

    public FlexConnectionWorker(FlexHub hub, DeviceControlGate gate)
    {
        _hub = hub;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastDiscover = -(long)DiscoverMs;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(FlexScreens.Handler))
                {
                    _hub.Close();
                    await Task.Delay(DiscoverMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                var now = Environment.TickCount64;
                if (now - lastDiscover >= DiscoverMs)
                {
                    lastDiscover = now;
                    _hub.Refresh();
                }
                _hub.Tick();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-flex] worker error: {ex.Message}");
            }
            await Task.Delay(TickMs, stoppingToken).ConfigureAwait(false);
        }
        _hub.Close();
    }
}
