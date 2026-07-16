using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

/// <summary>
/// Elgato Device.Model code to (cols, rows, label). Codes not in the table
/// (including unverified variants like 20GBD9901LL, and the variable-grid
/// VSD2/WiFi virtual deck) fall back to inferring the grid from the profile's
/// own observed action coordinates.
/// </summary>
public static class ElgatoModelCatalog
{
    private static readonly Dictionary<string, (int Cols, int Rows, string Label)> Known = new(StringComparer.Ordinal)
    {
        ["20GAI9901"] = (3, 2, "Stream Deck Mini"),
        ["20GAA9901"] = (5, 3, "Stream Deck"),
        ["20GAA9902"] = (5, 3, "Stream Deck"),
        ["20GBJ9901"] = (4, 2, "Stream Deck Neo"),
        ["20GBD9901"] = (4, 2, "Stream Deck +"),
    };

    private const string XlFamilyPrefix = "20GAT99";

    public static (int Cols, int Rows, string Label) Resolve(string model, int maxColSeen, int maxRowSeen)
    {
        if (!string.IsNullOrEmpty(model))
        {
            if (Known.TryGetValue(model, out var known))
            {
                return known;
            }
            if (model.StartsWith(XlFamilyPrefix, StringComparison.Ordinal))
            {
                return (8, 4, "Stream Deck XL");
            }
        }

        var cols = Math.Max(1, maxColSeen + 1);
        var rows = Math.Max(1, maxRowSeen + 1);
        return (cols, rows, string.IsNullOrEmpty(model) ? "Stream Deck" : model);
    }
}
