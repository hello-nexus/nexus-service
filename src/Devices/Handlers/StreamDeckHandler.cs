using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Elgato Stream Deck button decks (gen1 verified on the Mini; gen2 lands in Phase 3).</summary>
public sealed class StreamDeckHandler : IDeviceHandler
{
    private const string ElgatoSoftwareWarning = "elgato-software-running";

    /// <summary>ConflictAppCatalog id for the same Elgato-software contention <see cref="GetWarning"/> detects.</summary>
    public const string ElgatoConflictAppId = "elgato-stream-deck";

    private readonly StreamDeckConnectionWorker _worker;

    public StreamDeckHandler(StreamDeckConnectionWorker worker)
    {
        _worker = worker;
    }

    public string Id => "streamdeck";
    public string Name => "Stream Deck";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; } = StreamDeckModels.All
        .Select(m => new UsbId(m.VendorId, m.ProductId))
        .ToArray();

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() =>
        _worker.Surfaces.Values.FirstOrDefault(s => s.IsConnected)?.FirmwareVersion ?? "";

    /// <summary>
    /// Elgato's own Stream Deck software can hold the same HID handle
    /// concurrently (Windows queues per-open reads/writes rather than
    /// rejecting the second open), so both apps painting the deck is a
    /// visual fight rather than a connection failure - surfaced as a warning
    /// instead of attempted detection-by-failure.
    /// </summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (!IsConnected(detectedDevices))
        {
            return null;
        }
        return IsElgatoSoftwareRunning() ? ElgatoSoftwareWarning : null;
    }

    private static bool IsElgatoSoftwareRunning()
    {
        try
        {
            // macOS names the process "Stream Deck" (with a space).
            return Process.GetProcessesByName("StreamDeck").Length > 0 || Process.GetProcessesByName("Stream Deck").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
