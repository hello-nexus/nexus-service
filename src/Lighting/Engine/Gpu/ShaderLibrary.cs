using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Loads GLSL fragment sources from per-effect .frag files embedded via
/// &lt;EmbeddedResource&gt; in the csproj. Each shader is authored as a plain
/// .frag file under Lighting/Engine/Gpu/Shaders/ and prefixed at load time
/// with the shared _prelude.frag.
///
/// Embedded rather than loose files because AOT + single-file publish can't
/// find files next to the exe; the assembly manifest is the portable path.
///
/// GLSL 330 core, consumed by the desktop-GL backend on Windows and Linux.
/// macOS needs Metal or GLSL-via-SPIR-V-Cross; see the Gpu/ README.
/// </summary>
internal static class ShaderLibrary
{
    private const string ResourcePrefix = "Nexus.Service.Lighting.Engine.Gpu.Shaders.";
    private const string PreludeResource = ResourcePrefix + "_prelude.frag";

    // Asm-manifest resource names use dots for directory separators, so the
    // prelude shows up as Nexus.Service.Lighting.Engine.Gpu.Shaders._prelude.frag
    // after MSBuild normalises. Cached at first use, no IO on the hot path.
    private static readonly Assembly Asm = typeof(ShaderLibrary).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ShaderParamSpec>> ParamsCache = new();
    private static readonly ConcurrentDictionary<string, string> SourceTagCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<SensorUniformSpec>> SensorCache = new();
    private static readonly string Prelude = LoadRaw(PreludeResource);

    /// <summary>Key prefix of a user-made effect; the rest is the store id.</summary>
    public const string CustomPrefix = "custom:";

    // User-made sources mirrored from CustomEffectStore. Self-contained shaders: no prelude.
    private static readonly ConcurrentDictionary<string, string> CustomSources = new(System.StringComparer.Ordinal);
    // Derived data per custom key, stamped with the source it was computed from; a read
    // whose stamp is not the current source recomputes, so stale data is never served.
    private static readonly ConcurrentDictionary<string, (string Frag, IReadOnlyDictionary<string, ShaderParamSpec> Params, string Tag)> CustomDerived = new();

    public static bool IsCustomKey(string key) => key.StartsWith(CustomPrefix, System.StringComparison.Ordinal);

    /// <summary>True for an embedded effect key or a custom key the store currently holds.</summary>
    public static bool IsKnownKey(string key) =>
        IsCustomKey(key) ? CustomSources.ContainsKey(key[CustomPrefix.Length..]) : AllEffectKeys.Contains(key);

    public static void RegisterCustom(string id, string frag)
    {
        CustomSources[id] = frag;
        CustomDerived.TryRemove(CustomPrefix + id, out _);
    }

    public static void UnregisterCustom(string id)
    {
        CustomSources.TryRemove(id, out _);
        CustomDerived.TryRemove(CustomPrefix + id, out _);
    }

    /// <summary>Whether a source reads any live audio uniform.</summary>
    public static bool ReadsAudio(string frag)
    {
        var i = 0;
        while (i < frag.Length)
        {
            var c = frag[i];
            if (c == '/' && i + 1 < frag.Length && frag[i + 1] == '/')
            {
                while (i < frag.Length && frag[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < frag.Length && frag[i + 1] == '*')
            {
                var end = frag.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                i = end < 0 ? frag.Length : end + 2;
            }
            else if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < frag.Length && (char.IsAsciiLetterOrDigit(frag[i]) || frag[i] == '_'))
                {
                    i++;
                }
                if (AudioUniforms.Contains(frag.Substring(start, i - start)))
                {
                    return true;
                }
            }
            else
            {
                i++;
            }
        }
        return false;
    }

    // Every audio uniform ShaderEffect binds. u_audioBoost alone does not count: it
    // reaches GL through ExtraParams and reads no capture data.
    private static readonly HashSet<string> AudioUniforms = new(System.StringComparer.Ordinal)
    {
        "u_audioLevel", "u_audioBass", "u_audioMid", "u_audioHigh", "u_audioBeat",
        "u_spectrum", "u_spectrum64", "u_specHist", "u_bassPeak", "u_midPeak", "u_highPeak",
    };

