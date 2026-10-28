using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.SteelSeries;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents each attached Apex Pro TKL's OLED as a streamed panel. The screen interface is plain
/// HID, so discovery just looks for it each pass; absence is a normal result.
///
/// The profile asks for <see cref="StreamCodec.RawBgra"/> at the screen's own pixel size and the
/// transport reduces each frame to one bit. A keyboard that refuses frames drops out of discovery
/// while a growing delay runs, so a broken screen is retried slowly instead of every pass.
/// </summary>
public sealed class ApexOledPanelDiscovery : IStreamedPanelDiscovery
{
    public const string Id = "steelseries-apex-oled";

    /// <summary>Widgets on a mono screen change slowly and unchanged frames are not sent, so a low rate is plenty and light on the keyboard.</summary>
    internal const int StreamFps = 10;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence? _presence;
    private readonly LcdLockState? _lock;
    private readonly AttachBackoff _backoff;

    public ApexOledPanelDiscovery(IHidEnumerator hid, HardwarePresence? presence = null, LcdLockState? lockState = null, Func<long>? clock = null)
    {
        _hid = hid;
        _presence = presence;
        _lock = lockState;
        _backoff = new AttachBackoff(clock ?? (() => Environment.TickCount64));
    }

    public string HandlerId => Id;

    /// <summary>The panel serial for a screen: family, model and the key that tells two keyboards apart.</summary>
    public static string PanelSerial(ApexOledScreen screen) => $"{Id}:{screen.Model.ProductId:x4}:{screen.Key}";

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        if (_backoff.DelayMs > 0)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        if (_presence is not null && !_presence.UsbPresent(ApexOledProtocol.VendorId, ApexOledProtocol.ProductIds))
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        var panels = new List<StreamedPanelDeviceInfo>();
        foreach (var screen in ApexOledScreens.Find(_hid))
        {
            panels.Add(new StreamedPanelDeviceInfo
            {
                Serial = PanelSerial(screen),
                Profile = new StreamedPanelProfile
                {
                    Kind = Id,
                    DisplayName = screen.Model.Name,
                    Surface = PanelSurfaces.LcdWide,
                    Family = Id,
                    CssWidth = ApexOledProtocol.Width,
                    CssHeight = ApexOledProtocol.Height,
                    Dpr = 1.0,
                    Fps = StreamFps,
                    Codec = StreamCodec.RawBgra,
                    MultiInstance = true,
                },
            });
        }
        return panels;
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info)
    {
        // Serial is "<id>:<product id hex>:<key>".
        var parts = info.Serial.Split(':', 3);
        var model = (parts.Length == 3 && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var pid)
            ? ApexOledProtocol.ModelFor(pid)
            : null) ?? throw new System.IO.IOException($"not an Apex OLED panel: {info.Serial}");
        return new ApexOledStreamTransport(_hid, model, info.Serial, _lock, backoff: _backoff);
    }
}
