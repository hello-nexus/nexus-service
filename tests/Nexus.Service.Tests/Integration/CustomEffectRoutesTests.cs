using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.CustomEffects;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Integration;

/// <summary>Throwaway store directory and a compile check the test can fail on demand.</summary>
public sealed class CustomEffectAppFactory : NexusAppFactory
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "nexus-custom-fx-itest-" + Guid.NewGuid().ToString("N"));

    /// <summary>The info log the next compile returns; null accepts.</summary>
    public volatile string? CompileError;

    public int CompileCalls;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<CustomEffectStore>();
            services.AddSingleton(new CustomEffectStore(Dir));
            services.RemoveAll<CustomFragCompiler>();
            services.AddSingleton<CustomFragCompiler>(_ =>
            {
                Interlocked.Increment(ref CompileCalls);
                return CompileError;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, recursive: true); } catch { /* already gone */ }
    }
}

public sealed class CustomEffectRoutesTests : IClassFixture<CustomEffectAppFactory>
{
    private readonly CustomEffectAppFactory _factory;

    public CustomEffectRoutesTests(CustomEffectAppFactory factory)
    {
        _factory = factory;
        _factory.CompileError = null;
    }

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Body(string name, string recipe, string frag) =>
        new(JsonSerializer.Serialize(new { name, recipe, frag }), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Read(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Url(string id) => "/lighting/custom-effects/" + id;

    [Fact]
    public async Task Put_stores_the_entry_and_returns_it_with_the_audio_flag()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();

        var res = await http.PutAsync(Url(id), Body("Sunset", "{\"k\":1}", CustomFrags.Frag(audio: true)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Read(res);
        Assert.Equal(id, json.GetProperty("id").GetString());
        Assert.Equal("Sunset", json.GetProperty("name").GetString());
        Assert.Equal("{\"k\":1}", json.GetProperty("recipe").GetString());
        Assert.True(json.GetProperty("updatedAt").GetInt64() > 0);
        Assert.True(json.GetProperty("audio").GetBoolean());
        Assert.False(json.TryGetProperty("frag", out _));
    }

    [Fact]
    public async Task Get_lists_entries_oldest_first_without_the_frag()
    {
        var http = Desktop();
        var a = CustomFrags.NewId();
        var b = CustomFrags.NewId();
        await http.PutAsync(Url(a), Body("A", "", CustomFrags.Frag()));
        await http.PutAsync(Url(b), Body("B", "", CustomFrags.Frag()));

        var list = await Read(await http.GetAsync("/lighting/custom-effects"));

        var ids = list.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.True(ids.IndexOf(a) >= 0 && ids.IndexOf(a) < ids.IndexOf(b));
        var entry = list.EnumerateArray().First(e => e.GetProperty("id").GetString() == a);
        Assert.False(entry.GetProperty("audio").GetBoolean());
        Assert.False(entry.TryGetProperty("frag", out _));
    }

    [Theory]
    [InlineData("BAD")]
    [InlineData("has-dash")]
    public async Task Put_rejects_a_bad_id(string id)
    {
        var res = await Desktop().PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await Read(res);
        Assert.True(body.GetProperty("error").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("msg").GetString()));
    }

    [Fact]
    public async Task Put_rejects_a_bad_id_that_is_too_long()
    {
        var res = await Desktop().PutAsync(Url(new string('a', 33)), Body("n", "", CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_rejects_non_ascii_a_missing_version_line_and_a_blank_name()
    {
        var http = Desktop();
        var id = CustomFrags.NewId();

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag(extra: "// café\n")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync(Url(id), Body("n", "", "void main() {}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync(Url(id), Body("  ", "", CustomFrags.Frag()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/lighting/shaders/custom:" + id)).StatusCode);
    }

    [Fact]
    public async Task Put_rejects_an_unbounded_loop_with_a_clear_message()
    {
        var res = await Desktop().PutAsync(Url(CustomFrags.NewId()),
            Body("n", "", CustomFrags.Frag(extra: "void f() { while (true) { } }\n")));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("while", (await Read(res)).GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Put_rejects_an_id_with_a_trailing_newline()
    {
        var res = await Desktop().PutAsync(Url("abc%0A"), Body("n", "", CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_rejects_oversize_recipe_and_frag()
    {
        var http = Desktop();
        var id = CustomFrags.NewId();

        var bigRecipe = await http.PutAsync(Url(id), Body("n", new string('r', CustomEffectStore.MaxRecipeBytes + 1), CustomFrags.Frag()));
        var bigFrag = await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag(extra: "//" + new string('x', CustomEffectStore.MaxFragBytes) + "\n")));

        Assert.Equal(HttpStatusCode.BadRequest, bigRecipe.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bigFrag.StatusCode);
    }

    [Fact]
    public async Task A_compile_failure_returns_the_info_log_and_stores_nothing()
    {
        var id = CustomFrags.NewId();
        _factory.CompileError = "0:7(3): error: syntax error";

        var res = await Desktop().PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await Read(res);
        Assert.True(body.GetProperty("error").GetBoolean());
        Assert.Equal("0:7(3): error: syntax error", body.GetProperty("msg").GetString());
        Assert.Null(_factory.Services.GetRequiredService<CustomEffectStore>().Get(id));
    }

    [Fact]
    public async Task Put_replaces_the_source_and_invalidates_the_library_caches()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();
        await http.PutAsync(Url(id), Body("v1", "", CustomFrags.Frag(gainDefault: 0.5f)));
        var tag1 = ShaderLibrary.SourceTag("custom:" + id);
        Assert.Equal(0.5f, ShaderLibrary.Params("custom:" + id)["u_b1_gain"].Default);

        await http.PutAsync(Url(id), Body("v2", "", CustomFrags.Frag(gainDefault: 1.5f)));

        Assert.Equal(1.5f, ShaderLibrary.Params("custom:" + id)["u_b1_gain"].Default);
        Assert.NotEqual(tag1, ShaderLibrary.SourceTag("custom:" + id));
        var frag = await Read(await http.GetAsync("/lighting/shaders/custom:" + id));
        Assert.Equal(CustomFrags.Frag(gainDefault: 1.5f), frag.GetProperty("frag").GetString());
    }

    [Fact]
    public async Task The_shaders_route_serves_a_custom_frag_with_a_colon_key_and_404s_when_missing()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();
        await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));

        var ok = await http.GetAsync("/lighting/shaders/custom:" + id);
        var missing = await http.GetAsync("/lighting/shaders/custom:" + CustomFrags.NewId());

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(CustomFrags.Frag(), (await Read(ok)).GetProperty("frag").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_thumbnail_route_renders_a_known_custom_key_and_404s_an_unknown_one()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();
        await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));
        var gpu = _factory.Services.GetRequiredService<GpuContext>();
        if (!gpu.Available)
        {
            gpu.EnsureInitializedLocked();
            gpu.WaitForInit(TimeSpan.FromSeconds(20));
        }

        var custom = await http.GetAsync($"/lighting/effects/custom:{id}/thumbnail.bmp?slot=0");
        var unknown = await http.GetAsync($"/lighting/effects/custom:{CustomFrags.NewId()}/thumbnail.bmp?slot=0");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        // Same contract as a built-in key: an image with a GPU, 404 without one.
        Assert.Equal(gpu.Available ? HttpStatusCode.OK : HttpStatusCode.NotFound, custom.StatusCode);
        if (gpu.Available)
        {
            Assert.Equal("image/bmp", custom.Content.Headers.ContentType?.MediaType);
            var etag1 = custom.Headers.ETag;
            await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag(gainDefault: 1.0f)));
            var after = await http.GetAsync($"/lighting/effects/custom:{id}/thumbnail.bmp?slot=0");
            Assert.NotEqual(etag1, after.Headers.ETag);
        }
    }

