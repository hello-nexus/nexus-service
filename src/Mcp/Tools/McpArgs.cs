using System.Text.Json;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Shared tools/call argument extraction. Manual JsonElement reads, not
/// reflection-bound deserialization, so every write tool stays AOT-safe.
/// </summary>
internal static class McpArgs
{
    public static string? StringArg(JsonElement? args, string name) =>
        args is { } a && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? NumberArg(JsonElement? args, string name) =>
        args is { } a && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static bool BoolArg(JsonElement? args, string name) =>
        args is { } a && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Rounds a numeric arg to the nearest int; null if absent, non-numeric, or non-finite.</summary>
    public static int? IntArg(JsonElement? args, string name) =>
        NumberArg(args, name) is { } d && double.IsFinite(d) ? (int)System.Math.Round(d) : null;

    public static bool TryGetArray(JsonElement? args, string name, out JsonElement array)
    {
        if (args is { } a && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            array = value;
            return true;
        }
        array = default;
        return false;
    }
}
