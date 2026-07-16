using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lifecycle;
using Xunit;
using static Nexus.Service.Lifecycle.DataLayoutMigration;

namespace Nexus.Service.Tests;

public sealed class DataLayoutMigrationTests : IDisposable
{
    private readonly string _base;

    public DataLayoutMigrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "nexus-migtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    private string P(params string[] parts) =>
        Path.Combine(new[] { _base }.Concat(parts).ToArray());

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void MoveDir_moves_content_and_removes_old()
    {
        var old = P("old");
        var @new = P("devices", "streamdeck");
        WriteFile(Path.Combine(old, "serial", "hash.bin"), "IMG");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) });

        Assert.Equal("IMG", File.ReadAllText(Path.Combine(@new, "serial", "hash.bin")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void MoveDir_never_clobbers_an_existing_target()
    {
        var old = P("old");
        var @new = P("new");
        WriteFile(Path.Combine(old, "a.txt"), "OLD");
        WriteFile(Path.Combine(@new, "a.txt"), "NEW");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) });

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(@new, "a.txt"))); // untouched
        Assert.True(Directory.Exists(old));                                  // not moved
    }

    [Fact]
    public void MoveDir_is_idempotent()
    {
        var old = P("old");
        var @new = P("new");
        WriteFile(Path.Combine(old, "a.txt"), "X");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) });
        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) }); // second run: old gone -> no-op

        Assert.Equal("X", File.ReadAllText(Path.Combine(@new, "a.txt")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void MoveDir_noop_when_old_missing()
    {
        var @new = P("new");
        Execute(new[] { new Entry(P("does-not-exist"), @new, EntryKind.MoveDir) });
        Assert.False(Directory.Exists(@new));
    }

    [Fact]
    public void MoveDir_handles_a_target_nested_inside_the_source()
    {
        // media -> media/effects: new is a descendant of old.
        var old = P("media");
        var @new = P("media", "effects");
        WriteFile(Path.Combine(old, "id", "frames.bin"), "RGB");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) });

        Assert.Equal("RGB", File.ReadAllText(Path.Combine(@new, "id", "frames.bin")));
        Assert.False(File.Exists(Path.Combine(old, "id", "frames.bin"))); // not left at the old depth
        Assert.False(Directory.Exists(old + ".migrate-tmp"));             // temp cleaned up
    }

    [Fact]
    public void MoveDir_resumes_an_interrupted_descendant_move()
    {
        // Crash after old -> tmp but before tmp -> new: data is parked in tmp, old gone.
        var tmp = P("media.migrate-tmp");
        WriteFile(Path.Combine(tmp, "id", "frames.bin"), "RGB");
        var old = P("media");
        var @new = P("media", "effects");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveDir) });

        Assert.Equal("RGB", File.ReadAllText(Path.Combine(@new, "id", "frames.bin")));
        Assert.False(Directory.Exists(tmp));
    }

    [Fact]
    public void MoveFile_moves_and_creates_parent_dirs()
    {
        var old = P("qseries-transports.json");
        var @new = P("devices", "transports", "qseries-transports.json");
        WriteFile(old, "{}");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveFile) });

        Assert.Equal("{}", File.ReadAllText(@new));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void MoveFile_never_clobbers_an_existing_target()
    {
        var old = P("old.json");
        var @new = P("devices", "transports", "new.json");
        WriteFile(old, "OLD");
        WriteFile(@new, "NEW");

        Execute(new[] { new Entry(old, @new, EntryKind.MoveFile) });

        Assert.Equal("NEW", File.ReadAllText(@new));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void DeleteDir_removes_the_stray()
    {
        var old = P("tools");
        WriteFile(Path.Combine(old, "ibp-aw5", "x.exe"), "BIN");

        Execute(new[] { new Entry(old, "", EntryKind.DeleteDir) });

        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Execute_continues_past_a_failing_entry()
    {
        // A file where the failing entry needs a directory: CreateDirectory throws.
        var blocker = P("blocker");
        WriteFile(blocker, "i-am-a-file");
        var src1 = P("src1");
        WriteFile(Path.Combine(src1, "a"), "1");
        var src2 = P("src2");
        WriteFile(Path.Combine(src2, "b"), "2");
        var dst2 = P("dst2");

        Execute(new[]
        {
            new Entry(src1, Path.Combine(blocker, "dst"), EntryKind.MoveDir), // throws, caught
            new Entry(src2, dst2, EntryKind.MoveDir),                          // must still run
        });

        Assert.Equal("2", File.ReadAllText(Path.Combine(dst2, "b"))); // second entry ran
        Assert.True(Directory.Exists(src1));                           // first entry did not complete
    }

    [Fact]
    public void BuildEntries_maps_old_stores_to_the_grouped_layout()
    {
        var entries = BuildEntries();

        string S(params string[] p) => Path.Combine(p); // OS-correct separator

        // device media + records land under devices/
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveDir && e.New.EndsWith(S("devices", "streamdeck")));
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveDir && e.New.EndsWith(S("devices", "lianli-wireless")));
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveDir && e.New.EndsWith(S("devices", "tryx", "media")));
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveFile && e.New.EndsWith(S("devices", "transports", "qseries-transports.json")));
        // lighting content lands under media/
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveDir && e.New.EndsWith(S("media", "effects")));
        Assert.Contains(entries, e => e.Kind == EntryKind.MoveDir && e.New.EndsWith(S("media", "deck-images")));
        // regenerable / re-download: deleted, not moved
        Assert.Contains(entries, e => e.Kind == EntryKind.DeleteDir && e.Old.EndsWith(S("Nexus", "tools")));
        Assert.Contains(entries, e => e.Kind == EntryKind.DeleteDir && e.Old.EndsWith("tryx-thumbs"));
        // The OTA staging dir is renamed in code but the migrator never touches it.
        Assert.DoesNotContain(entries, e => e.Old.Contains("staged-updates"));
        // firmware is never referenced (left in place)
        Assert.DoesNotContain(entries, e => e.Old.Contains("firmware") || e.New.Contains("firmware"));
    }
}