    [Fact]
    public void The_real_compile_check_accepts_valid_glsl_and_returns_the_info_log_for_broken_glsl()
    {
        var gpu = _factory.Services.GetRequiredService<GpuContext>();
        if (!gpu.Available)
        {
            gpu.EnsureInitializedLocked();
            gpu.WaitForInit(TimeSpan.FromSeconds(20));
        }
        if (!gpu.Available)
        {
            // No GL on this box: ValidateFragment accepts without checking, which is the contract.
            Assert.Null(ShaderEffect.ValidateFragment(gpu, "#version 330 core\nnot glsl"));
            return;
        }

        Assert.Null(ShaderEffect.ValidateFragment(gpu, CustomFrags.Frag()));
        var log = ShaderEffect.ValidateFragment(gpu, "#version 330 core\nvoid main() { undefined_symbol(); }\n");
        Assert.False(string.IsNullOrWhiteSpace(log));
    }

    [Fact]
    public async Task Put_rebuilds_the_running_custom_effect_live()
    {
        var id = CustomFrags.NewId();
        var key = "custom:" + id;
        var http = Desktop();
        await http.PutAsync(Url(id), Body("v1", "", CustomFrags.Frag()));
        var provider = _factory.Services.GetRequiredService<ILightingProvider>();
        provider.StartAnimate(new AnimateHeadlessStart { Effect = key, Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        var engine = _factory.Services.GetRequiredService<LightingEngine>();
        var before = engine.CurrentEffect;

        await http.PutAsync(Url(id), Body("v2", "", CustomFrags.Frag(gainDefault: 1.0f)));

        Assert.NotSame(before, engine.CurrentEffect);
        Assert.Equal(key, engine.CurrentEffectName);
    }

    [Fact]
    public async Task Delete_removes_the_entry_404s_when_missing_and_moves_a_running_effect_to_rainbow()
    {
        var id = CustomFrags.NewId();
        var key = "custom:" + id;
        var http = Desktop();
        await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));
        var provider = _factory.Services.GetRequiredService<ILightingProvider>();
        provider.StartAnimate(new AnimateHeadlessStart { Effect = key, Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });

        var del = await http.DeleteAsync(Url(id));
        var again = await http.DeleteAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/lighting/shaders/" + key)).StatusCode);
        Assert.Equal("rainbow", _factory.Services.GetRequiredService<LightingEngine>().CurrentEffectName);
    }

