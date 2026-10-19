using System;
using System.Collections.Generic;
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

    public FlexScreens(FlexLcdHub lcd, FlexHub flex)
    {
        _lcd = lcd;
        _flex = flex;
    }

    public string HandlerId => Handler;

    public string LogTag => "lianli-flex-lcd";

    public IReadOnlyList<string> AttachedSerials()
    {
        var serials = new List<string>();
        foreach (var s in _lcd.Hub.Screens)
        {
            serials.Add(s.Serial);
        }
        return serials;
    }

    public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.FlexLcdScreens;

    public void PrepareFrames()
    {
    }

    public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) =>
        _lcd.Hub.SyncBrightness(serial, settings, ref applied, ref lockSlept);

    public bool SetRotation(string serial, byte rotation) => _lcd.Hub.SetRotation(serial, rotation);

    public bool PushImage(string serial, byte[] jpeg) => _lcd.Hub.PushImageToSerial(serial, jpeg);

    public void Release(string serial)
    {
    }

    public void SetPanelOwned(string serial, bool owned) => _panelOwnership.Set(serial, owned);

    public bool PushPanelFrame(string serial, byte[] jpeg) => _panelOwnership.Push(serial, () => PushImage(serial, jpeg));

    public string PanelFamily => "lianli-flex-lcd";

    public int PanelFps => 25;

    public string PanelName(string serial)
    {
        foreach (var u in _flex.Units)
        {
            if (string.Equals(u.ScreenSerial, serial, StringComparison.OrdinalIgnoreCase))
            {
                return u.Family == FlexFamily.SlInfFlex ? "Lian Li SL-INF LCD Flex" : "Lian Li TL LCD Flex";
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
        if (!_flex.RebootLcd(mac, 15_000))
        {
            return false;
        }
        _lcd.Hub.Forget(unit.ScreenSerial, "restart");
        return true;
    }
}

/// <summary>Publishes the attached Flex screens and forgets departed ones, like the wireless screen worker.</summary>
public sealed class FlexLcdConnectionWorker : BackgroundService
{
    private const int PollMs = 2000;

    private readonly FlexLcdHub _lcd;
    private readonly DeviceControlGate _gate;

    public FlexLcdConnectionWorker(FlexLcdHub lcd, DeviceControlGate gate)
    {
        _lcd = lcd;
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
        var lastDiscover = long.MinValue;
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
