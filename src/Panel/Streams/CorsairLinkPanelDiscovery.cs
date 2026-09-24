using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents the iCUE LINK cooler's LCD as a streamed panel, the way the Kraken's glass is
/// one. There is no bus to poll here either: the hub's connection worker owns discovery
/// and opens the LCD's own HID interface, so this reports whatever it currently holds and
/// absence is a normal tick result.
///
/// The panel takes a whole JPEG per frame, so the profile asks for
/// <see cref="StreamCodec.RawBgra"/> and the transport encodes.
/// </summary>
public sealed class CorsairLinkPanelDiscovery : IStreamedPanelDiscovery
{
    /// <summary>Persisted per-serial override key; changing it resets user overrides.</summary>
    internal const string ProfileKind = CorsairLinkLcd.DeviceId;

    private readonly CorsairLinkHub _hub;
    private readonly CorsairLinkLcd _lcd;

    public CorsairLinkPanelDiscovery(CorsairLinkHub hub, CorsairLinkLcd lcd)
    {
        _hub = hub;
        _lcd = lcd;
    }

    /// <summary>
    /// The glass has its own gate, separate from the hub's: someone who claims the cooler
    /// for fans and RGB has not thereby asked Nexus to repaint their pump screen.
    /// </summary>
    public string HandlerId => ProfileKind;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        if (!_hub.State.IsConnected || !_hub.State.HasLcd || !_lcd.HasDevice)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        return new[]
        {
            new StreamedPanelDeviceInfo
            {
                Serial = _lcd.Serial ?? ProfileKind,
                Profile = new StreamedPanelProfile
                {
                    Kind = ProfileKind,
                    DisplayName = "iCUE LINK LCD",
                    Surface = PanelSurfaces.LcdRound,
                    // Shares 'lcd-round' with the XC7, so the sidebar brands from this
                    // rather than the surface name.
                    Family = ProfileKind,
                    CssWidth = CorsairLinkLcd.PanelWidth,
                    CssHeight = CorsairLinkLcd.PanelHeight,
                    Dpr = 1.0,
                    // The firmware latches a frame per sub-1016-byte chunk and OLH paces
                    // its own player at 10 ms; 30 matches the rest of the cooler-LCD family
                    // and leaves the 1 KB-at-a-time HID pipe headroom.
                    Fps = 30,
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new CorsairLinkLcdStreamTransport(_lcd, info.Serial);
}
