using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class Slv3FlexLcdThemeDto
{
    public int Index { get; set; }
    public int ColorCount { get; set; }
    public string[] Defaults { get; set; } = Array.Empty<string>();
}

public sealed class Slv3FlexLcdFanDto
{
    public int Theme { get; set; }
    public int Source { get; set; }
    /// <summary>"#RRGGBB" per editable color of the theme, or null for the theme's defaults.</summary>
    public string[]? Colors { get; set; }
}

public sealed class Slv3FlexLcdChainDto
{
    public string Mac { get; set; } = "";
    /// <summary>"tlFlex" or "slInfFlex".</summary>
    public string Family { get; set; } = "";
    public int FanCount { get; set; }
    public bool Enabled { get; set; }
    public int Brightness { get; set; }
    public int Rotation { get; set; }
    /// <summary>TL chains only: the two halves of each fan are exchanged; null for other families.</summary>
    public bool? UpDownSwap { get; set; }
    public Slv3FlexLcdFanDto[] Fans { get; set; } = Array.Empty<Slv3FlexLcdFanDto>();
}

public sealed class Slv3FlexLcdResponse
{
    public int SourceCount { get; set; }
    public Slv3FlexLcdThemeDto[] Themes { get; set; } = Array.Empty<Slv3FlexLcdThemeDto>();
    public Slv3FlexLcdChainDto[] Chains { get; set; } = Array.Empty<Slv3FlexLcdChainDto>();
}

public sealed class Slv3FlexLcdFanPatch
{
    public int Index { get; set; }
    public int? Theme { get; set; }
    public int? Source { get; set; }
    /// <summary>Colors for the fan's current theme; an empty array returns to the theme's defaults.</summary>
    public string[]? Colors { get; set; }
}

/// <summary>Patch for one chain's wireless themes; null fields keep their value.</summary>
public sealed class Slv3FlexLcdRequest
{
    public bool? Enabled { get; set; }
    public int? Brightness { get; set; }
    public int? Rotation { get; set; }
    /// <summary>TL chains only; stored with the chain's light settings.</summary>
    public bool? UpDownSwap { get; set; }
    public Slv3FlexLcdFanPatch[]? Fans { get; set; }
}

