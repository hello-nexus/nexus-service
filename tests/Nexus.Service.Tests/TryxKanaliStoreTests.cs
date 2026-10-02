using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class TryxKanaliStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kanali-store-test-{Guid.NewGuid():N}");

    public TryxKanaliStoreTests()
    {
        Directory.CreateDirectory(_dir);
        // Kanali's own entry plus an unrelated key, tab-indented as conf writes it.
        File.WriteAllText(Path.Combine(_dir, "store.json"),
            "{\n\t\"waterBlockScreenEnable\": true,\n\t\"waterBlockScreenCustomMedia\": [\n\t\t{\n" +
            "\t\t\t\"type\": \"MP4\",\n\t\t\t\"path\": \"C:\\\\k\\\\media\\\\a.mp4\",\n" +
            "\t\t\t\"fileName\": [\n\t\t\t\t[\n\t\t\t\t\t\"a.mp4.h264_2240x1080\"\n\t\t\t\t]\n\t\t\t]\n\t\t}\n\t],\n" +
            "\t\"panoramaSettingConfig\": {\n\t\t\"x\": 1.50\n\t}\n}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private JsonElement Root() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "store.json"))).RootElement;

    private string Clip(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[42]);
        return path;
    }

    [Fact]
    public void Register_appends_an_entry_shaped_like_kanalis_own()
    {
        var clip = Clip("2026-10-01_20-48-00-123.mp4");

        Assert.True(TryxKanaliStore.Register(_dir, new TryxKanaliStore.Upload(
            clip, "C:\\k\\media\\thumb\\t.png", 42, "2026-10-01_20-48-00-123.mp4.h264_2240x1080", "BYZL1")));

        var media = Root().GetProperty("waterBlockScreenCustomMedia");
        Assert.Equal(2, media.GetArrayLength());
        var entry = media[1];
        Assert.Equal(
            new[] { "type", "path", "ratio", "name", "thumb", "size", "outPath", "fileName", "sn", "source" },
            entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal("MP4", entry.GetProperty("type").GetString());
        Assert.Equal("2:1", entry.GetProperty("ratio").GetString());
        Assert.Equal("2026-10-01_20-48-00-123.mp4", entry.GetProperty("name").GetString());
        Assert.Equal(42, entry.GetProperty("size").GetInt64());
        Assert.Equal(clip + ".h264_2240x1080", entry.GetProperty("outPath").GetString());
        Assert.Equal("2026-10-01_20-48-00-123.mp4.h264_2240x1080", entry.GetProperty("fileName")[0][0].GetString());
        Assert.Equal("BYZL1", entry.GetProperty("sn").GetString());
        Assert.Equal("LOCAL", entry.GetProperty("source").GetString());
    }

    [Fact]
    public void Register_keeps_every_other_key_and_kanalis_tab_indent()
    {
        TryxKanaliStore.Register(_dir, new TryxKanaliStore.Upload(Clip("b.mp4"), null, 42, "b.mp4.h264_2240x1080", "BYZL1"));

        var text = File.ReadAllText(Path.Combine(_dir, "store.json"));
        Assert.StartsWith("{\n\t\"waterBlockScreenEnable\": true,", text.Replace("\r\n", "\n"));
        Assert.Contains("\"x\": 1.50", text);
        Assert.False(Root().GetProperty("waterBlockScreenCustomMedia")[1].TryGetProperty("thumb", out _));
    }

    [Fact]
    public void Register_replaces_an_entry_for_the_same_panel_file()
    {
        TryxKanaliStore.Register(_dir, new TryxKanaliStore.Upload(Clip("a2.mp4"), null, 42, "a.mp4.h264_2240x1080", "BYZL1"));

        var media = Root().GetProperty("waterBlockScreenCustomMedia");
        Assert.Equal(1, media.GetArrayLength());
        Assert.EndsWith("a2.mp4", media[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Unregister_drops_the_entry_and_deletes_its_clip()
    {
        var clip = Clip("c.mp4");
        TryxKanaliStore.Register(_dir, new TryxKanaliStore.Upload(clip, null, 42, "c.mp4.h264_2240x1080", "BYZL1"));

        TryxKanaliStore.Unregister(_dir, "c.mp4.h264_2240x1080");

        var media = Root().GetProperty("waterBlockScreenCustomMedia");
        Assert.Equal(1, media.GetArrayLength());
        Assert.Equal("a.mp4.h264_2240x1080", media[0].GetProperty("fileName")[0][0].GetString());
        Assert.False(File.Exists(clip));
    }

    [Fact]
    public void Register_fails_without_a_kanali_store()
    {
        File.Delete(Path.Combine(_dir, "store.json"));

        Assert.False(TryxKanaliStore.Register(_dir, new TryxKanaliStore.Upload(Clip("d.mp4"), null, 42, "d.mp4.h264_2240x1080", "BYZL1")));
    }

    [Fact]
    public void Timestamp_matches_kanalis_naming()
    {
        Assert.Equal("2026-07-05_08-46-40-161", TryxKanaliStore.Timestamp(new DateTime(2026, 7, 5, 8, 46, 40, 161)));
    }
}
