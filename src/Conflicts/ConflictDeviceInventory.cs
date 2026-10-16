using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals;
using Nexus.Service.Platform;

namespace Nexus.Service.Conflicts;

/// <summary>Gathers the passive inputs of <see cref="ConflictRelevance"/> (USB list, handler ids, board vendor) and caches the verdict briefly, since every conflict reader asks.</summary>
public sealed class ConflictDeviceInventory
{
    private readonly Func<IReadOnlyList<ConnectedUsb>> _usb;
    private readonly Func<IReadOnlyList<CuratedUsbMapping>> _curated;
    private readonly Func<string?> _board;
    private readonly object _lock = new();
    private ConflictRelevanceResult _cached = ConflictRelevanceResult.Empty;
    private long _cachedAt = long.MinValue / 2;

    private static readonly Lazy<IReadOnlyList<SupportedUsbDevice>> Supported = new(LoadSupported);

    /// <summary>How long a verdict is reused. Tests set 0.</summary>
    internal long TtlMs { get; set; } = 4000;

    public ConflictDeviceInventory(
        Func<IReadOnlyList<ConnectedUsb>> usb, Func<IReadOnlyList<CuratedUsbMapping>> curated, Func<string?> board)
    {
        _usb = usb;
        _curated = curated;
        _board = board;
    }

    public ConflictRelevanceResult Current()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (now - _cachedAt < TtlMs)
            {
                return _cached;
            }
            try
            {
                _cached = ConflictRelevance.Evaluate(
                    ConflictAppCatalog.All, _usb(), _board(), Array.Empty<string>(), Supported.Value, _curated());
            }
            catch (Exception ex)
            {
                // An unreadable inventory leaves every optional app alone; always-ended apps still qualify.
                ServiceLog.Warn($"[conflicts] device inventory failed, leaving optional apps alone: {ex.Message}");
                _cached = ConflictRelevance.Evaluate(
                    ConflictAppCatalog.All, Array.Empty<ConnectedUsb>(), null, Array.Empty<string>(),
                    Array.Empty<SupportedUsbDevice>(), Array.Empty<CuratedUsbMapping>());
            }
            _cachedAt = now;
            return _cached;
        }
    }

    private static IReadOnlyList<SupportedUsbDevice> LoadSupported()
    {
        var list = new List<SupportedUsbDevice>();
        foreach (var d in LightingDevicesCatalog.All)
        {
            if (TryHex(d.VendorId, out var vid) && TryHex(d.ProductId, out var pid))
            {
                list.Add(new SupportedUsbDevice(vid, pid, d.Model));
            }
        }
        return list;
    }

    private static bool TryHex(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return int.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
