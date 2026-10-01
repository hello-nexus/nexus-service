using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

/// <summary>src/Widgets/AppDataStore.cs: per-profile layout, CAS, size/key-count limits, bundle apply, legacy migration.</summary>
public sealed class AppDataStoreTests : IDisposable
{
    private const string P = "p1";
    private const string Q = "p2";
    private readonly string _root;
    private readonly AppDataStore _store;

    public AppDataStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-appdata-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new AppDataStore(() => _root, () => P);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void Get_on_an_absent_document_returns_revision_zero_and_null_data()
    {
        var (revision, updatedAt, data) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(0, revision);
        Assert.Equal("", updatedAt);
        Assert.Null(data);
    }

    [Fact]
    public void Put_with_baseRevision_zero_creates_the_first_revision()
    {
        var result = _store.Put(P, "com.test.app", "save", 0, Json("""{"a":1}"""));
        Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Revision);

        var (revision, _, data) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(1, revision);
        Assert.Equal("1", data!.Value.GetProperty("a").GetRawText());
    }

    [Fact]
    public void Put_with_a_stale_baseRevision_is_rejected_with_the_current_document()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("""{"a":1}"""));

        var stale = _store.Put(P, "com.test.app", "save", 0, Json("""{"a":2}"""));

        Assert.Equal(AppDataStore.PutOutcome.Conflict, stale.Outcome);
        Assert.Equal(1, stale.Revision);
        Assert.Equal("1", stale.Data!.Value.GetProperty("a").GetRawText());

        // The rejected write never landed.
        var (revision, _, data) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(1, revision);
        Assert.Equal("1", data!.Value.GetProperty("a").GetRawText());
    }

    [Fact]
    public void Put_rebased_on_the_current_revision_succeeds_and_advances_it()
    {
        var first = _store.Put(P, "com.test.app", "save", 0, Json("""{"a":1}"""));
        var second = _store.Put(P, "com.test.app", "save", first.Revision, Json("""{"a":2}"""));

        Assert.Equal(AppDataStore.PutOutcome.Ok, second.Outcome);
        Assert.Equal(2, second.Revision);
    }

    [Fact]
    public void Put_over_the_256KiB_limit_is_rejected_as_TooLarge()
    {
        var huge = "\"" + new string('x', 300 * 1024) + "\"";
        var result = _store.Put(P, "com.test.app", "save", 0, Json(huge));
        Assert.Equal(AppDataStore.PutOutcome.TooLarge, result.Outcome);

        var (revision, _, _) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(0, revision);
    }

    [Fact]
    public void A_17th_key_for_the_same_app_is_rejected_as_TooManyKeys()
    {
        for (var i = 0; i < AppDataStore.MaxKeysPerApp; i++)
        {
            var result = _store.Put(P, "com.test.app", $"k{i}", 0, Json("1"));
            Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
        }

        var overCap = _store.Put(P, "com.test.app", "one-too-many", 0, Json("1"));
        Assert.Equal(AppDataStore.PutOutcome.TooManyKeys, overCap.Outcome);
    }

    [Fact]
    public void Updating_an_existing_key_never_counts_against_the_per_app_key_cap()
    {
        for (var i = 0; i < AppDataStore.MaxKeysPerApp; i++)
        {
            _store.Put(P, "com.test.app", $"k{i}", 0, Json("1"));
        }

        // Rewriting an existing key must not be blocked by the cap it already
        // satisfies.
        var existing = _store.Get(P, "com.test.app", "k0");
        var result = _store.Put(P, "com.test.app", "k0", existing.Revision, Json("2"));
        Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("a")]
    [InlineData("a.b-c_d9")]
    public void Valid_keys_are_accepted(string key) => Assert.True(AppDataKeys.IsValid(key));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Save")]
    [InlineData("-save")]
    [InlineData(".save")]
    [InlineData("save/x")]
    [InlineData("save x")]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("con.bak")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("COM9")]
    [InlineData("lpt1")]
    [InlineData("lpt9.json")]
    public void Invalid_keys_are_rejected(string? key) => Assert.False(AppDataKeys.IsValid(key));

    [Fact]
    public void Invalid_key_over_max_length_is_rejected()
    {
        Assert.False(AppDataKeys.IsValid(new string('a', AppDataKeys.MaxLength + 1)));
        Assert.True(AppDataKeys.IsValid(new string('a', AppDataKeys.MaxLength)));
    }

    [Fact]
    public async Task Concurrent_puts_against_the_same_key_serialise_without_losing_a_write()
    {
        var seed = _store.Put(P, "com.test.app", "counter", 0, Json("0"));

        // Each task reads the current revision, then retries on conflict -
        // proving the lock prevents two writers from both succeeding against
        // the same base revision (which would silently drop one write).
        var tasks = new Task[20];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                while (true)
                {
                    var (revision, _, _) = _store.Get(P, "com.test.app", "counter");
                    var result = _store.Put(P, "com.test.app", "counter", revision, Json((revision + 1).ToString()));
                    if (result.Outcome == AppDataStore.PutOutcome.Ok) return;
                }
            });
        }
        await Task.WhenAll(tasks);

        var expectedFinalRevision = seed.Revision + tasks.Length;
        var (finalRevision, _, data) = _store.Get(P, "com.test.app", "counter");
        Assert.Equal(expectedFinalRevision, finalRevision);
        Assert.Equal(expectedFinalRevision.ToString(), data!.Value.GetRawText());
    }

    [Fact]
    public void Import_writes_a_revision_strictly_above_whatever_is_currently_stored()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        _store.Put(P, "com.test.app", "save", 1, Json("2"));

        var imported = _store.Import(P, "com.test.app", "save", Json("99"));

        Assert.Equal(3, imported.Revision);
        var (revision, _, data) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(3, revision);
        Assert.Equal("99", data!.Value.GetRawText());
    }

    // ── per-profile layout ───────────────────────────────────────────────

    [Fact]
    public void Documents_live_under_the_profile_folder_and_are_isolated_per_profile()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));

        Assert.True(File.Exists(Path.Combine(_root, "profiles", P, "com.test.app", "save.json")));
        Assert.Equal(0, _store.Get(Q, "com.test.app", "save").Revision);

        _store.Put(Q, "com.test.app", "save", 0, Json("2"));
        Assert.Equal("1", _store.Get(P, "com.test.app", "save").Data!.Value.GetRawText());
        Assert.Equal("2", _store.Get(Q, "com.test.app", "save").Data!.Value.GetRawText());
    }

    [Fact]
    public void The_key_cap_is_per_profile()
    {
        for (var i = 0; i < AppDataStore.MaxKeysPerApp; i++)
        {
            _store.Put(P, "com.test.app", $"k{i}", 0, Json("1"));
        }

        Assert.Equal(AppDataStore.PutOutcome.Ok, _store.Put(Q, "com.test.app", "k0", 0, Json("1")).Outcome);
    }

    [Theory]
    [InlineData("p1", "NotValid", "save")]
    [InlineData("p1", "com.test.app", "BadKey")]
    [InlineData("p1", "../escape", "save")]
    [InlineData("p1", "com.test.app", "../escape")]
    [InlineData("../p1", "com.test.app", "save")]
    [InlineData("p/1", "com.test.app", "save")]
    [InlineData("", "com.test.app", "save")]
    public void Every_public_method_rejects_an_invalid_profileId_appId_or_key(string profileId, string appId, string key)
    {
        Assert.Throws<ArgumentException>(() => _store.Get(profileId, appId, key));
        Assert.Throws<ArgumentException>(() => _store.TryRead(profileId, appId, key));
        Assert.Throws<ArgumentException>(() => _store.Put(profileId, appId, key, 0, Json("1")));
        Assert.Throws<ArgumentException>(() => _store.Import(profileId, appId, key, Json("1")));
        Assert.Throws<ArgumentException>(() => _store.Delete(profileId, appId, key));
    }

    [Fact]
    public void Profile_level_methods_reject_an_invalid_profileId()
    {
        Assert.Throws<ArgumentException>(() => _store.DeleteProfile("../x"));
        Assert.Throws<ArgumentException>(() => _store.ReadProfile("../x"));
        Assert.Throws<ArgumentException>(() => _store.AppIdsFor("a/b"));
        Assert.Throws<ArgumentException>(() => _store.ReplaceApps("..", new Dictionary<string, Dictionary<string, JsonElement>>()));
    }

    [Fact]
    public void DeleteProfile_removes_only_that_profiles_documents()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        _store.Put(Q, "com.test.app", "save", 0, Json("1"));

        _store.DeleteProfile(P);

        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", P)));
        Assert.Equal(1, _store.Get(Q, "com.test.app", "save").Revision);
    }

    [Fact]
    public void A_new_profile_has_no_app_data()
    {
        Assert.Empty(_store.ReadProfile("fresh"));
        Assert.Empty(_store.AppIdsFor("fresh"));
    }

    [Fact]
    public void ArchiveProfilesExcept_moves_the_other_profiles_out_of_the_live_tree()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        _store.Put(Q, "com.test.app", "save", 0, Json("1"));

        _store.ArchiveProfilesExcept(new[] { P });

        Assert.True(Directory.Exists(Path.Combine(_root, "profiles", P)));
        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", Q)));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".archive"), "save.json", SearchOption.AllDirectories));
    }

    // ── bundle read/apply ────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, JsonElement>> Bundle(params (string App, string Key, string Json)[] docs)
    {
        var bundle = new Dictionary<string, Dictionary<string, JsonElement>>();
        foreach (var (app, key, json) in docs)
        {
            if (!bundle.TryGetValue(app, out var keys))
            {
                keys = new Dictionary<string, JsonElement>();
                bundle[app] = keys;
            }
            keys[key] = Json(json);
        }
        return bundle;
    }

    [Fact]
    public void ReadProfile_returns_the_bundle_shape_without_revision_metadata()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("""{"a":1}"""));
        _store.Put(P, "com.test.app", "prefs", 0, Json("2"));
        _store.Put(P, "com.other.app", "save", 0, Json("3"));

        var bundle = _store.ReadProfile(P);

        Assert.Equal(new[] { "com.other.app", "com.test.app" }, bundle.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "prefs", "save" }, bundle["com.test.app"].Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(1, bundle["com.test.app"]["save"].GetProperty("a").GetInt32());
        Assert.Equal(new[] { "com.other.app", "com.test.app" }, _store.AppIdsFor(P));
    }

    [Fact]
    public void ReplaceApps_replaces_the_apps_in_the_bundle_deletes_absent_keys_and_leaves_other_apps_alone()
    {
        _store.Put(P, "com.test.app", "old", 0, Json("1"));
        _store.Put(P, "com.test.app", "keep", 0, Json("1"));
        _store.Put(P, "com.untouched.app", "save", 0, Json("1"));

        _store.ReplaceApps(P, Bundle(("com.test.app", "keep", "9"), ("com.test.app", "added", "8")));

        var bundle = _store.ReadProfile(P);
        Assert.Equal(new[] { "added", "keep" }, bundle["com.test.app"].Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("9", bundle["com.test.app"]["keep"].GetRawText());
        Assert.Equal("1", bundle["com.untouched.app"]["save"].GetRawText());
        Assert.Equal(2, _store.Get(P, "com.test.app", "keep").Revision);
    }

    [Fact]
    public void ReplaceApps_with_an_empty_app_entry_clears_that_app()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));

        _store.ReplaceApps(P, new Dictionary<string, Dictionary<string, JsonElement>> { ["com.test.app"] = new() });

        Assert.Empty(_store.AppIdsFor(P));
    }

    [Fact]
    public void ReplaceApps_drops_invalid_ids_oversized_documents_and_keys_past_the_cap()
    {
        var bundle = Bundle(("NotAnId", "save", "1"), ("com.test.app", "BadKey", "1"), ("com.test.app", "fine", "1"));
        bundle["com.test.app"]["huge"] = Json("\"" + new string('x', AppDataStore.MaxDataBytes + 10) + "\"");
        for (var i = 0; i < AppDataStore.MaxKeysPerApp + 4; i++)
        {
            bundle["com.many.app"] = bundle.GetValueOrDefault("com.many.app") ?? new();
            bundle["com.many.app"][$"k{i}"] = Json("1");
        }

        _store.ReplaceApps(P, bundle);

        var result = _store.ReadProfile(P);
        Assert.False(result.ContainsKey("NotAnId"));
        Assert.Equal(new[] { "fine" }, result["com.test.app"].Keys);
        Assert.Equal(AppDataStore.MaxKeysPerApp, result["com.many.app"].Count);
    }

    [Fact]
    public void ReplaceApps_requests_a_reset_only_when_it_wrote_into_the_active_profile()
    {
        var resets = new List<string>();
        _store.ResetRequested += resets.Add;

        _store.ReplaceApps(Q, Bundle(("com.test.app", "save", "1")));
        Assert.Empty(resets);

        _store.ReplaceApps(P, new Dictionary<string, Dictionary<string, JsonElement>>());
        Assert.Empty(resets);

        _store.ReplaceApps(P, Bundle(("com.test.app", "save", "1")));
        Assert.Equal(new[] { P }, resets);
    }

    [Fact]
    public void ReplaceApps_with_a_null_bundle_does_nothing()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));

        Assert.False(_store.ReplaceApps(P, null));

        Assert.Equal(1, _store.Get(P, "com.test.app", "save").Revision);
    }

    // ── legacy layout migration ─────────────────────────────────────────

    private void WriteLegacy(string appId, string key, string json)
    {
        var dir = Path.Combine(_root, appId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + ".json"), json);
    }

    [Fact]
    public void MigrateLegacyLayout_moves_account_wide_app_dirs_into_the_active_profile()
    {
        WriteLegacy("com.test.app", "save", """{"revision":4,"updatedAt":"t","data":{"a":1},"cloud":{"accountId":"x"}}""");

        _store.MigrateLegacyLayout();

        Assert.False(Directory.Exists(Path.Combine(_root, "com.test.app")));
        var (revision, _, data) = _store.Get(P, "com.test.app", "save");
        Assert.Equal(4, revision);
        Assert.Equal("""{"a":1}""", data!.Value.GetRawText());
        Assert.Equal(0, _store.Get(Q, "com.test.app", "save").Revision);
    }

    [Fact]
    public void MigrateLegacyLayout_skips_an_app_the_active_profile_already_has_and_leaves_the_legacy_dir()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        WriteLegacy("com.test.app", "save", """{"revision":9,"updatedAt":"t","data":2}""");

        _store.MigrateLegacyLayout();

        Assert.True(File.Exists(Path.Combine(_root, "com.test.app", "save.json")));
        Assert.Equal("1", _store.Get(P, "com.test.app", "save").Data!.Value.GetRawText());
    }

    [Fact]
    public void MigrateLegacyLayout_ignores_the_archive_the_profiles_folder_and_non_app_dirs_and_is_idempotent()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".archive", "x"));
        Directory.CreateDirectory(Path.Combine(_root, "not-an-app"));
        WriteLegacy("com.test.app", "save", """{"revision":1,"updatedAt":"t","data":1}""");

        _store.MigrateLegacyLayout();
        _store.MigrateLegacyLayout();

        Assert.True(Directory.Exists(Path.Combine(_root, ".archive", "x")));
        Assert.True(Directory.Exists(Path.Combine(_root, "not-an-app")));
        Assert.Equal(new[] { "com.test.app" }, _store.AppIdsFor(P));
    }

    // ── DocumentChanged is the one broadcast hook every write path uses ──

    [Fact]
    public void DocumentChanged_fires_on_a_successful_put_but_not_on_a_rejected_one()
    {
        var fired = new List<(string Profile, string AppId, string Key)>();
        _store.DocumentChanged += (profile, appId, key) => fired.Add((profile, appId, key));

        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        Assert.Equal(new[] { (P, "com.test.app", "save") }, fired);

        _store.Put(P, "com.test.app", "save", 0, Json("2")); // stale base - conflict
        Assert.Single(fired);
    }

    [Fact]
    public void DocumentChanged_fires_on_import_and_delete()
    {
        _store.Put(P, "com.test.app", "save", 0, Json("1"));
        var fired = new List<string>();
        _store.DocumentChanged += (_, _, key) => fired.Add(key);

        _store.Import(P, "com.test.app", "save", Json("2"));
        _store.Put(P, "com.test.app", "other", 0, Json("1"));
        _store.Delete(P, "com.test.app", "other");

        Assert.Equal(new[] { "save", "other", "other" }, fired);
    }

    [Fact]
    public void DocumentChanged_does_not_fire_when_deleting_a_key_that_never_existed()
    {
        var fired = 0;
        _store.DocumentChanged += (_, _, _) => fired++;

        _store.Delete(P, "com.test.app", "never-written");

        Assert.Equal(0, fired);
    }

    // ── rule 9: the per-app key-count cap is checked under a per-app lock ─

    [Fact]
    public async Task Concurrent_puts_to_distinct_new_keys_never_exceed_the_per_app_key_cap()
    {
        var tasks = new Task<AppDataStore.PutResult>[AppDataStore.MaxKeysPerApp + 8];
        for (var i = 0; i < tasks.Length; i++)
        {
            var key = $"k{i}";
            tasks[i] = Task.Run(() => _store.Put(P, "com.test.app", key, 0, Json("1")));
        }
        await Task.WhenAll(tasks);

        var okCount = tasks.Count(t => t.Result.Outcome == AppDataStore.PutOutcome.Ok);
        Assert.Equal(AppDataStore.MaxKeysPerApp, okCount);
        Assert.Equal(AppDataStore.MaxKeysPerApp, _store.ReadProfile(P)["com.test.app"].Count);
    }

    [Fact]
    public void Null_documents_never_reach_disk_or_the_bundle()
    {
        _store.ReplaceApps(P, Bundle(("com.test.app", "n", "null"), ("com.test.app", "ok", "1")));

        Assert.Equal(new[] { "ok" }, _store.ReadProfile(P)["com.test.app"].Keys);
        Assert.False(AppDataStore.IsStorable(Json("null")));
        File.WriteAllText(Path.Combine(_root, "profiles", P, "com.test.app", "legacy-null.json"), """{"revision":1,"updatedAt":"t","data":null}""");
        Assert.DoesNotContain("legacy-null", _store.ReadProfile(P)["com.test.app"].Keys);
    }

    [Fact]
    public void ReplaceApps_ignores_stray_files_with_invalid_key_names()
    {
        _store.Put(P, "com.test.app", "ok", 0, Json("1"));
        File.WriteAllText(Path.Combine(_root, "profiles", P, "com.test.app", "Bad Key.json"), "{}");
        var fired = new List<string>();
        _store.DocumentChanged += (_, _, key) => fired.Add(key);

        _store.ReplaceApps(P, Bundle(("com.test.app", "new", "1")));

        Assert.Equal(new[] { "ok", "new" }, fired);
        Assert.True(File.Exists(Path.Combine(_root, "profiles", P, "com.test.app", "Bad Key.json")));
    }

    [Fact]
    public void ReplaceProfile_removes_apps_that_are_not_in_the_bundle_and_requests_a_reset()
    {
        _store.Put(P, "com.gone.app", "save", 0, Json("1"));
        _store.Put(P, "com.test.app", "old", 0, Json("1"));
        var resets = new List<string>();
        _store.ResetRequested += resets.Add;

        _store.ReplaceProfile(P, Bundle(("com.test.app", "new", "1")));

        var now = _store.ReadProfile(P);
        Assert.Equal(new[] { "com.test.app" }, now.Keys);
        Assert.Equal(new[] { "new" }, now["com.test.app"].Keys);
        Assert.Single(resets);

        _store.ReplaceProfile(P, new Dictionary<string, Dictionary<string, JsonElement>>());
        Assert.Empty(_store.ReadProfile(P));
        Assert.Equal(2, resets.Count);
    }

    [Fact]
    public void ArchiveProfilesExcept_and_DeleteProfile_do_not_throw_on_a_missing_tree()
    {
        _store.ArchiveProfilesExcept(new[] { P });
        _store.DeleteProfile("nothing-here");
    }
}
