using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.Nzxt;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents a connected Kraken's LCD as a streamed panel. There is no bus to poll: the
/// cooler's own connection worker owns discovery, so this reports whatever the hub
/// currently holds, and absence is a normal tick result.
///
/// The LCD takes whole uncompressed frames, so the profile asks for
/// <see cref="StreamCodec.RawBgra"/> and the overlay skips its H.264 encoder.
/// </summary>
public sealed class KrakenPanelDiscovery : IStreamedPanelDiscovery
{
    /// <summary>Persisted per-serial override key; changing it resets user overrides.</summary>
    private const string ProfileKind = "kraken-round";

    private readonly KrakenHub _hub;

    public KrakenPanelDiscovery(KrakenHub hub)
    {
        _hub = hub;
    }

    public string HandlerId => KrakenHub.DeviceId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        // The bulk pipe is what carries frames; without it the cooler still works as a
        // cooler but has no panel to offer. A still-image-only panel is not a stream target.
        if (!_hub.IsConnected || !_hub.HasLcd || _hub.LcdWidth <= 0 || _hub.LcdHeight <= 0
            || _hub.Model.StillImageOnly)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        return new[]
        {
            new StreamedPanelDeviceInfo
            {
                Serial = _hub.Serial ?? KrakenHub.DeviceId,
                Profile = new StreamedPanelProfile
                {
                    Kind = ProfileKind,
                    DisplayName = $"{_hub.ModelName} LCD",
                    Surface = PanelSurfaces.Kraken,
                    Family = KrakenHub.DeviceId,
                    CssWidth = _hub.LcdWidth,
                    CssHeight = _hub.LcdHeight,
                    Dpr = 1.0,
                    // Frames go out Q565-compressed: 7-11 KB rather than 1.6 MB, so a full
                    // bucket cycle measures ~8.5 ms median (13 ms worst) against the ~450 ms
                    // a raw frame took. The wire would take more than this; 30 is the display
                    // limit - a frame swaps the panel between two buckets, and driving that
                    // swap harder reads as flicker.
                    Fps = 30,
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new KrakenStreamTransport(_hub, info.Serial);
}
