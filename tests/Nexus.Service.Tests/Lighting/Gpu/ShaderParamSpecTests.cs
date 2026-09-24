using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// Coverage for the hint_range parser described in the shader param ranges
/// contract: every tunable uniform's range comes from the GLSL itself, never
/// a hand-maintained C# table.
/// </summary>
public class ShaderParamSpecTests
{
    [Fact]
    public void Parse_DeclarationLine_CapturesRangeStepAndDefault()
    {
        var specs = ShaderParamSpec.Parse("uniform float u_zoom; // hint_range(0.5, 3.0, 0.05) = 1.0\n");

        var spec = specs["u_zoom"];
        Assert.Equal(0.5f, spec.Min);
        Assert.Equal(3.0f, spec.Max);
        Assert.Equal(0.05f, spec.Step);
        Assert.Equal(1.0f, spec.Default);
        Assert.True(spec.Declared);
    }

    [Fact]
    public void Parse_CommentOnlyOverride_NarrowsRangeAndStaysDeclared()
    {
        var src = "uniform float u_saturation; // hint_range(0.0, 4.0, 0.01) = 1.0\n"
                + "// u_saturation hint_range(0.4, 1.0, 0.01) = 1.0\n";

        var spec = ShaderParamSpec.Parse(src)["u_saturation"];
        Assert.Equal(0.4f, spec.Min);
        Assert.Equal(1.0f, spec.Max);
        // The override line itself isn't a `uniform` line, but the earlier
        // declaration line already marked this name Declared.
        Assert.True(spec.Declared);
    }

    [Fact]
    public void Parse_CommentOnlyLine_WithNoPriorDeclaration_IsNotDeclared()
    {
        var spec = ShaderParamSpec.Parse("// u_hue hint_range(0.0, 1.0, 0.01) = 0.0\n")["u_hue"];
        Assert.False(spec.Declared);
    }

    [Fact]
    public void Parse_MissingStepAndDefault_FallsBackToPointOhOneAndMin()
    {
        var spec = ShaderParamSpec.Parse("uniform float u_thing; // hint_range(2.0, 8.0)\n")["u_thing"];
        Assert.Equal(0.01f, spec.Step);
        Assert.Equal(2.0f, spec.Default);
    }

    [Fact]
    public void Parse_TwoIdentifiersOnOneLine_NameIsTheLastBeforeHintRange()
    {
        var specs = ShaderParamSpec.Parse("// u_foo relates to u_bar hint_range(1.0, 2.0) = 1.5\n");
        Assert.False(specs.ContainsKey("u_foo"));
        Assert.True(specs.ContainsKey("u_bar"));
    }

    [Fact]
    public void Parse_LaterLine_OverridesAnEarlierRangeForTheSameName()
    {
        var src = "uniform float u_x; // hint_range(0.0, 10.0) = 5.0\n"
                + "uniform float u_x; // hint_range(0.0, 1.0) = 0.5\n";

        var spec = ShaderParamSpec.Parse(src)["u_x"];
        Assert.Equal(1.0f, spec.Max);
        Assert.Equal(0.5f, spec.Default);
    }

    [Fact]
    public void Clamp_WithinRange_ReturnsValueUnchanged()
    {
        var spec = new ShaderParamSpec("u_x", 0f, 10f, 0.1f, 5f, true);
        Assert.Equal(7f, ShaderParamSpec.Clamp(spec, 7f));
    }

    [Fact]
    public void Clamp_BelowMin_ClampsToMin()
    {
        var spec = new ShaderParamSpec("u_x", 0f, 10f, 0.1f, 5f, true);
        Assert.Equal(0f, ShaderParamSpec.Clamp(spec, -5f));
    }

