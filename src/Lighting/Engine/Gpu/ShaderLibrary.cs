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
    private static readonly string Prelude = LoadRaw(PreludeResource);

    /// <summary>Resolve an effect name ("plasma") to its concatenated GLSL source.</summary>
    public static string Get(string effectName)
    {
        return Cache.GetOrAdd(effectName, key =>
        {
            // Every "simple*" colour key shares the one solid-fill shader; the
            // colour lives entirely in the per-key template tint, not the GLSL.
            var file = key.StartsWith("simple", System.StringComparison.Ordinal) ? "simple" : key;
            var body = LoadRaw(ResourcePrefix + file + ".frag");
            return Prelude + "\n" + body;
        });
    }

    /// <summary>hint_range specs parsed from the effect's composed source, cached like Get.</summary>
    public static IReadOnlyDictionary<string, ShaderParamSpec> Params(string effectName) =>
        ParamsCache.GetOrAdd(effectName, key => ShaderParamSpec.Parse(Get(key)));

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
        "sharplines",
        // Constellation mesh plus the Nexus 2 theme set.
        "constellation", "cybertunnel", "hyperspace",
        "synthwave", "retropetals", "contourbands",
        // Audio-reactive set; mirrors AudioEffectKeys below, keep in sync.
        "spectrumbars", "spectrumradial", "scope", "basspulse",
        "beatstrobe", "harmonicstar", "audiotunnel", "bassbloom",
        "beatbuilder", "spectrumaurora", "neonwaveform", "liquidbeat",
        "beatburst",
    };

    /// <summary>
    /// Every key with its own .frag, animate plus the static catalog. The client
    /// fetches sources through /lighting/shaders/{name}, which gates on this, so
    /// a key missing here renders server-side only and the local preview falls
    /// back to the streamed canvas.
    /// </summary>
    public static IReadOnlyList<string> AllEffectKeys { get; } =
        AnimateEffectKeys.Concat(Nexus.Service.Lighting.StaticEffectCatalog.Patterns).ToArray();

    // Mirrors the audio-reactive block of AllEffectKeys above; keep the two in sync.
    public static readonly HashSet<string> AudioEffectKeys = new(System.StringComparer.Ordinal)
    {
        "spectrumbars", "spectrumradial", "scope", "basspulse",
        "beatstrobe", "harmonicstar", "audiotunnel", "bassbloom",
        "beatbuilder", "spectrumaurora", "neonwaveform", "liquidbeat",
        "beatburst",
    };

    public static bool IsAudioEffect(string key) => AudioEffectKeys.Contains(key);

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
