using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>The SL-LCD Wireless screens as a <see cref="IFanLcdScreens"/> family, over <see cref="Slv3LcdHub"/>.</summary>
public sealed class Slv3LcdScreens : IFanLcdScreens
{
    private readonly Slv3LcdHub _hub;
    private readonly Slv3Hub _rfHub;
    private readonly IConfigStore _store;
    private readonly FanLcdPanelOwnership _panelOwnership = new();

    public Slv3LcdScreens(Slv3LcdHub hub, Slv3Hub rfHub, IConfigStore store)
    {
        _hub = hub;
        _rfHub = rfHub;
        _store = store;
    }

    public string HandlerId => "lianli-wireless";

    public string LogTag => "lianli-wireless-lcd";

    /// <summary>The connection worker's last pass, so panel discovery does not re-enumerate the bus every tick.</summary>
    public IReadOnlyList<string> AttachedSerials()
    {
        var screens = _hub.Screens;
        var serials = new List<string>(screens.Count);
        foreach (var screen in screens)
        {
            serials.Add(screen.Serial);
        }
        return serials;
    }

    public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.LianLiWireless.Screens;

    /// <summary>
    /// Arms the RF TX for LCD video traffic (L-Connect's ensure_video_mode); idempotent per RF
    /// connection, so it also re-arms after a dongle reconnect. The screens' video RF shares the
    /// 2.4 GHz air with the fan control link.
    /// </summary>
    public void PrepareFrames() => _rfHub.EnsureVideoMode();

    public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept) =>
        _hub.SyncBrightness(serial, settings, ref applied, ref lockSlept);

    public bool SetRotation(string serial, byte rotation)
    {
        var ok = _hub.SetRotation(serial, rotation);
        if (ok)
        {
            _panelOwnership.Repush(serial, jpeg => PushImage(serial, jpeg));
        }
        return ok;
    }

    public bool PushImage(string serial, byte[] jpeg) => _hub.PushImageToSerial(serial, jpeg);

    /// <summary>The screen holds its last frame; there is nothing to hand back.</summary>
    public void Release(string serial)
    {
    }

    public void SetPanelOwned(string serial, bool owned) => _panelOwnership.Set(serial, owned);

    public bool PushPanelFrame(string serial, byte[] jpeg) => _panelOwnership.Push(serial, jpeg, () => PushImage(serial, jpeg));

    public string PanelFamily => "lianli-wireless-lcd";

    /// <summary>
    /// The cooler-LCD panel rate. The overlay sends a frame when the layout repaints, so an
    /// animated layout reaches it; the screens' video shares the 2.4 GHz air with fan control
    /// (see <see cref="PrepareFrames"/>).
    /// </summary>
    public int PanelFps => 25;

    /// <summary>"#n" from the Screens tab's list order, the same number its tiles show.</summary>
    public string PanelName(string serial)
    {
        const string name = "Lian Li Wireless LCD";
        var ordered = Routes.Slv3LcdRoutes.OrderScreens(_hub.Screens, _store.Load().Devices.LianLiWireless.Screens);
        int index = ordered.FindIndex(s => string.Equals(s.Serial, serial, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? $"{name} #{index + 1}" : name;
    }
}