    [Fact]
    public async Task Delete_prunes_the_effects_recents_and_keeps_the_others()
    {
        var id = CustomFrags.NewId();
        var key = "custom:" + id;
        var http = Desktop();
        await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));
        var config = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        config.Update(s => s.Ui.RecentAnimations = new List<string> { key + "@2", "fire@1", key });

        var del = await http.DeleteAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(new[] { "fire@1" }, config.Load().Ui.RecentAnimations);
    }

    [Fact]
    public async Task Headless_start_runs_a_custom_key_and_an_unknown_id_still_starts()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();
        await http.PutAsync(Url(id), Body("n", "", CustomFrags.Frag()));
        var engine = _factory.Services.GetRequiredService<LightingEngine>();

        var start = new StringContent(
            JsonSerializer.Serialize(new { effect = "custom:" + id, speed = 50, intensity = 1, saturation = 1, contrast = 1 }),
            Encoding.UTF8, "application/json");
        var res = await http.PostAsync("/lighting/animate/headless-start", start);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("custom:" + id, engine.CurrentEffectName);

        var unknown = new StringContent(
            JsonSerializer.Serialize(new { effect = "custom:" + CustomFrags.NewId(), speed = 50, intensity = 1, saturation = 1, contrast = 1 }),
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/lighting/animate/headless-start", unknown)).StatusCode);
    }

    [Fact]
    public async Task The_routes_require_auth()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/lighting/custom-effects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync(Url("abc"), Body("n", "", CustomFrags.Frag()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync(Url("abc"))).StatusCode);
    }

    private static StringContent LinkBody(string name, object? marketId, object? marketAuthor) =>
        new(JsonSerializer.Serialize(new { name, recipe = "", frag = CustomFrags.Frag(), marketId, marketAuthor }), Encoding.UTF8, "application/json");

    private const string MarketGuid = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    [Fact]
    public async Task Put_stores_the_market_link_and_get_returns_it_camel_cased()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();

        var put = await Read(await http.PutAsync(Url(id), LinkBody("Linked", MarketGuid, "Maker_1")));
        var list = await Read(await http.GetAsync("/lighting/custom-effects"));

        Assert.Equal(MarketGuid, put.GetProperty("marketId").GetString());
        Assert.Equal("Maker_1", put.GetProperty("marketAuthor").GetString());
        var entry = list.EnumerateArray().First(e => e.GetProperty("id").GetString() == id);
        Assert.Equal(MarketGuid, entry.GetProperty("marketId").GetString());
        Assert.Equal("Maker_1", entry.GetProperty("marketAuthor").GetString());
    }

    [Fact]
    public async Task An_unlinked_entry_omits_the_market_fields()
    {
        var id = CustomFrags.NewId();

        var put = await Read(await Desktop().PutAsync(Url(id), Body("Plain", "", CustomFrags.Frag())));

        Assert.False(put.TryGetProperty("marketId", out _));
        Assert.False(put.TryGetProperty("marketAuthor", out _));
    }

    [Fact]
    public async Task A_put_that_omits_the_market_fields_keeps_the_stored_link()
    {
        var id = CustomFrags.NewId();
        var http = Desktop();
        await http.PutAsync(Url(id), LinkBody("Linked", MarketGuid, "Maker_1"));

        var resave = await Read(await http.PutAsync(Url(id), Body("Renamed", "", CustomFrags.Frag())));

        Assert.Equal("Renamed", resave.GetProperty("name").GetString());
        Assert.Equal(MarketGuid, resave.GetProperty("marketId").GetString());
        Assert.Equal("Maker_1", resave.GetProperty("marketAuthor").GetString());
    }

    [Theory]
    [InlineData("not-a-guid", "Maker_1")]
    [InlineData("3f2504e04f8941d39a0c0305e82c3301", "Maker_1")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", "abc")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", "has-dash")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", "waytoolongusername1")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", "caf\u00e9name")]
    public async Task Put_rejects_a_malformed_market_link(string marketId, string marketAuthor)
    {
        var id = CustomFrags.NewId();

        var res = await Desktop().PutAsync(Url(id), LinkBody("n", marketId, marketAuthor));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await Read(res)).GetProperty("error").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Desktop().GetAsync("/lighting/shaders/custom:" + id)).StatusCode);
    }

    private static StringContent DownloadedBody(bool? downloaded) =>
        new(JsonSerializer.Serialize(new { name = "Dl", recipe = "", frag = CustomFrags.Frag(), downloaded }), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Downloaded_flag_sets_persists_across_a_put_without_it_and_clears_on_false()
    {
        var http = Desktop();
        var id = CustomFrags.NewId();

        var fresh = await Read(await http.PutAsync(Url(id), Body("Plain", "", CustomFrags.Frag())));
        Assert.False(fresh.TryGetProperty("downloaded", out var unset) && unset.ValueKind != JsonValueKind.Null);

        var set = await Read(await http.PutAsync(Url(id), DownloadedBody(true)));
        Assert.True(set.GetProperty("downloaded").GetBoolean());

        await http.PutAsync(Url(id), Body("Resaved", "", CustomFrags.Frag()));
        var list = await Read(await http.GetAsync("/lighting/custom-effects"));
        var kept = list.EnumerateArray().First(e => e.GetProperty("id").GetString() == id);
        Assert.True(kept.GetProperty("downloaded").GetBoolean());

        var cleared = await Read(await http.PutAsync(Url(id), DownloadedBody(false)));
        Assert.False(cleared.GetProperty("downloaded").GetBoolean());
    }

    [Fact]
    public async Task An_empty_market_id_clears_the_link_and_a_later_put_without_it_keeps_it_cleared()
    {
        var http = Desktop();
        var id = CustomFrags.NewId();
        await http.PutAsync(Url(id), new StringContent(JsonSerializer.Serialize(
            new { name = "L", recipe = "", frag = CustomFrags.Frag(), marketId = MarketGuid, marketAuthor = "Maker_1", downloaded = true }), Encoding.UTF8, "application/json"));

        var cleared = await Read(await http.PutAsync(Url(id), new StringContent(JsonSerializer.Serialize(
            new { name = "L", recipe = "", frag = CustomFrags.Frag(), marketId = "", marketAuthor = "ignored!", downloaded = true }), Encoding.UTF8, "application/json")));
        Assert.False(cleared.TryGetProperty("marketId", out var a) && a.ValueKind != JsonValueKind.Null);
        Assert.False(cleared.TryGetProperty("marketAuthor", out var b) && b.ValueKind != JsonValueKind.Null);
        Assert.False(cleared.TryGetProperty("downloaded", out var c) && c.ValueKind != JsonValueKind.Null);

        await http.PutAsync(Url(id), Body("L2", "", CustomFrags.Frag()));
        var list = await Read(await http.GetAsync("/lighting/custom-effects"));
        var entry = list.EnumerateArray().First(e => e.GetProperty("id").GetString() == id);
        Assert.False(entry.TryGetProperty("marketId", out var d) && d.ValueKind != JsonValueKind.Null);
        Assert.False(entry.TryGetProperty("downloaded", out var e2) && e2.ValueKind != JsonValueKind.Null);
    }
}
