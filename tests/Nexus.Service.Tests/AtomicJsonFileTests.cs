using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class AtomicJsonFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicJsonFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-atomic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_string_creates_file_with_contents()
    {
        var path = Path.Combine(_tempDir, "a.json");
        AtomicJsonFile.Write(path, "{\"hello\":1}");
        Assert.True(File.Exists(path));
        Assert.Equal("{\"hello\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_string_replaces_existing_file()
    {
        var path = Path.Combine(_tempDir, "b.json");
        File.WriteAllText(path, "old");
        AtomicJsonFile.Write(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Write_bytes_replaces_existing_file()
    {
        var path = Path.Combine(_tempDir, "c.bin");
        File.WriteAllText(path, "old");
        AtomicJsonFile.Write(path, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_does_not_leave_tmp_artifact_on_success()
    {
        var path = Path.Combine(_tempDir, "d.json");
        AtomicJsonFile.Write(path, "{}");
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Concurrent_writes_to_distinct_files_dont_collide()
    {
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var path = Path.Combine(_tempDir, $"concurrent-{i}.json");
            AtomicJsonFile.Write(path, $"value-{i}");
            Assert.Equal($"value-{i}", File.ReadAllText(path));
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Write_consumes_orphan_tmp_left_by_a_crash()
    {
        var path = Path.Combine(_tempDir, "orphan.json");
        File.WriteAllText(path, "old-good");
        File.WriteAllText(path + ".tmp", "garbage-partial"); // simulate crash mid-write

        AtomicJsonFile.Write(path, "new-good");

        Assert.Equal("new-good", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"), "the orphan tmp must be consumed");
    }

    [Fact]
    public void Write_keeps_previous_content_as_bak_and_removes_tmp()
    {
        var path = Path.Combine(_tempDir, "e.json");
        File.WriteAllText(path, "v1");
        AtomicJsonFile.Write(path, "v2");
        Assert.Equal("v2", File.ReadAllText(path));
        Assert.Equal("v1", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Write_overwrites_an_existing_bak()
    {
        var path = Path.Combine(_tempDir, "f.json");
        AtomicJsonFile.Write(path, "v1");
        AtomicJsonFile.Write(path, "v2");
        AtomicJsonFile.Write(path, "v3");
        Assert.Equal("v3", File.ReadAllText(path));
        Assert.Equal("v2", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void Write_first_save_creates_no_bak()
    {
        var path = Path.Combine(_tempDir, "g.json");
        AtomicJsonFile.Write(path, "v1");
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Write_string_is_utf8_without_bom()
    {
        var path = Path.Combine(_tempDir, "h.json");
        const string text = "{\"n\":\"caf\u00e9\"}";
        AtomicJsonFile.Write(path, text);
        Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(text), File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_truncates_a_longer_pre_created_tmp()
    {
        var path = Path.Combine(_tempDir, "i.json");
        File.WriteAllText(AtomicJsonFile.TempPathFor(path), new string('x', 500));
        AtomicJsonFile.Write(path, "short");
        Assert.Equal("short", File.ReadAllText(path));
    }
}
