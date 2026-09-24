using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// Runs DeckConfigNavigation.FitToGrid against the shared vectors file
/// (tests/Deck/fitToGrid.vectors.json, copied verbatim to nexus-web's
/// fitToGrid.vectors.json for its own TS suite). Read via the source file's
/// own path rather than a csproj copy-to-output rule, since the vectors file
/// lives outside the test project directory (a sibling of both suites).
/// </summary>
public sealed class FitToGridVectorsTests
{
    public sealed class VectorCase
    {
        public string Name { get; set; } = "";
        public int PresetCols { get; set; }
        public int PresetRows { get; set; }
        public int TargetCols { get; set; }
        public int TargetRows { get; set; }
        public string TargetKind { get; set; } = "";
        public JsonElement Preset { get; set; }
        public JsonElement Expected { get; set; }
    }

    private sealed class VectorFile
    {
        public List<VectorCase> Cases { get; set; } = new();
    }

    private static string VectorsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Deck", "fitToGrid.vectors.json"));

    public static IEnumerable<object[]> Cases()
    {
        var json = File.ReadAllText(VectorsPath());
        var file = JsonSerializer.Deserialize<VectorFile>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        foreach (var c in file.Cases)
        {
            yield return new object[] { c.Name, c };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FitToGrid_MatchesTheSharedVector(string name, VectorCase testCase)
    {
        var preset = JsonSerializer.Deserialize(testCase.Preset.GetRawText(), AppJsonContext.Default.DeckConfig)!;
        var kind = testCase.TargetKind == "widget" ? DeckTargetKind.Widget : DeckTargetKind.Physical;

        var actual = DeckConfigNavigation.FitToGrid(testCase.PresetCols, testCase.PresetRows, preset, testCase.TargetCols, testCase.TargetRows, kind);

        var actualJson = JsonSerializer.Serialize(actual, AppJsonContext.Default.DeckConfig);
        var expectedJson = Normalize(testCase.Expected.GetRawText());
        Assert.True(Normalize(actualJson) == expectedJson, $"{name}: expected {expectedJson}, got {Normalize(actualJson)}");
    }

    /// <summary>Round-trips through JsonDocument so property order/whitespace differences between the hand-written vector and the source-gen writer never fail the comparison.</summary>
    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, AppJsonContext.Default.DeckConfig.Options);
    }
}
