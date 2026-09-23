using System;
using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// Canonical <c>PanelDeviceCapabilities.Surface</c> values. The SPA infers
/// its surface from the viewport for self-registered devices; service-created
/// records (monitor promotion) stamp it explicitly.
/// </summary>
public static class PanelSurfaces
{
    public const string Y70 = "y70";
    public const string Q60 = "q60";
    public const string Phone = "phone";
    public const string Desktop = "desktop";
    /// <summary>A user-promoted OS monitor hosting a fullscreen panel kiosk.</summary>
    public const string Monitor = "monitor";

    /// <summary>Round 640x640 glass on an NZXT Kraken, driven by pushed frames.</summary>
    public const string Kraken = "kraken";

    /// <summary>
    /// Round cooler glass driven by pushed JPEG frames. Unlike <see cref="Kraken"/> this
    /// is not one model's resolution: the panel record carries the real pixel size.
    /// </summary>
    public const string LcdRound = "lcd-round";

    /// <summary>Square cooler glass on the same pushed-JPEG path as <see cref="LcdRound"/>.</summary>
    public const string LcdSquare = "lcd-square";

    /// <summary>
    /// Wide cooler glass on the same pushed-frame path as <see cref="LcdSquare"/>, split
    /// out because the single tile these surfaces carry is sized by the surface, not the
    /// record: a square panel wants a 2x2 and a 1600x720 Thermalright Wonder Vision a 4x2.
    /// </summary>
    public const string LcdWide = "lcd-wide";

    /// <summary>
    /// Surfaces that are exactly one physical panel per host. They self-register
    /// over <c>POST /panel/devices</c> (no OS displayId to key on) and rely on
    /// the kiosk WebView's localStorage to reuse their record. The Q-series OEM
    /// shell drops that cache across reconnects, which would accrete a record
    /// every connect, so <see cref="Panel.PanelDeviceRegistry.Allocate"/> reuses
    /// existing record for these instead of minting a new one. Phones are
    /// multi-instance (many pair); promoted monitors are multi-instance and
    /// keyed by displayId.
    /// </summary>
    public static readonly IReadOnlySet<string> SingleInstance =
        new HashSet<string>(StringComparer.Ordinal) { Y70, Q60, Kraken, LcdRound, LcdSquare, LcdWide };

    public static bool IsSingleInstance(string? surface) =>
        surface is not null && SingleInstance.Contains(surface);
}
