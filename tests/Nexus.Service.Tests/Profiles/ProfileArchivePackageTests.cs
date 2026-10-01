using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Profiles;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Profiles;

/// <summary>src/Profiles/ProfileArchivePackage.cs: the legacy .nexusprofile zip reader, and every entry it silently drops rather than failing the whole import.</summary>
public sealed class ProfileArchivePackageTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string SampleProfileJson() => JsonSerializer.Serialize(
        new Nexus.Service.Models.Profiles.ProfileExport { Name = "Default", Settings = new NexusSettings() },
        PersistenceJsonContext.Default.ProfileExport);

    /// <summary>Builds a legacy .nexusprofile zip: profile.json plus one app-data entry per document, in the shape older builds wrote.</summary>
    internal static byte[] BuildLegacyZip(string profileJson, params (string AppId, string Key, string DocJson)[] docs)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                s.Write(System.Text.Encoding.UTF8.GetBytes(profileJson));
            }
            foreach (var (appId, key, docJson) in docs)
            {
                using var s = zip.CreateEntry($"app-data/{appId}/{key}.json").Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(docJson));
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Read_returns_profile_json_and_app_data_entries_unchanged()
    {
        var profileJson = SampleProfileJson();
        var bytes = BuildLegacyZip(profileJson, ("com.hellonexus.aquarium", "save", """{"revision":3,"updatedAt":"t","data":{"fish":7},"cloud":{"revision":1}}"""));

        using var ms = new MemoryStream(bytes);
        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Equal(profileJson, result.ProfileJson);
        var entry = Assert.Single(result.AppData);
        Assert.Equal("com.hellonexus.aquarium", entry.AppId);
        Assert.Equal("save", entry.Key);
        Assert.Equal(7, entry.Data.GetProperty("fish").GetInt32());
    }

    [Fact]
    public void Read_fails_when_profile_json_is_missing()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("app-data/com.test.app/save.json").Open();
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_ignores_app_data_entries_with_an_invalid_app_id_or_key()
    {
        var profileJson = SampleProfileJson();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(profileJson);
                s.Write(bytes);
            }
            // Invalid app id (uppercase, no dot).
            using (var s = zip.CreateEntry("app-data/NOTVALID/save.json").Open())
            {
                JsonSerializer.Serialize(s, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = Json("1") },
                    AppJsonContext.Default.AppDataDocumentDto);
            }
            // Invalid key (uppercase).
            using (var s = zip.CreateEntry("app-data/com.test.app/BadKey.json").Open())
            {
                JsonSerializer.Serialize(s, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = Json("1") },
                    AppJsonContext.Default.AppDataDocumentDto);
            }
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Empty(result.AppData);
    }

    [Fact]
    public void Read_fails_on_bytes_that_are_not_a_zip_archive_at_all()
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a zip file"));

        var result = ProfileArchivePackage.Read(ms);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_drops_app_data_entries_beyond_the_per_app_key_cap()
    {
        var profileJson = SampleProfileJson();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                s.Write(System.Text.Encoding.UTF8.GetBytes(profileJson));
            }
            for (var i = 0; i < Nexus.Service.Widgets.AppDataStore.MaxKeysPerApp + 5; i++)
            {
                using var s = zip.CreateEntry($"app-data/com.test.app/k{i}.json").Open();
                JsonSerializer.Serialize(s, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = Json("1") },
                    AppJsonContext.Default.AppDataDocumentDto);
            }
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Equal(Nexus.Service.Widgets.AppDataStore.MaxKeysPerApp, result.AppData.Count);
    }

    [Fact]
    public void Read_fails_when_the_archive_has_more_entries_than_the_cap()
    {
        var profileJson = SampleProfileJson();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                s.Write(System.Text.Encoding.UTF8.GetBytes(profileJson));
            }
            for (var i = 0; i < ProfileArchivePackage.MaxEntries + 1; i++)
            {
                using var s = zip.CreateEntry($"app-data/com.test.app/pad{i}.json").Open();
            }
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_drops_an_app_data_entry_whose_actual_content_exceeds_the_data_size_limit()
    {
        var profileJson = SampleProfileJson();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                s.Write(System.Text.Encoding.UTF8.GetBytes(profileJson));
            }
            var huge = Json("\"" + new string('x', Nexus.Service.Widgets.AppDataStore.MaxDataBytes + 1024) + "\"");
            using var s2 = zip.CreateEntry("app-data/com.test.app/save.json").Open();
            JsonSerializer.Serialize(s2, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = huge },
                AppJsonContext.Default.AppDataDocumentDto);
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Empty(result.AppData);
    }
}
