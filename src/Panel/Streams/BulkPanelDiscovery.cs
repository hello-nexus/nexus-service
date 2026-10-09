using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents one connected bulk-pipe cooler LCD as a streamed panel. Geometry comes from
/// the hub rather than a constant: a Thermalright reports its own model at connect, so the
/// same driver can be a 480x480 puck or a 1920x462 strip.
/// </summary>
public sealed class BulkPanelDiscovery : IStreamedPanelDiscovery
{
    private readonly BulkPanelHub _hub;
    private readonly IVirtualMonitorHost? _monitors;
    private readonly Func<bool>? _withheld;

    /// <param name="withheld">True while the panel's glass shows something other than Nexus.</param>
    /// <param name="listedWhileWithheld">See <see cref="IStreamedPanelDiscovery.ListedWhileWithheld"/>.</param>
    public BulkPanelDiscovery(BulkPanelHub hub, IVirtualMonitorHost? monitors = null, Func<bool>? withheld = null, bool listedWhileWithheld = false)
    {
        _hub = hub;
        _monitors = monitors;
        _withheld = withheld;
        ListedWhileWithheld = listedWhileWithheld;
    }

    public bool ListedWhileWithheld { get; }

    public string? WithheldSerial => _hub.IsConnected && Withheld ? _hub.Serial ?? _hub.Driver.HandlerId : null;

    public string HandlerId => _hub.Driver.HandlerId;

    public bool Withheld => _withheld?.Invoke() == true;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        if (!_hub.IsConnected || _hub.Width <= 0 || _hub.Height <= 0 || Withheld)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        var driver = _hub.Driver;
        return new[]
        {
            new StreamedPanelDeviceInfo
            {
                Serial = _hub.Serial ?? driver.HandlerId,
                Profile = new StreamedPanelProfile
                {
                    Kind = driver.HandlerId,
                    DisplayName = driver.Name,
                    Surface = driver.Surface,
                    Family = driver.HandlerId,
                    CssWidth = _hub.Width,
                    CssHeight = _hub.Height,
                    Dpr = 1.0,
                    Fps = driver.Fps,
                    SupportsBrightness = driver.SupportsBrightness,
                    SupportsSecondaryMonitor = driver.SupportsSecondaryMonitor && _monitors is { IsAvailable: true },
                    // The driver owns compression - JPEG for most, raw pixels for the
                    // Ryujin - so the overlay hands back whole frames either way.
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new BulkPanelStreamTransport(_hub, info.Serial, _monitors);
}
