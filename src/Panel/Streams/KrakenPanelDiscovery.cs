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

    /// <summary>The density the Kraken's grid has always been laid out at; the shared round-glass default differs.</summary>
    internal const double PanelDpi = 240;

    private readonly KrakenHub _hub;

    public KrakenPanelDiscovery(KrakenHub hub)
    {
        _hub = hub;
    }

    public string HandlerId => KrakenHub.DeviceId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        // The bulk pipe is what carries frames; without it the cooler still works as a
        // cooler but has no panel to offer.
        if (!_hub.IsConnected || !_hub.HasLcd || _hub.LcdWidth <= 0 || _hub.LcdHeight <= 0)
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
                    // Persisted as the legacy value so an older build still finds the record by its
                    // surface. Comparisons go through PanelSurfaces.Canonical; the profile-file key
                    // and capability equality read it raw on purpose.
                    Surface = PanelSurfaces.LegacyKraken,
                    Family = KrakenHub.DeviceId,
                    CssWidth = _hub.LcdWidth,
                    CssHeight = _hub.LcdHeight,
                    Dpr = 1.0,
                    Dpi = PanelDpi,
                    // The Q565 models' rate is a display limit: a frame swaps buckets, and
                    // driving that harder reads as flicker.
                    Fps = _hub.Model.StreamFps,
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new KrakenStreamTransport(_hub, info.Serial);
}