/// <summary>Wireless themes of the TL / SL-INF LCD Flex fans bound to this PC's radio.</summary>
public static class Slv3FlexLcdRoutes
{
    public static void MapSlv3FlexLcdEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli-wireless/flex-lcd", (Slv3Hub hub, IConfigStore store) =>
            Results.Json(BuildResponse(hub.State, store.Load()), AppJsonContext.Default.Slv3FlexLcdResponse));

        app.MapPut("/devices/lianli-wireless/flex-lcd/{mac}", (string mac, Slv3FlexLcdRequest body, Slv3Hub hub, IConfigStore store, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var (status, error, swapChanged) = Put(mac, body, hub.State, store);
            if (swapChanged)
            {
                Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            }
            return status == 200
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.Json(ApiResponse.Fail(error ?? ""), AppJsonContext.Default.ApiResponse, statusCode: status);
        });
    }

    /// <summary>Validates and stores one chain's patch; a rejected patch writes nothing. Reports whether the up/down swap changed.</summary>
    internal static (int Status, string? Error, bool SwapChanged) Put(string mac, Slv3FlexLcdRequest body, Slv3State state, IConfigStore store)
    {
        var chain = EligibleChains(state).FirstOrDefault(f => string.Equals(f.Mac, mac, StringComparison.OrdinalIgnoreCase));
        if (chain is null)
        {
            return (404, "not an LCD Flex fan bound to this PC", false);
        }
        var chainKey = chain.Mac.ToUpperInvariant();
        var isTl = Slv3Protocol.ClassifyFanFamily((byte)chain.FanType) != Slv3FanFamily.SlInfFlex;
        if (body.UpDownSwap is not null && !isTl)
        {
            return (400, "up/down swap is not supported", false);
        }
        store.Load().Devices.LianLiWireless.FlexLcd.TryGetValue(chainKey, out var snapshot);
        if (Validate(body, chain.FanCount, snapshot) is { } early)
        {
            return (400, early, false);
        }
        string? error = null;
        var swapChanged = false;
        store.Update(s =>
        {
            s.Devices.LianLiWireless.FlexLcd.TryGetValue(chainKey, out var current);
            error = Validate(body, chain.FanCount, current);
            if (error is not null)
            {
                return;
            }
            Apply(s.Devices.LianLiWireless.FlexLcd, chainKey, chain.FanCount, body);
            if (body.UpDownSwap is { } swap)
            {
                s.Devices.FlexUnits.TryGetValue(chainKey, out var look);
                swapChanged = (look?.UpDownSwap ?? false) != swap;
                var next = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits, StringComparer.Ordinal)
                {
                    [chainKey] = LianLiFlexRoutes.Patch(look ?? new LianLiFlexUnitSettings(), new LianLiFlexChainLightingRequest { UpDownSwap = swap }, Nexus.Service.Peripherals.LianLiFlex.FlexFamily.TlFlex),
                };
                s.Devices.FlexUnits = next;
            }
        });
        return error is null ? (200, null, swapChanged) : (400, error, false);
    }

    internal static IEnumerable<Slv3FanInfo> EligibleChains(Slv3State state) =>
        state.Fans.Where(f => f.FlexLcdEligible);

    internal static Slv3FlexLcdResponse BuildResponse(Slv3State state, NexusSettings settings)
    {
        var themes = new Slv3FlexLcdThemeDto[Slv3FlexLcd.ThemeCount];
        for (var i = 0; i < themes.Length; i++)
        {
            themes[i] = new Slv3FlexLcdThemeDto
            {
                Index = i,
                ColorCount = Slv3FlexLcd.ColorCount(i),
                Defaults = Slv3FlexLcd.DefaultColors(i).Select(Hex).ToArray(),
            };
        }
        var chains = new List<Slv3FlexLcdChainDto>();
        foreach (var fan in EligibleChains(state))
        {
            settings.Devices.LianLiWireless.FlexLcd.TryGetValue(fan.Mac.ToUpperInvariant(), out var saved);
            var count = Math.Min(Slv3FlexLcd.MaxFans, fan.FanCount);
            var fans = new Slv3FlexLcdFanDto[count];
            for (var i = 0; i < count; i++)
            {
                var f = saved is not null && i < saved.Fans.Count ? saved.Fans[i] : new LianLiFlexWirelessLcdFan();
                fans[i] = new Slv3FlexLcdFanDto { Theme = f.Theme, Source = f.Source, Colors = f.Colors?.ToArray() };
            }
            chains.Add(new Slv3FlexLcdChainDto
            {
                Mac = fan.Mac,
                Family = Slv3Protocol.ClassifyFanFamily((byte)fan.FanType) == Slv3FanFamily.SlInfFlex ? "slInfFlex" : "tlFlex",
                FanCount = count,
                Enabled = saved?.Enabled ?? false,
                Brightness = saved?.Brightness ?? 60,
                Rotation = saved?.Rotation ?? 3,
                UpDownSwap = Slv3Protocol.ClassifyFanFamily((byte)fan.FanType) == Slv3FanFamily.SlInfFlex ? null
                    : settings.Devices.FlexUnits.TryGetValue(fan.Mac.ToUpperInvariant(), out var look) && look.UpDownSwap,
                Fans = fans,
            });
        }
        return new Slv3FlexLcdResponse { SourceCount = Slv3FlexLcd.DataSourceCount, Themes = themes, Chains = chains.ToArray() };
    }

    internal static string? Validate(Slv3FlexLcdRequest body, int fanCount, LianLiFlexWirelessLcdSettings? current = null)
    {
        if (body.Brightness is < 1 or > 100)
        {
            return "brightness must be 1-100";
        }
        if (body.Rotation is < 0 or > 3)
        {
            return "rotation must be 0-3";
        }
        foreach (var f in body.Fans ?? Array.Empty<Slv3FlexLcdFanPatch>())
        {
            if (f.Index < 0 || f.Index >= Math.Min(Slv3FlexLcd.MaxFans, fanCount))
            {
                return "fan index out of range";
            }
            if (f.Theme is < 0 or >= Slv3FlexLcd.ThemeCount)
            {
                return "theme out of range";
            }
            if (f.Source is < 0 or >= Slv3FlexLcd.DataSourceCount)
            {
                return "source out of range";
            }
            if (f.Colors is { Length: > 0 } colors)
            {
                if (colors.Any(c => !TryParseHex(c, out _)))
                {
                    return "colors must be #RRGGBB";
                }
                var theme = f.Theme ?? (current is not null && f.Index < current.Fans.Count ? current.Fans[f.Index].Theme : 0);
                if (colors.Length != Slv3FlexLcd.ColorCount(theme))
                {
                    return "wrong number of colors for the theme";
                }
            }
        }
        return null;
    }

    internal static void Apply(Dictionary<string, LianLiFlexWirelessLcdSettings> all, string mac, int fanCount, Slv3FlexLcdRequest body)
    {
        if (!all.TryGetValue(mac, out var current))
        {
            current = new LianLiFlexWirelessLcdSettings();
            all[mac] = current;
        }
        var count = Math.Min(Slv3FlexLcd.MaxFans, fanCount);
        while (current.Fans.Count < count)
        {
            current.Fans.Add(new LianLiFlexWirelessLcdFan());
        }
        if (body.Enabled is { } enabled)
        {
            current.Enabled = enabled;
        }
        if (body.Brightness is { } brightness)
        {
            current.Brightness = brightness;
        }
        if (body.Rotation is { } rotation)
        {
            current.Rotation = rotation;
        }
        foreach (var patch in body.Fans ?? Array.Empty<Slv3FlexLcdFanPatch>())
        {
            var fan = current.Fans[patch.Index];
            if (patch.Theme is { } theme && theme != fan.Theme)
            {
                fan.Theme = theme;
                fan.Colors = null;
            }
            if (patch.Source is { } source)
            {
                fan.Source = source;
            }
            if (patch.Colors is { } colors)
            {
                var wanted = Slv3FlexLcd.ColorCount(fan.Theme);
                fan.Colors = colors.Length == wanted && wanted > 0 ? colors.Select(c => c.ToUpperInvariant()).ToList() : null;
            }
        }
    }

    /// <summary>Reads the settings under the store's lock, so a concurrent save cannot tear the enumeration; null when no chain shows a theme.</summary>
    internal static Slv3FlexLcdFrame? BuildFrame(IConfigStore store, Func<SummarySensorKind, float?> read)
    {
        Dictionary<string, Slv3FlexLcdChain>? chains = null;
        store.Read(s => chains = ChainsOf(s));
        return chains is null ? null : FrameOf(chains, read);
    }

    internal static Slv3FlexLcdFrame? BuildFrame(NexusSettings settings, Func<SummarySensorKind, float?> read)
    {
        var chains = ChainsOf(settings);
        return chains is null ? null : FrameOf(chains, read);
    }

    private static Dictionary<string, Slv3FlexLcdChain>? ChainsOf(NexusSettings settings)
    {
        var chains = new Dictionary<string, Slv3FlexLcdChain>(StringComparer.Ordinal);
        foreach (var (mac, s) in settings.Devices.LianLiWireless.FlexLcd)
        {
            var fans = s.Fans.Select(f => new Slv3FlexLcdFan(f.Theme, f.Source, ColorsOf(f))).ToArray();
            chains[mac.ToUpperInvariant()] = new Slv3FlexLcdChain(mac.ToUpperInvariant(), s.Enabled, s.Brightness, s.Rotation, fans);
        }
        return chains.Values.Any(c => c.Enabled) ? chains : null;
    }

    private static Slv3FlexLcdFrame FrameOf(Dictionary<string, Slv3FlexLcdChain> chains, Func<SummarySensorKind, float?> read)
    {
        // The table field is a 16-bit megabyte count, so a larger machine reads as the most it can carry.
        var total = Math.Min(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0, ushort.MaxValue);
        var memoryUsage = read(SummarySensorKind.MemoryUsage) ?? 0f;
        var sensors = new Slv3FlexLcdSensors(
            read(SummarySensorKind.CpuTemp) ?? 0f, read(SummarySensorKind.CpuUsage) ?? 0f,
            read(SummarySensorKind.GpuTemp) ?? 0f, read(SummarySensorKind.GpuUsage) ?? 0f,
            read(SummarySensorKind.CpuClock) ?? 0f, read(SummarySensorKind.GpuClock) ?? 0f,
            0, 0, total, total * memoryUsage / 100.0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new Slv3FlexLcdFrame(chains, sensors);
    }

    private static uint[]? ColorsOf(LianLiFlexWirelessLcdFan fan)
    {
        if (fan.Colors is not { Count: > 0 } list || list.Count != Slv3FlexLcd.ColorCount(fan.Theme))
        {
            return null;
        }
        var result = new uint[list.Count];
        for (var i = 0; i < result.Length; i++)
        {
            if (!TryParseHex(list[i], out result[i]))
            {
                return null;
            }
        }
        return result;
    }

    private static bool TryParseHex(string? text, out uint rgb)
    {
        rgb = 0;
        return text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    private static string Hex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}
