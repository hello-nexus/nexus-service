using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Peripherals.LianLiFlex;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiFlex;

/// <summary>One recorded effect animation with the settings that produced it.</summary>
public sealed class FlexEffectVector
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required FlexFamily Family { get; init; }
    public required string Key { get; init; }
    public required string Scope { get; init; }
    public required int Direction { get; init; }
    public required int BrightnessPercent { get; init; }
    public required int SpeedMultiplier { get; init; }
    public required int RightAttach { get; init; }
    public required RgbColor[] Colors { get; init; }
    public required int Leds { get; init; }
    public required int Frames { get; init; }
    public required double IntervalMs { get; init; }
    public required bool ZoneOnly { get; init; }
    public required byte[] Raw { get; init; }
    public bool UpDownSwap { get; init; }
    public IReadOnlyDictionary<string, FlexZoneLook>? Zones { get; init; }

    public int BrightnessLevel => BrightnessPercent switch { 0 => 0, 25 => 64, 50 => 128, 75 => 192, _ => 255 };

    public override string ToString() => Id;

    private static IReadOnlyList<FlexEffectVector>? cache;

    public static IReadOnlyList<FlexEffectVector> All => cache ??= Load();

    public static FlexEffectVector Get(string id) => All.Single(v => v.Id == id);

    public static IEnumerable<object[]> Ids(Func<FlexEffectVector, bool> filter) =>
        All.Where(filter).Select(v => new object[] { v.Id });

    private static Dictionary<string, FlexZoneLook> ReadZones(JsonElement zs) =>
        zs.EnumerateObject().ToDictionary(p => p.Name, p => new FlexZoneLook(
            p.Value.GetProperty("key").GetString()!,
            p.Value.GetProperty("speed").GetInt32(),
            p.Value.GetProperty("direction").GetInt32(),
            p.Value.GetProperty("brightness").GetInt32(),
            p.Value.GetProperty("colors").EnumerateArray().Select(c => new RgbColor(c[0].GetByte(), c[1].GetByte(), c[2].GetByte())).ToArray()));

    private static IReadOnlyList<FlexEffectVector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LianLiFlex", "Data", "flex-effect-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var list = new List<FlexEffectVector>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add(new FlexEffectVector
            {
                Id = e.GetProperty("id").GetString()!,
                Kind = e.GetProperty("kind").GetString()!,
                Family = e.GetProperty("family").GetString() == "tlFlex" ? FlexFamily.TlFlex : FlexFamily.SlInfFlex,
                Key = e.GetProperty("key").GetString()!,
                Scope = e.GetProperty("scope").GetString()!,
                Direction = e.GetProperty("direction").GetInt32(),
                BrightnessPercent = e.GetProperty("brightnessPercent").GetInt32(),
                SpeedMultiplier = e.GetProperty("speedMultiplier").GetInt32(),
                RightAttach = e.GetProperty("rightAttach").GetInt32(),
                Colors = e.GetProperty("colors").EnumerateArray()
                    .Select(c => new RgbColor(c[0].GetByte(), c[1].GetByte(), c[2].GetByte())).ToArray(),
                Leds = e.GetProperty("leds").GetInt32(),
                Frames = e.GetProperty("frames").GetInt32(),
                IntervalMs = e.GetProperty("intervalMs").GetDouble(),
                ZoneOnly = e.GetProperty("zoneOnly").GetBoolean(),
                Raw = Convert.FromBase64String(e.GetProperty("framesB64").GetString()!),
                UpDownSwap = e.TryGetProperty("upDownSwap", out var sw) && sw.GetBoolean(),
                Zones = e.TryGetProperty("zones", out var zs) ? ReadZones(zs) : null,
            });
        }
        return list;
    }
}
