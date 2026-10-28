using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>Unit coverage for the lighting write tools against the shared StubLightingProvider.</summary>
public sealed class LightingWriteToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-lighting-write-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_tempDir, "settings.json"));

    // ── apply_lighting_scenario ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyLightingScenario_valid_effect_starts_it_and_persists()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new ApplyLightingScenarioTool(lighting, NewStore(), new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "rainbow" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal("rainbow", call.Effect);
        Assert.True(call.Persist);
    }

    [Fact]
    public async Task ApplyLightingScenario_with_saved_state_starts_with_tuned_values_and_keeps_state()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var store = NewStore();
        store.Update(s => s.Lighting.Animate.States["synthwave"] = new AnimateEffectState
        {
            Speed = 72,
            Hue = 0.88f,
            Colorize = 0.3f,
            Saturation = 1.25f,
            Contrast = 2.53f,
            Params = new() { ["u_lines"] = 36f, ["u_amplitude"] = 0.18f },
        });
        var tool = new ApplyLightingScenarioTool(lighting, store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "synthwave" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal(72, call.Speed);
        Assert.Equal(0.88f, call.Hue, 3);
        Assert.Equal(0.3f, call.Colorize, 3);
        Assert.Equal(1.25f, call.Saturation, 3);
        Assert.Equal(2.53f, call.Contrast, 3);
        Assert.Equal(36f, call.Params.Single(p => p.Name == "u_lines").Value, 3);
        Assert.Equal(0.18f, call.Params.Single(p => p.Name == "u_amplitude").Value, 3);
        var kept = store.Load().Lighting.Animate.States["synthwave"];
        Assert.Equal(0.88f, kept.Hue, 3);
        Assert.Equal(2.53f, kept.Contrast, 3);
        Assert.Equal(36f, kept.Params["u_lines"], 3);
    }

    [Fact]
    public async Task ApplyLightingScenario_without_saved_state_starts_with_template_defaults()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var store = NewStore();
        var tool = new ApplyLightingScenarioTool(lighting, store, new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "fire" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartAnimateCalls);
        var slot = Nexus.Service.Lighting.AnimateTemplateDefaults.Slot("fire", 0)!;
        Assert.Equal(slot.Speed, call.Speed);
        Assert.Equal(slot.Hue, call.Hue, 3);
        Assert.Equal(slot.Params["u_turbulence"], call.Params.Single(p => p.Name == "u_turbulence").Value, 3);
        Assert.False(store.Load().Lighting.Animate.States.ContainsKey("fire"));
    }

    [Fact]
    public async Task ApplyLightingScenario_unknown_scenario_is_error_and_does_not_start_anything()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new ApplyLightingScenarioTool(lighting, NewStore(), new MultiplexHub());

        var args = JsonSerializer.SerializeToElement(new { scenario = "not-a-real-effect" });
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(lighting.StartAnimateCalls);
    }

    // ── set_static_color ────────────────────────────────────────────────────

    [Theory]
    [InlineData("#ff0000")]
    [InlineData("#336699")]
    [InlineData("#111111")]
    [InlineData("#00e639")]
    public async Task SetStaticColor_sets_the_exact_global_color(string color)
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new SetStaticColorTool(lighting, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { color }), CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(lighting.StartStaticCalls);
        Assert.Equal(color, call.Color);
        Assert.True(call.Persist);
        Assert.Contains(color, result.Text ?? "");
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#ff00")]
    [InlineData("#gggggg")]
    [InlineData("ff0000")]
    public async Task SetStaticColor_invalid_hex_is_error(string color)
    {
        var tool = new SetStaticColorTool(new McpTestHarness.StubLightingProvider(), new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { color }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── set_brightness ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetBrightness_valid_percent_persists_the_clamped_fraction()
    {
        var store = NewStore();
        var tool = new SetBrightnessTool(store, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { percent = 60 }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(0.6f, store.Load().Lighting.GlobalBrightness, precision: 3);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(150)]
    public async Task SetBrightness_out_of_range_percent_is_error(double percent)
    {
        var tool = new SetBrightnessTool(NewStore(), new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { percent }), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── stop_lighting ───────────────────────────────────────────────────────

    [Fact]
    public async Task StopLighting_calls_stop_all()
    {
        var lighting = new McpTestHarness.StubLightingProvider();
        var tool = new StopLightingTool(lighting, new MultiplexHub());

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, lighting.StopAllCallCount);
    }
}