    [Fact]
    public void Clamp_AboveMax_ClampsToMax()
    {
        var spec = new ShaderParamSpec("u_x", 0f, 10f, 0.1f, 5f, true);
        Assert.Equal(10f, ShaderParamSpec.Clamp(spec, 999f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Clamp_NonFinite_ReturnsDefault(float value)
    {
        var spec = new ShaderParamSpec("u_x", 0f, 10f, 0.1f, 5f, true);
        Assert.Equal(5f, ShaderParamSpec.Clamp(spec, value));
    }

    // Every registered animate/static key's own .frag plus _prelude.frag are
    // embedded resources under this prefix; mirrors ShaderLibrary's own
    // resource naming so the guard below inspects the real shipped files.
    private const string ResourcePrefix = "Nexus.Service.Lighting.Engine.Gpu.Shaders.";

    [Fact]
    public void EveryDeclaredUniformFloat_CarriesAHintRangeAnnotation()
    {
        // System uniforms fed by the engine itself, never a user-tunable
        // param: no slider exists for them, so they carry no range to guard.
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "u_time", "u_resolution",
            "u_audioLevel", "u_audioBass", "u_audioMid", "u_audioHigh", "u_audioBeat",
            "u_spectrum", "u_spectrum64", "u_specHist",
            "u_bassPeak", "u_midPeak", "u_highPeak", "u_levelPeak",
        };
        var declRegex = new Regex(@"^uniform\s+float\s+(u_\w+)");
        var asm = typeof(ShaderLibrary).Assembly;
        var missing = new List<string>();

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".frag", StringComparison.Ordinal)
                || resourceName.EndsWith("_prelude.frag", StringComparison.Ordinal))
            {
                continue;
            }
            var fileName = resourceName[ResourcePrefix.Length..];
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimStart();
                var m = declRegex.Match(trimmed);
                if (!m.Success || trimmed.Contains("hint_range", StringComparison.Ordinal))
                {
                    continue;
                }
                var name = m.Groups[1].Value;
                if (allowlist.Contains(name))
                {
                    continue;
                }
                // lightning.frag declares u_audioBoost without a range on
                // purpose (see the shader param ranges contract).
                if (fileName == "lightning.frag" && name == "u_audioBoost")
                {
                    continue;
                }
                missing.Add($"{fileName}: {name}");
            }
        }

        Assert.True(missing.Count == 0, "declared uniforms missing hint_range: " + string.Join(", ", missing));
    }
}

/// <summary>
/// End-to-end coverage for the bug this contract fixes: a hand-edited profile
/// export carrying an out-of-range preset value must not reach the shader.
/// </summary>
public class ShaderParamSpecImportTests : IDisposable
{
    private readonly string _tempDir;

    public ShaderParamSpecImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-shaderspec-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ImportProfileJson_ClampsOutOfRangeAnimateParams()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new JsonConfigStore(settingsPath);
        var pm = new ProfileManager(store);
        pm.Initialize();
        try
        {
            const string json = """
            {
              "lighting": {
                "animate": {
                  "effect": "plasma",
                  "states": {
                    "plasma": {
                      "speed": 5000,
                      "intensity": 1,
                      "hue": 0,
                      "colorize": 0,
                      "saturation": 1,
                      "contrast": 1,
                      "params": { "u_zoom": 9999 }
                    }
                  }
                }
              }
            }
            """;

            var entry = pm.ImportProfileJson(json);
            var savedJson = File.ReadAllText(Path.Combine(_tempDir, $"profile-{entry.Id}.json"));
            var saved = JsonSerializer.Deserialize(savedJson, PersistenceJsonContext.Default.NexusSettings)!;
            var state = saved.Lighting.Animate.States["plasma"];

            var zoomSpec = ShaderLibrary.Params("plasma")["u_zoom"];
            var speedSpec = ShaderLibrary.Params("plasma")["u_speed"];
            Assert.InRange(state.Params["u_zoom"], zoomSpec.Min, zoomSpec.Max);
            Assert.InRange(state.Speed / 50f, speedSpec.Min, speedSpec.Max);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }
}
