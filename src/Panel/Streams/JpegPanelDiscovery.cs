using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents one connected JPEG-over-HID cooler LCD as a streamed panel. Like the Kraken's
/// there is no bus to poll: the hub's own connection worker owns discovery, so this reports
/// whatever the hub currently holds and absence is a normal tick result.
///
/// The profile asks for <see cref="StreamCodec.RawBgra"/> and the transport JPEG-encodes
/// each frame; the overlay carries no image library, so the compression happens here.
/// </summary>
public sealed class JpegPanelDiscovery : IStreamedPanelDiscovery
{
    private readonly JpegPanelHub _hub;

    public JpegPanelDiscovery(JpegPanelHub hub)
    {
        _hub = hub;
    }

    public string HandlerId => _hub.Model.HandlerId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        var model = _hub.Model;
        return new[]
        {
            new StreamedPanelDeviceInfo
            {
                Serial = _hub.Serial ?? model.HandlerId,
                Profile = new StreamedPanelProfile
                {
                    // Per-serial overrides key off this, so it carries the model rather than
                    // the family: changing one model's profile must not reset another's.
                    Kind = model.HandlerId,
                    DisplayName = model.Name,
                    Surface = model.Surface,
                    // Two models share each cooler-LCD surface, so the sidebar brands from
                    // this rather than the surface name.
                    Family = model.HandlerId,
                    SupportsBrightness = model.SupportsBrightness,
                    CssWidth = model.Width,
                    CssHeight = model.Height,
                    Dpr = 1.0,
                    Fps = model.Fps,
                    MultiInstance = model.MultiInstance,
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new JpegPanelStreamTransport(_hub, info.Serial);
}
