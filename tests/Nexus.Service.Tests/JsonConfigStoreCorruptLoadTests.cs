using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Exercises the REAL <see cref="JsonConfigStore"/> (not a hand-written fake):
/// a corrupt settings.json must load defaults without throwing AND preserve the
/// unreadable bytes in a <c>.corrupt</c> backup so user config isn't silently
/// destroyed; and a written setting must survive a reopen.
/// </summary>
public sealed class JsonConfigStoreCorruptLoadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonConfigStoreCorruptLoadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Corrupt_file_loads_defaults_and_is_backed_up()
    {
        File.WriteAllText(_path, "{ this is not valid json ");

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.NotNull(settings); // defaults, no throw
        var backup = _path + ".corrupt";
        Assert.True(File.Exists(backup), "the corrupt file must be preserved");
        Assert.Contains("not valid json", File.ReadAllText(backup));
    }

    [Fact]
    public void Corrupt_file_marks_onboarding_already_complete()
    {
        // The file existed (however unreadable), so this is an upgrade of an
        // existing install, not a fresh one - the welcome screen must not
        // reappear for it.
        File.WriteAllText(_path, "{ this is not valid json ");

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.True(settings.OnboardingCompleted);
    }

    [Fact]
    public void Corrupt_file_keeps_telemetry_off()
    {
        // The file existed, so the corrupt-fallback path routes through
        // Migrate() like any other pre-existing document, never the
        // fresh-install !File.Exists branch that defaults telemetry on.
        File.WriteAllText(_path, "{ this is not valid json ");

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.False(settings.Telemetry.CollectAnonymousData);
    }

    [Fact]
    public void Setting_round_trips_through_a_reopen()
    {
        using (var store = new JsonConfigStore(_path))
        {
            store.Update(s => s.Lighting.GlobalBrightness = 0.5f);
            store.FlushNow();
        }

        using var reopened = new JsonConfigStore(_path);
        Assert.Equal(0.5f, reopened.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public void Unreadable_field_elsewhere_resets_the_file_but_keeps_sign_ins()
    {
        File.WriteAllText(_path, """
            {
              "lighting": { "globalBrightness": "not a number" },
              "auth": {
                "cloudAccounts": [ { "accountId": "acct-1", "username": "nicola", "refreshToken": "refresh-1" } ],
                "activeCloudAccountId": "acct-1"
              }
            }
            """);

        using var store = new JsonConfigStore(_path);
        var auth = store.Load().Auth!;

        var account = Assert.Single(auth.CloudAccounts);
        Assert.Equal("refresh-1", account.RefreshToken);
        Assert.Equal("acct-1", auth.ActiveCloudAccountId);
        Assert.True(File.Exists(_path + ".corrupt"));
        using var reopened = new JsonConfigStore(_path);
        Assert.Equal("refresh-1", Assert.Single(reopened.Load().Auth!.CloudAccounts).RefreshToken);
    }

    [Fact]
    public void Failed_flush_is_retried_by_the_next_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var store = new JsonConfigStore(_path);
        store.Load();
        store.Update(s => s.Lighting.GlobalBrightness = 0.25f);

        var mode = File.GetUnixFileMode(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            store.FlushNow();
        }
        finally
        {
            File.SetUnixFileMode(_dir, mode);
        }
        store.FlushNow();

        using var reopened = new JsonConfigStore(_path);
        Assert.Equal(0.25f, reopened.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public void Zero_filled_settings_recovers_from_bak()
    {
        File.WriteAllText(_path, "{ \"lighting\": { \"globalBrightness\": 0.4 } }");
        File.Move(_path, _path + ".bak");
        File.WriteAllBytes(_path, new byte[256]);

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.Equal(0.4f, settings.Lighting.GlobalBrightness);
        Assert.True(File.Exists(_path + ".corrupt"));
        Assert.Equal(new byte[256], File.ReadAllBytes(_path + ".corrupt"));
        using var reopened = new JsonConfigStore(_path);
        Assert.Equal(0.4f, reopened.Load().Lighting.GlobalBrightness);
        Assert.Contains("0.4", File.ReadAllText(_path + ".bak"));
    }

    [Fact]
    public void Corrupt_settings_and_corrupt_bak_load_defaults_keep_auth_and_rewrite_bak()
    {
        File.WriteAllText(_path, """
            { "lighting": { "globalBrightness": "x" },
              "auth": { "cloudAccounts": [ { "accountId": "a", "username": "n", "refreshToken": "r1" } ], "activeCloudAccountId": "a" } }
            """);
        File.WriteAllBytes(_path + ".bak", new byte[64]);

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.Equal(1f, settings.Lighting.GlobalBrightness);
        Assert.Equal("r1", Assert.Single(settings.Auth!.CloudAccounts).RefreshToken);
        Assert.Contains("globalBrightness", File.ReadAllText(_path + ".corrupt"));
        Assert.Contains("globalBrightness", File.ReadAllText(_path + ".bak"));
    }

    [Fact]
    public void Missing_settings_with_valid_bak_recovers()
    {
        File.WriteAllText(_path + ".bak", "{ \"lighting\": { \"globalBrightness\": 0.4 } }");

        using var store = new JsonConfigStore(_path);

        Assert.Equal(0.4f, store.Load().Lighting.GlobalBrightness);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Missing_settings_without_bak_loads_defaults()
    {
        using var store = new JsonConfigStore(_path);

        Assert.Equal(1f, store.Load().Lighting.GlobalBrightness);
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Fact]
    public void Missing_settings_with_corrupt_bak_loads_defaults()
    {
        File.WriteAllBytes(_path + ".bak", new byte[64]);

        using var store = new JsonConfigStore(_path);

        Assert.Equal(1f, store.Load().Lighting.GlobalBrightness);
    }

    [UnprivilegedUnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Unreadable_primary_for_a_non_parse_reason_does_not_recover_from_bak()
    {
        File.WriteAllText(_path, "{ \"lighting\": { \"globalBrightness\": 0.9 } }");
        File.SetUnixFileMode(_path, UnixFileMode.None);
        const string bakJson = "{ \"lighting\": { \"globalBrightness\": 0.4 } }";
        File.WriteAllText(_path + ".bak", bakJson);

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.Equal(1f, settings.Lighting.GlobalBrightness);
        Assert.DoesNotContain("0.4", File.ReadAllText(_path));
    }

    [Fact]
    public void Old_schema_bak_is_migrated_on_recovery()
    {
        File.WriteAllText(_path, "{ nope");
        File.WriteAllText(_path + ".bak", "{ \"schemaVersion\": 5, \"theme\": { \"themeMode\": \"dark\" } }");

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.Equal(NexusSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("dark", settings.Theme.ThemeMode);
        Assert.Matches("\"schemaVersion\":\\s*" + NexusSettings.CurrentSchemaVersion, File.ReadAllText(_path));
    }

    [Fact]
    public void Bak_keeps_the_replaced_files_owner_only_mode_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using (var store = new JsonConfigStore(_path))
        {
            store.Load();
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            store.Update(s => s.Lighting.GlobalBrightness = 0.6f);
            store.FlushNow();
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path + ".bak"));
    }
}
