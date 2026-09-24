using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// Runs RecentAppsTracker.BuildView against the shared vectors file
/// (tests/Deck/recentAppsView.vectors.json, copied verbatim to nexus-web's
/// own copy for its TS suite). RecentKey is not a wire type (it never leaves
/// the process), so this reads the vectors with plain reflection-based JSON
/// like FitToGridVectorsTests does for its own fixture-only types.
/// </summary>
public sealed class RecentAppsViewVectorsTests
{
    public sealed class RingEntryVector
    {
        public string ProcessKey { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Pid { get; set; }
        public string? ExePath { get; set; }
        public string? ShortcutId { get; set; }
    }

    public sealed class KeyVector
    {
        public string Kind { get; set; } = "";
        public string? ProcessKey { get; set; }
        public string? Name { get; set; }
        public string? ShortcutId { get; set; }
        public string? ExePath { get; set; }
        public bool Focused { get; set; }
    }

    public sealed class VectorCase
    {
        public string Name { get; set; } = "";
        public int Cols { get; set; }
        public int Rows { get; set; }
        public string? FocusedProcessKey { get; set; }
        public List<RingEntryVector> Ring { get; set; } = new();
        public List<List<KeyVector>> Expected { get; set; } = new();
    }

    private sealed class VectorFile
    {
        public List<VectorCase> Cases { get; set; } = new();
    }

    private static string VectorsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Deck", "recentAppsView.vectors.json"));

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
    public void BuildView_MatchesTheSharedVector(string name, VectorCase testCase)
    {
        var ring = testCase.Ring.ConvertAll(r => new RecentApp
        {
            ProcessKey = r.ProcessKey,
            Name = r.Name,
            Pid = r.Pid,
            ExePath = r.ExePath,
            ShortcutId = r.ShortcutId,
        });

        var actual = RecentAppsTracker.BuildView(ring, testCase.FocusedProcessKey, testCase.Cols, testCase.Rows);

        Assert.Equal(testCase.Expected.Count, actual.Count);
        for (var p = 0; p < testCase.Expected.Count; p++)
        {
            var expectedPage = testCase.Expected[p];
            var actualPage = actual[p];
            Assert.True(expectedPage.Count == actualPage.Count, $"{name}: page {p} key count expected {expectedPage.Count}, got {actualPage.Count}");
            for (var k = 0; k < expectedPage.Count; k++)
            {
                var e = expectedPage[k];
                var a = actualPage[k];
                Assert.True(e.Kind == a.Kind, $"{name}: page {p} key {k} kind expected {e.Kind}, got {a.Kind}");
                Assert.True(e.ProcessKey == a.ProcessKey, $"{name}: page {p} key {k} processKey expected {e.ProcessKey}, got {a.ProcessKey}");
                Assert.True(e.Name == a.Name, $"{name}: page {p} key {k} name expected {e.Name}, got {a.Name}");
                Assert.True(e.ShortcutId == a.ShortcutId, $"{name}: page {p} key {k} shortcutId expected {e.ShortcutId}, got {a.ShortcutId}");
                Assert.True(e.ExePath == a.ExePath, $"{name}: page {p} key {k} exePath expected {e.ExePath}, got {a.ExePath}");
                Assert.True(e.Focused == a.Focused, $"{name}: page {p} key {k} focused expected {e.Focused}, got {a.Focused}");
            }
        }
    }
}
