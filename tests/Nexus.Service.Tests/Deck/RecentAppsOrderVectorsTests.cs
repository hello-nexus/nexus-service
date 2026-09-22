using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// Runs RecentAppsTracker.StableOrder against the shared vectors file
/// (tests/Deck/recentAppsOrder.vectors.json, copied verbatim to nexus-web
/// for its stableRecentAppsOrder suite). Reads the vectors with plain
/// reflection-based JSON like RecentAppsViewVectorsTests.
/// </summary>
public sealed class RecentAppsOrderVectorsTests
{
    public sealed class VectorCase
    {
        public string Name { get; set; } = "";
        public int Cols { get; set; }
        public int Rows { get; set; }
        public int CurrentPage { get; set; }
        public string? PreviousFocused { get; set; }
        public string? FocusedProcessKey { get; set; }
        public List<string> Ring { get; set; } = new();
        public List<string>? PreviousOrder { get; set; }
        public List<string> Expected { get; set; } = new();
    }

    private sealed class VectorFile
    {
        public List<VectorCase> Cases { get; set; } = new();
    }

    private static string VectorsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Deck", "recentAppsOrder.vectors.json"));

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
    public void StableOrder_MatchesTheSharedVector(string name, VectorCase testCase)
    {
        var ring = testCase.Ring.ConvertAll(key => new RecentApp { ProcessKey = key, Name = key.ToUpperInvariant() });

        var actual = RecentAppsTracker.StableOrder(ring, testCase.PreviousOrder, testCase.PreviousFocused, testCase.FocusedProcessKey, testCase.Cols, testCase.Rows, testCase.CurrentPage);

        Assert.True(string.Join(",", testCase.Expected) == string.Join(",", actual.ConvertAll(a => a.ProcessKey)),
            $"{name}: expected [{string.Join(",", testCase.Expected)}], got [{string.Join(",", actual.ConvertAll(a => a.ProcessKey))}]");
    }
}