    private static (string Frag, IReadOnlyDictionary<string, ShaderParamSpec> Params, string Tag) Derived(string key)
    {
        var frag = CustomSources.TryGetValue(key[CustomPrefix.Length..], out var f)
            ? f
            : throw new FileNotFoundException($"No custom effect for '{key}'.");
        if (CustomDerived.TryGetValue(key, out var d) && ReferenceEquals(d.Frag, frag))
        {
            return d;
        }
        d = (frag, ShaderParamSpec.Parse(frag), Fnv(frag));
        CustomDerived[key] = d;
        return d;
    }

    private static string Fnv(string source)
    {
        uint h = 2166136261;
        foreach (var c in source)
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    private static readonly System.Text.RegularExpressions.Regex OutGainDefinition =
        new(@"float\s+outGain\s*\(\s*\)\s*\{", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Resolve an effect name ("plasma") to its concatenated GLSL source.</summary>
    public static string Get(string effectName)
    {
        if (IsCustomKey(effectName))
        {
            return Derived(effectName).Frag;
        }
        return Cache.GetOrAdd(effectName, key =>
        {
            // Every "simple*" colour key shares the one solid-fill shader; the
            // colour lives entirely in the per-key template tint, not the GLSL.
            var file = key.StartsWith("simple", System.StringComparison.Ordinal) ? "simple" : key;
            var body = LoadRaw(ResourcePrefix + file + ".frag");
            // A sensor effect is a baked Studio fragment that declares everything itself; the prelude would redeclare it.
            if (IsSensorEffect(key))
            {
                return body;
            }
            // The prelude's gainOut() calls outGain(); a body that tonemaps defines its own.
            var unityGain = OutGainDefinition.IsMatch(body) ? "" : "\nfloat outGain() { return 1.0; }\n";
            return Prelude + "\n" + body + unityGain;
        });
    }

    /// <summary>FNV-1a hash of the effect's composed source, cached like Get. Thumbnail
    /// ETags carry it, so an edited shader never revalidates as its old image.</summary>
    public static string SourceTag(string effectName) =>
        IsCustomKey(effectName)
            ? Derived(effectName).Tag
            : SourceTagCache.GetOrAdd(effectName, key => Fnv(Get(key)));

    /// <summary>Sensor uniforms with their authored default bindings, parsed from the effect's source, cached like Get.</summary>
    public static IReadOnlyList<SensorUniformSpec> SensorUniforms(string effectName) =>
        IsCustomKey(effectName)
            ? SensorUniformSpec.Parse(Get(effectName))
            : SensorCache.GetOrAdd(effectName, key => SensorUniformSpec.Parse(Get(key)));

    /// <summary>hint_range specs parsed from the effect's composed source, cached like Get.</summary>
    public static IReadOnlyDictionary<string, ShaderParamSpec> Params(string effectName) =>
        IsCustomKey(effectName)
            ? Derived(effectName).Params
            : ParamsCache.GetOrAdd(effectName, key => ShaderParamSpec.Parse(Get(key)));

    // ── Named accessors kept for call-site clarity ─────────────────────────
    // Every switch arm in LightingProvider.BuildAnimateEffect references these.
    public static string Rainbow => Get("rainbow");
    public static string Plasma => Get("plasma");
    public static string Fire => Get("fire");
    public static string Spiral => Get("spiral");
    public static string Matrix => Get("matrix");
    public static string Meteor => Get("meteor");
    public static string Ripple => Get("ripple");
    public static string Wave => Get("wave");
    public static string GradientWave => Get("gradientwave");
    public static string Ball => Get("ball");
    public static string Radar => Get("radar");
    public static string Pulse => Get("pulse");
    public static string Watercolor => Get("watercolor");
    public static string Jellyfish => Get("jellyfish");
    public static string Aurora => Get("aurora");
    public static string LavaLamp => Get("lavalamp");
    public static string Starfield => Get("starfield");
    public static string VoronoiCells => Get("voronoi");
    public static string NeonRain => Get("neonrain");
    public static string Bursts => Get("bursts");
    public static string Nebula => Get("nebula");
    public static string LavaFissure => Get("lavafissure");
    public static string Kaleidoscope => Get("kaleidoscope");
    public static string Wormhole => Get("wormhole");
    public static string Interference => Get("interference");
    public static string SacredGeometry => Get("sacredgeometry");
    public static string Tessellation => Get("tessellation");
    public static string DomainWarp => Get("domainwarp");
    public static string InkBloom => Get("inkbloom");
    public static string CosmicDust => Get("cosmicdust");
    public static string ChromaSpiral => Get("chromaspiral");
    public static string NeonGrid => Get("neongrid");
    public static string OilSlick => Get("oilslick");
    public static string Bubbles => Get("bubbles");
    public static string SilkWave => Get("silkwave");
    public static string PrismWave => Get("prismwave");
    public static string CrystalTunnel => Get("crystaltunnel");
    public static string RibbonFlow => Get("ribbonflow");
    public static string BeatBuilder => Get("beatbuilder");

    /// <summary>Internal shader for the Screen Mirror Reactive sub-mode. Not user-selectable.</summary>
    internal static string ReactiveGlow => Get("reactiveglow");

    /// <summary>Every registered effect key. Most match a .frag filename; the
    /// "simple*" keys all alias the shared simple.frag (see Get above).</summary>
    private static readonly string[] AnimateEffectKeys = new[]
    {
        // Simple solid-colour fills.
        "simplewhite", "simplesoftpink", "simplepink", "simplered", "simpleorange",
        "simpleyellow", "simplegreen", "simpledarkgreen", "simplecyan",
        "simpleblue", "simpleviolet",
        "rainbow", "plasma", "fire", "spiral", "matrix", "meteor",
        "ripple", "wave", "gradientwave", "ball", "radar", "pulse",
        "watercolor", "jellyfish", "aurora", "lavalamp", "starfield",
        "voronoi", "neonrain", "bursts", "nebula",
        "lavafissure", "kaleidoscope", "wormhole",
        "interference",
        "sacredgeometry", "tessellation", "domainwarp",
        "inkbloom", "cosmicdust", "chromaspiral",
        "neongrid", "oilslick",
        "caustics", "galaxy", "starpath",
        "plasmaglobe", "lightning", "flowfield", "ferrofluid",
        "liquidchrome",
        "hextunnel", "mandelbrot", "circuit",
        "bokeh", "sandstorm", "dotmatrix",
        "bubbles", "silkwave",
        "prismwave", "crystaltunnel", "ribbonflow",
        // Tunnels + flowy + abstract backgrounds.
        "ringtunnel", "vortextunnel", "helixtunnel", "boxtunnel",
        "meshgradient", "tide", "vapor", "satinflow",
        "ridgeline", "chevron", "terrace", "harlequin", "mosaic",
        "sharplines", "breathing", "clockfill",
        // Constellation mesh plus the Nexus 2 theme set.
        "constellation", "cybertunnel", "hyperspace",
        "synthwave", "retropetals", "contourbands",
        // Audio-reactive set; mirrors AudioEffectKeys below, keep in sync.
        "spectrumbars", "spectrumradial", "scope", "basspulse",
        "beatstrobe", "harmonicstar", "audiotunnel", "bassbloom",
        "beatbuilder", "spectrumaurora", "neonwaveform", "liquidbeat",
        "beatburst", "lavaribbons", "lavahaze",
        // Full-frame set members with their own key; the rest are listed above.
        "sweepcycle", "sweepbrush", "sweepliquid", "sweepcomet",
    };

    /// <summary>Effects that sample the whole canvas on every device, simple mode's
    /// set. Each is also a regular animate effect with preset slots. Their stock
    /// looks travel horizontally, or not at all (breathing and cycle do not travel; the
    /// whole frame is one colour). Brush is the exception: its stock slot is rotated off
    /// the horizontal, so <see cref="IsFullFrame"/> returns false for it and it samples the canvas.
    /// Any other y-travelling stock look added here would collapse every linear strip to one
    /// colour under <see cref="LightingEngine.FullFrameSampling"/>.</summary>
    public static readonly HashSet<string> SweepEffectKeys = new(System.StringComparer.Ordinal)
    {
        "rainbow", "breathing", "sharplines", "sweepcycle",
        "sweepbrush", "sweepliquid", "sweepcomet",
    };

    public static bool IsSweepEffect(string key) => SweepEffectKeys.Contains(key);

    /// <summary>Whether a running effect samples the whole canvas on every device. Rainbow and sharplines
    /// travel along a rotated axis once rotated, and brush once it leaves the horizontal, which a full-frame
    /// read would flatten to one color per strip (or to blocks that change at once), so they use canvas
    /// sampling then.</summary>
    public static bool IsFullFrame(string key, IReadOnlyDictionary<string, float>? extras)
    {
        if (!SweepEffectKeys.Contains(key))
        {
            return false;
        }
        if (key is not ("rainbow" or "sharplines" or "sweepbrush")
            || extras is null || !extras.TryGetValue("u_rotation", out var rotation))
        {
            return true;
        }
        if (key == "sweepbrush")
        {
            var half = System.Math.Abs(rotation) % 180f;
            return half < 0.0001f || half > 180f - 0.0001f;
        }
        return System.Math.Abs(rotation) <= 0.0001f;
    }

    /// <summary>Key prefix of a built-in sensor effect: a self-contained baked Studio fragment, shipped as
    /// Shaders/sensor&lt;name&gt;.frag and registered by that file alone.</summary>
    public const string SensorPrefix = "sensor";

    /// <summary>Every embedded Shaders/sensor*.frag, found by name so a new file needs no code change.</summary>
    private static readonly string[] SensorEffectKeys = Asm.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix + SensorPrefix, System.StringComparison.Ordinal)
            && n.EndsWith(".frag", System.StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^".frag".Length])
        .OrderBy(n => n, System.StringComparer.Ordinal)
        .ToArray();

