using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// AppLocalIdMap's own per-day global-id -&gt; local-id remap: sequential
/// local id assignment, dedup of an already-seen global id, the
/// LoadForWrite/ReadOnly split (only the writer truncates a torn tail), and
/// Flush appending only what GetOrAdd assigned since load.
/// </summary>
public class AppLocalIdMapTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppLocalIdMapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-applocalidmap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "0.ids");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void GetOrAdd_NewGlobalIds_AssignsSequentialLocalIds()
    {
        var map = AppLocalIdMap.LoadForWrite(_path);

        Assert.Equal(0, map.GetOrAdd(42));
        Assert.Equal(1, map.GetOrAdd(7));
        Assert.Equal(0, map.GetOrAdd(42)); // already assigned - same local id
    }

    [Fact]
    public void TryGetLocalId_UnassignedGlobalId_ReturnsNull()
    {
        var map = AppLocalIdMap.LoadForWrite(_path);
        map.GetOrAdd(42);

        Assert.Null(map.TryGetLocalId(99));
        Assert.Equal(0, map.TryGetLocalId(42));
    }

    [Fact]
    public void Flush_ThenReadOnly_RoundTripsGlobalIdsInLocalIdOrder()
    {
        var map = AppLocalIdMap.LoadForWrite(_path);
        map.GetOrAdd(42);
        map.GetOrAdd(7);
        map.Flush();

        var reloaded = AppLocalIdMap.ReadOnly(_path);

        Assert.Equal(new[] { 42, 7 }, reloaded);
    }

    [Fact]
    public void Flush_Twice_AppendsOnlyTheIdsAssignedSinceThePreviousFlush()
    {
        var map = AppLocalIdMap.LoadForWrite(_path);
        map.GetOrAdd(42);
        map.Flush();
        map.GetOrAdd(7);
        map.Flush();
        map.Flush();

        Assert.Equal(new[] { 42, 7 }, AppLocalIdMap.ReadOnly(_path));
    }

    [Fact]
    public void Flush_WithNothingNewSinceLoad_DoesNotCreateAFile()
    {
        var map = AppLocalIdMap.LoadForWrite(_path);
        map.Flush();

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void LoadForWrite_ThenGetOrAdd_ContinuesFromThePersistedCount()
    {
        var first = AppLocalIdMap.LoadForWrite(_path);
        first.GetOrAdd(42);
        first.Flush();

        var second = AppLocalIdMap.LoadForWrite(_path);
        Assert.Equal(0, second.TryGetLocalId(42));
        Assert.Equal(1, second.GetOrAdd(7));
    }

    [Fact]
    public void LoadForWrite_WithATornTrailingRecord_TruncatesItAndKeepsEarlierEntries()
    {
        var first = AppLocalIdMap.LoadForWrite(_path);
        first.GetOrAdd(42);
        first.Flush();

        // Simulate a crash mid-append: a partial 4-byte record.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[] { 1, 2 }); // 2 of 4 bytes for the next record
        }

        var reopened = AppLocalIdMap.LoadForWrite(_path);

        Assert.Equal(0, reopened.TryGetLocalId(42));
        // The torn tail was truncated, so the next assignment lands at local
        // id 1, not colliding with (or being corrupted by) the debris.
        Assert.Equal(1, reopened.GetOrAdd(7));
        reopened.Flush();

        Assert.Equal(8, new FileInfo(_path).Length); // 2 clean 4-byte records, torn tail gone
    }

    [Fact]
    public void ReadOnly_WithATornTrailingRecord_IgnoresIt_WithoutTouchingTheFile()
    {
        var first = AppLocalIdMap.LoadForWrite(_path);
        first.GetOrAdd(42);
        first.Flush();

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[] { 1, 2 });
        }
        var lengthBeforeRead = new FileInfo(_path).Length;

        var ids = AppLocalIdMap.ReadOnly(_path);

        Assert.Equal(new[] { 42 }, ids);
        // Unlike LoadForWrite, a read-only load never mutates the file - a
        // concurrent writer might still be appending to it.
        Assert.Equal(lengthBeforeRead, new FileInfo(_path).Length);
    }

    [Fact]
    public void ReadOnly_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(AppLocalIdMap.ReadOnly(Path.Combine(_dir, "missing.ids")));
    }
}
