using System;
using System.Collections.Generic;

namespace Nexus.Service.Cooling;

/// <summary>Machine-local 3D cooling layout: airflow direction overrides, fan channel bindings and saved virtual tests.</summary>
public sealed class CoolingLayout
{
    /// <summary>Slot key to <c>intake</c> or <c>exhaust</c>; only overrides of the default.</summary>
    public Dictionary<string, string> Directions { get; set; } = new();
    public List<CoolingBinding> Bindings { get; set; } = new();
    public List<CoolingTest> Tests { get; set; } = new();
}

public sealed class CoolingBinding
{
    /// <summary>The <c>FanChannel.id</c> driving these slots.</summary>
    public string ChannelId { get; set; } = "";
    public List<string> SlotKeys { get; set; } = new();
}

public sealed class CoolingTest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<CoolingMove> Moves { get; set; } = new();
    public Dictionary<string, string> Directions { get; set; } = new();
}

public sealed class CoolingMove
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>The file: a revision that increments on every save, and the layout.</summary>
public sealed class CoolingLayoutFile
{
    public int Rev { get; set; }
    public CoolingLayout Layout { get; set; } = new();
}

public static class CoolingLayoutValidation
{
    public const int MaxBindings = 64;
    public const int MaxSlotKeys = 16;
    public const int MaxTests = 16;
    public const int MaxMoves = 64;
    public const int MaxKey = 128;
    public const int MaxName = 64;
    // Directions are bounded by the body cap, not a count: one entry per fan slot of one case.
    public const int MaxDirections = 512;
    public const long MaxBodyBytes = 256 * 1024;

    /// <summary>The first problem found, or null when the layout is storable.</summary>
    public static string? Validate(CoolingLayout? layout)
    {
        if (layout is null)
        {
            return "layout is required";
        }
        if (layout.Directions is null || layout.Bindings is null || layout.Tests is null)
        {
            return "directions, bindings and tests are required";
        }
        if (DirectionsError(layout.Directions) is { } dirError)
        {
            return dirError;
        }
        if (layout.Bindings.Count > MaxBindings)
        {
            return $"at most {MaxBindings} bindings";
        }
        foreach (var b in layout.Bindings)
        {
            if (b is null || !KeyOk(b.ChannelId) || b.SlotKeys is null)
            {
                return "each binding needs a channelId of 1 to 128 characters and slotKeys";
            }
            if (b.SlotKeys.Count > MaxSlotKeys)
            {
                return $"at most {MaxSlotKeys} slotKeys per binding";
            }
            if (!b.SlotKeys.TrueForAll(KeyOk))
            {
                return "slotKeys must be 1 to 128 characters";
            }
        }
        if (layout.Tests.Count > MaxTests)
        {
            return $"at most {MaxTests} tests";
        }
        foreach (var t in layout.Tests)
        {
            if (t is null || !KeyOk(t.Id) || t.Name is null || t.Name.Length > MaxName || t.Moves is null || t.Directions is null)
            {
                return $"each test needs an id of 1 to 128 characters, a name up to {MaxName} characters, moves and directions";
            }
            if (t.Moves.Count > MaxMoves)
            {
                return $"at most {MaxMoves} moves per test";
            }
            if (!t.Moves.TrueForAll(m => m is not null && KeyOk(m.From) && KeyOk(m.To)))
            {
                return "move from and to must be 1 to 128 characters";
            }
            if (DirectionsError(t.Directions) is { } testDirError)
            {
                return testDirError;
            }
        }
        return null;
    }

    private static string? DirectionsError(Dictionary<string, string> directions)
    {
        if (directions.Count > MaxDirections)
        {
            return $"at most {MaxDirections} directions";
        }
        foreach (var (key, value) in directions)
        {
            if (!KeyOk(key))
            {
                return "direction keys must be 1 to 128 characters";
            }
            if (value is not ("intake" or "exhaust"))
            {
                return "direction must be intake or exhaust";
            }
        }
        return null;
    }

    private static bool KeyOk(string? s) => !string.IsNullOrEmpty(s) && s.Length <= MaxKey;
}
