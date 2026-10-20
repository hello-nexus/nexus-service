using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// One family of Lian Li fan screens (SL wireless, wired Uni Fan TL), as the shared content
/// worker, panel discovery and screen routes drive it. Screens are keyed by serial.
/// </summary>
public interface IFanLcdScreens
{
    /// <summary>DeviceControlGate handler id the screens follow.</summary>
    string HandlerId { get; }

    string LogTag { get; }

    IReadOnlyList<string> AttachedSerials();

    /// <summary>This family's per-screen settings inside the device settings.</summary>
    Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices);

    /// <summary>Runs before each content pass; the SL family arms its RF link for video here.</summary>
    void PrepareFrames();

    void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept);

    bool SetRotation(string serial, byte rotation);

    bool PushImage(string serial, byte[] jpeg);

    /// <summary>Nexus stopped driving the screen, so it goes back to what its firmware shows.</summary>
    void Release(string serial);

    /// <summary>Hands the screen to its panel session, or takes it back; returns once no panel frame is in flight.</summary>
    void SetPanelOwned(string serial, bool owned);

    /// <summary>A panel session's frame; refused unless the panel owns the screen.</summary>
    bool PushPanelFrame(string serial, byte[] jpeg);

    /// <summary>Streamed-panel family of a screen in panel mode; never a device handler id, so the panel never claims the device's row.</summary>
    string PanelFamily { get; }

    int PanelFps { get; }

    /// <summary>The most frames a second the screen shows; a GIF or video clip faster than this skips frames to keep its time.</summary>
    int MaxFps(string serial);

    string PanelName(string serial);
}