    private static readonly HashSet<string> SensorEffectKeySet = new(SensorEffectKeys, System.StringComparer.Ordinal);

    public static bool IsSensorEffect(string key) => SensorEffectKeySet.Contains(key);

    /// <summary>
    /// Every key with its own .frag, animate plus the static catalog. The client
    /// fetches sources through /lighting/shaders/{name}, which gates on this, so
    /// a key missing here renders server-side only and the local preview falls
    /// back to the streamed canvas.
    /// </summary>
    public static IReadOnlyList<string> AllEffectKeys { get; } =
        AnimateEffectKeys.Concat(SensorEffectKeys).Concat(Nexus.Service.Lighting.StaticEffectCatalog.Patterns).ToArray();

    // Mirrors the audio-reactive block of AllEffectKeys above; keep the two in sync.
    public static readonly HashSet<string> AudioEffectKeys = new(System.StringComparer.Ordinal)
    {
        "spectrumbars", "spectrumradial", "scope", "basspulse",
        "beatstrobe", "harmonicstar", "audiotunnel", "bassbloom",
        "beatbuilder", "spectrumaurora", "neonwaveform", "liquidbeat",
        "beatburst", "lavaribbons", "lavahaze",
    };

    public static bool IsAudioEffect(string key) =>
        IsCustomKey(key)
            ? CustomSources.TryGetValue(key[CustomPrefix.Length..], out var frag) && ReadsAudio(frag)
            : AudioEffectKeys.Contains(key);

    private static string LoadRaw(string resourceName)
    {
        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded shader resource '{resourceName}' missing. " +
                $"Ensure <EmbeddedResource Include=\"Lighting\\Engine\\Gpu\\Shaders\\**\\*.frag\"/> is in the csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
