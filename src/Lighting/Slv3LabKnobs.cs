using System;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Lighting;

/// <summary>Lab overrides for the SLV3 rolling-window writer, read from a JSON file; an absent or invalid file means stock behavior.</summary>
internal sealed record Slv3LabKnobs
{
    public const int MinWindowFrames = 6;
    public const int MaxWindowFrames = 60;
    public const int MaxExtraUploads = 8;

    public static readonly Slv3LabKnobs Default = new();

    public bool Resend { get; init; } = true;
    public int ForcedExtraUploads { get; init; }
    public bool ExtraSameIndex { get; init; }
    public int? WindowFrames { get; init; }
    public int? Stretch { get; init; }
    public int? CycleMs { get; init; }
    public string TargetMac { get; init; } = "";
    public bool LivePath { get; init; }

    /// <summary>Chain-scoped knobs apply to every chain when no target is set.</summary>
    public bool Targets(string macHex) =>
        TargetMac.Length == 0 || string.Equals(TargetMac, macHex, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string json, out Slv3LabKnobs knobs)
    {
        knobs = Default;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var k = Default;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                switch (p.Name)
                {
                    case "resend" when IsBool(v): k = k with { Resend = v.GetBoolean() }; break;
                    case "forcedExtraUploads" when v.TryGetInt32(out var n): k = k with { ForcedExtraUploads = Math.Clamp(n, 0, MaxExtraUploads) }; break;
                    case "extraSameIndex" when IsBool(v): k = k with { ExtraSameIndex = v.GetBoolean() }; break;
                    case "windowFrames" when v.TryGetInt32(out var f): k = k with { WindowFrames = Math.Clamp(f, MinWindowFrames, MaxWindowFrames) }; break;
                    case "stretch" when v.TryGetInt32(out var s): k = k with { Stretch = Math.Clamp(s, 1, 6) }; break;
                    case "cycleMs" when v.TryGetInt32(out var c): k = k with { CycleMs = Math.Clamp(c, 150, 10_000) }; break;
                    case "targetMac" when v.ValueKind == JsonValueKind.String: k = k with { TargetMac = (v.GetString() ?? "").Trim().ToUpperInvariant() }; break;
                    case "livePath" when IsBool(v): k = k with { LivePath = v.GetBoolean() }; break;
                }
            }
            knobs = k;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBool(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False;

    public override string ToString() =>
        $"resend={Resend} forcedExtraUploads={ForcedExtraUploads} extraSameIndex={ExtraSameIndex} windowFrames={WindowFrames?.ToString() ?? "-"} stretch={Stretch?.ToString() ?? "-"} cycleMs={CycleMs?.ToString() ?? "-"} targetMac={(TargetMac.Length == 0 ? "-" : TargetMac)} livePath={LivePath}";
}

/// <summary>Re-reads the knob file at most every two seconds; the snapshot is replaced whole.</summary>
internal sealed class Slv3LabKnobSource
{
    private const double RefreshMs = 2000;

    private readonly string _path;
    private double _nextReadMs;
    private bool _wasInvalid;

    public Slv3LabKnobSource(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "slv3-lab.json");
    }

    public string FilePath => _path;

    public Slv3LabKnobs Current { get; private set; } = Slv3LabKnobs.Default;

    /// <summary>True when the snapshot changed on this call; <paramref name="invalid"/> flags a file that failed to parse (the last good snapshot stays).</summary>
    public bool Refresh(double nowMs, out bool invalid)
    {
        invalid = false;
        if (nowMs < _nextReadMs) return false;
        _nextReadMs = nowMs + RefreshMs;
        var next = Slv3LabKnobs.Default;
        try
        {
            if (File.Exists(_path))
            {
                if (!Slv3LabKnobs.TryParse(File.ReadAllText(_path), out next))
                {
                    invalid = !_wasInvalid;
                    _wasInvalid = true;
                    return false;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        _wasInvalid = false;
        if (next == Current) return false;
        Current = next;
        return true;
    }
}
