using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Covers the one-time Initialize() seed that protects pre-device profile
/// files from a data-loss switch: those files were written before the
/// "device" sharing category (Stream Deck + Keeb) existed, so their
/// StreamDeck/Keeb blocks are stale defaults even though the live
/// settings.json may carry real values. Gated by the
/// <see cref="NexusSettings.DeviceCategorySeeded"/> marker rather than
/// content, so it runs exactly once regardless of what any profile file
/// already holds. Each test writes profiles.json / profile-*.json /
/// settings.json directly (bypassing ProfileManager) to reproduce that
/// pre-upgrade on-disk state, then constructs a fresh ProfileManager over it.
/// </summary>
public class ProfileManagerDeviceSeedTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ProfileManagerDeviceSeedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-device-seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteManifest(string activeId, params string[] profileIds)
    {
        var entries = string.Join(",", Array.ConvertAll(profileIds, id =>
            $$"""{ "id": "{{id}}", "name": "{{id}}", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }"""));
        var json = $$"""{ "activeProfileId": "{{activeId}}", "profiles": [{{entries}}] }""";
        File.WriteAllText(Path.Combine(_tempDir, "profiles.json"), json);
    }

    private void WriteProfileFile(string id, string body) =>
        File.WriteAllText(Path.Combine(_tempDir, $"profile-{id}.json"), body);

    private string ReadProfileFile(string id) =>
        File.ReadAllText(Path.Combine(_tempDir, $"profile-{id}.json"));

    [Fact]
    public void Initialize_SeedsRootStreamDeckAndKeebIntoEveryProfileFile_WhenMarkerNotSet()
    {
        WriteManifest("p1", "p1", "p2");
        WriteProfileFile("p1", "{}");
        WriteProfileFile("p2", "{}");
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "streamDeck": { "decks": { "SN-ROOT": { "name": "Root Deck" } } },
          "keeb": { "rotaryLeft": "Volume" }
        }
        """);

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();

            Assert.Contains("\"SN-ROOT\"", ReadProfileFile("p1"));
            Assert.Contains("\"SN-ROOT\"", ReadProfileFile("p2"));
            Assert.Contains("\"Volume\"", ReadProfileFile("p1"));
            Assert.Contains("\"Volume\"", ReadProfileFile("p2"));
            Assert.True(store.Load().DeviceCategorySeeded);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void Initialize_SeedsEvenWhenAProfileAlreadyCarriesADivergentDeck()
    {
        // Marker-gated, not content-guarded: a profile with its own device
        // data still gets overwritten by the one-time seed, since the marker
        // means this is the FIRST boot on a release that introduces Keeb into
        // the device category.
        WriteManifest("p1", "p1", "p2");
        WriteProfileFile("p1", "{}");
        WriteProfileFile("p2", """{ "streamDeck": { "decks": { "SN-OWN": { "name": "P2 Own Deck" } } } }""");
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "streamDeck": { "decks": { "SN-ROOT": { "name": "Root Deck" } } }
        }
        """);

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();

            Assert.Contains("SN-ROOT", ReadProfileFile("p1"));
            var p2Json = ReadProfileFile("p2");
            Assert.Contains("SN-ROOT", p2Json);
            Assert.DoesNotContain("SN-OWN", p2Json);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void Initialize_DoesNotReSeed_WhenMarkerAlreadySet()
    {
        WriteManifest("p1", "p1");
        WriteProfileFile("p1", "{}");
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "deviceCategorySeeded": true,
          "streamDeck": { "decks": { "SN-ROOT": { "name": "Root Deck" } } },
          "keeb": { "rotaryLeft": "Volume" }
        }
        """);

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();

            // The marker was already set, so root's live values must NOT be
            // stamped into the profile file - it stays exactly what it was.
            Assert.DoesNotContain("SN-ROOT", ReadProfileFile("p1"));
            Assert.DoesNotContain("Volume", ReadProfileFile("p1"));
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

}
