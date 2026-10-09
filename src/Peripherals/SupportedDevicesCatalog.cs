using System.Collections.Generic;
using Nexus.Service.Models.Peripherals;

namespace Nexus.Service.Peripherals;

/// <summary>
/// Hand-curated list of input peripherals Nexus knows about. Users search this to
/// answer "is my device supported?". The modal highlights rows whose VID/PID
/// matches a currently-plugged-in device via the raw USB enumerator.
///
/// A row is a support claim, so only devices Nexus actually drives belong here.
/// Devices whose sole working integration is lighting come from
/// <see cref="LightingDevicesCatalog"/> instead.
/// </summary>
public static class SupportedDevicesCatalog
{
    public static readonly IReadOnlyList<SupportedDeviceDto> All = new List<SupportedDeviceDto>
    {
        // Elgato Stream Deck family (button decks, Plus/Plus XL dials and touch
        // strip, Neo info screen, Studio rings) and the Corsair Galleon K100 SD
        // screen and dials, driven by src/Peripherals/StreamDeck/.
        Deck("Elgato", "Stream Deck Original",            "0x0FD9", "0x0060", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Mini",                "0x0FD9", "0x0063", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Mini MK.2",            "0x0FD9", "0x0090", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Mini Discord",         "0x0FD9", "0x00B3", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Mini MK.2 Module",     "0x0FD9", "0x00B8", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Original V2",          "0x0FD9", "0x006D", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck MK.2",                 "0x0FD9", "0x0080", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck MK.2 Scissor",         "0x0FD9", "0x00A5", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck MK.2 Module",          "0x0FD9", "0x00B9", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck XL",                   "0x0FD9", "0x006C", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck XL V2",                "0x0FD9", "0x008F", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck XL V2 Module",         "0x0FD9", "0x00BA", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck Neo",                  "0x0FD9", "0x009A", Caps("keys", "brightness", "screen")),
        Deck("Elgato", "Stream Deck +",                    "0x0FD9", "0x0084", Caps("keys", "brightness", "screen", "dials")),
        Deck("Elgato", "Stream Deck + XL",                 "0x0FD9", "0x00C6", Caps("keys", "brightness", "screen", "dials")),
        Deck("Elgato", "Stream Deck Studio",               "0x0FD9", "0x00AA", Caps("keys", "brightness", "screen", "dials")),
        Deck("Corsair", "Galleon K100 SD",                 "0x1B1C", "0x2B18", Caps("keys", "brightness", "screen", "dials")),
        Deck("Elgato", "Stream Deck Pedal",                "0x0FD9", "0x0086", Caps("keys")),
    };

    // These peripherals are driven by Nexus's native protocol stack (not OpenRGB),
    // so every row is sourced "nexus".
    private static SupportedDeviceDto Deck(string vendor, string model, string vid, string pid, List<string> caps) =>
        new() { Vendor = vendor, Model = model, Category = "controller", VendorId = vid, ProductId = pid, Capabilities = caps, Source = "nexus" };

    private static List<string> Caps(params string[] values) => new(values);
}
