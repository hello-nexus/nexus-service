using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;

namespace Nexus.Service.Tests;

public sealed class GalleryLibraryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _rootDir;
    private readonly string _photosDir;

    public GalleryLibraryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-gallery-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _rootDir = Path.Combine(_tempDir, "gallery");
        _photosDir = Path.Combine(_tempDir, "photos");
        Directory.CreateDirectory(_photosDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private GalleryLibrary NewLibrary() => new(_rootDir);

    private string WriteImage(string name)
    {
        var path = Path.Combine(_photosDir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    // ── Item ids ─────────────────────────────────────────────────────────────

    [Fact]
    public void ItemId_IsStable_And_UrlSafe()
    {
        var path = Path.Combine(_photosDir, "a.png");
        var id1 = GalleryLibrary.ItemIdForPath(path);
        var id2 = GalleryLibrary.ItemIdForPath(path);

        Assert.Equal(id1, id2);
        Assert.Equal(16, id1.Length);
        Assert.True(MediaLibrary.IsValidId(id1));
        Assert.NotEqual(id1, GalleryLibrary.ItemIdForPath(Path.Combine(_photosDir, "b.png")));
    }

    // ── AddReference validation ──────────────────────────────────────────────

    [Fact]
    public void AddReference_RejectsRelativePath()
    {
        var result = NewLibrary().AddReference("photos/a.png", GallerySourceKinds.File);
        Assert.True(result.Error);
    }

    [Fact]
    public void AddReference_RejectsMissingFile()
    {
        var result = NewLibrary().AddReference(Path.Combine(_photosDir, "missing.png"), GallerySourceKinds.File);
        Assert.True(result.Error);
    }

    [Fact]
    public void AddReference_RejectsNonImageFile()
    {
        var path = Path.Combine(_photosDir, "notes.txt");
        File.WriteAllText(path, "hi");

        var result = NewLibrary().AddReference(path, GallerySourceKinds.File);
        Assert.True(result.Error);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.M4V")]
    [InlineData("clip.webm")]
    [InlineData("clip.mov")]
    public void AddReference_AcceptsNativeVideoContainers(string name)
    {
        var path = WriteImage(name);

        var result = NewLibrary().AddReference(path, GallerySourceKinds.File);

        Assert.False(result.Error, result.Msg);
    }

    // No transcoder sits between the file and the panel's <video>, so a
    // container Chromium cannot open is refused at add time rather than
    // enumerated as an item that never plays. (Codecs are not inspected.)
    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.avi")]
    [InlineData("clip.wmv")]
    public void AddReference_RejectsVideoContainersPanelsCannotPlay(string name)
    {
        var path = WriteImage(name);

        var result = NewLibrary().AddReference(path, GallerySourceKinds.File);

        Assert.True(result.Error);
    }

    [Fact]
    public void AddReference_RejectsUnknownKind()
    {
        var result = NewLibrary().AddReference(WriteImage("a.png"), "url");
        Assert.True(result.Error);
    }

    [Fact]
    public void AddReference_RejectsDuplicatePath_WithDuplicateCode()
    {
        var lib = NewLibrary();
        var path = WriteImage("a.png");

        var first = lib.AddReference(path, GallerySourceKinds.File);
        Assert.False(first.Error);
        Assert.Equal("", first.Code);

        var second = lib.AddReference(path, GallerySourceKinds.File);
        Assert.True(second.Error);
        // The UI branches on this to say "already in the gallery" instead of
        // a generic add failure.
        Assert.Equal(GalleryErrorCodes.Duplicate, second.Code);
    }

    [Fact]
    public void AddReference_AcceptsFolder()
    {
        var result = NewLibrary().AddReference(_photosDir, GallerySourceKinds.Folder);

        Assert.False(result.Error);
        Assert.Equal(GallerySourceKinds.Folder, result.Source!.Kind);
        Assert.Equal(Path.GetFileName(_photosDir), result.Source.Name);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void Sources_RoundTrip_AcrossInstances()
    {
        var path = WriteImage("a.png");
        NewLibrary().AddReference(path, GallerySourceKinds.File);

        var reloaded = NewLibrary().ListSources();

        Assert.Single(reloaded);
        Assert.Equal(path, reloaded[0].Path);
        Assert.Equal(GallerySourceKinds.File, reloaded[0].Kind);
    }

    [Fact]
    public void CorruptSourcesFile_LoadsEmpty()
    {
        Directory.CreateDirectory(_rootDir);
        File.WriteAllText(Path.Combine(_rootDir, "sources.json"), "{not json");

        Assert.Empty(NewLibrary().ListSources());
    }

    // ── Enumeration ──────────────────────────────────────────────────────────

    [Fact]
    public void EnumerateItems_ScansFoldersRecursively()
    {
        WriteImage("zebra.png");
        WriteImage("apple.jpg");
        File.WriteAllText(Path.Combine(_photosDir, "notes.txt"), "skip me");
        Directory.CreateDirectory(Path.Combine(_photosDir, "nested"));
        File.WriteAllBytes(Path.Combine(_photosDir, "nested", "deep.png"), new byte[] { 1 });

        var lib = NewLibrary();
        lib.AddReference(_photosDir, GallerySourceKinds.Folder);

        var items = lib.EnumerateItems();

        // Recursive (users point at a Pictures root whose images live in
        // subfolders), images only, path-sorted.
        Assert.Equal(new[] { "apple.jpg", "deep.png", "zebra.png" }, items.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void EnumerateItems_TagsVideosByKind_AndSkipsUnplayableContainers()
    {
        WriteImage("photo.jpg");
        WriteImage("clip.mp4");
        WriteImage("clip.mkv");

        var lib = NewLibrary();
        lib.AddReference(_photosDir, GallerySourceKinds.Folder);

        var items = lib.EnumerateItems();

        Assert.Equal(new[] { "clip.mp4", "photo.jpg" }, items.Select(i => i.Name).ToArray());
        Assert.Equal(GalleryItemKinds.Video, items[0].Kind);
        Assert.Equal(GalleryItemKinds.Image, items[1].Kind);
    }

    [Fact]
    public void EnumerateItems_DedupesFileAlsoCoveredByFolder()
    {
        var path = WriteImage("a.png");
        var lib = NewLibrary();
        lib.AddReference(path, GallerySourceKinds.File);
        lib.AddReference(_photosDir, GallerySourceKinds.Folder);

        var items = lib.EnumerateItems();

        Assert.Single(items);
    }

    [Fact]
    public void EnumerateItems_SkipsDeletedFolder()
    {
        var doomed = Path.Combine(_tempDir, "doomed");
        Directory.CreateDirectory(doomed);
        var lib = NewLibrary();
        lib.AddReference(doomed, GallerySourceKinds.Folder);
        Directory.Delete(doomed);

        Assert.Empty(lib.EnumerateItems());
    }

    // ── Path resolution ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveItemPath_ResolvesRegisteredItem()
    {
        var path = WriteImage("a.png");
        var lib = NewLibrary();
        lib.AddReference(path, GallerySourceKinds.File);

        var id = lib.EnumerateItems().Single().Id;

        Assert.Equal(Path.GetFullPath(path), lib.ResolveItemPath(id));
    }

    [Fact]
    public void ResolveItemPath_RefusesForeignId()
    {
        var lib = NewLibrary();
        lib.AddReference(WriteImage("a.png"), GallerySourceKinds.File);

        // Id derived from a path that is NOT part of any source.
        var foreign = GalleryLibrary.ItemIdForPath(Path.Combine(_tempDir, "secret.png"));

        Assert.Null(lib.ResolveItemPath(foreign));
    }

    [Fact]
    public void ResolveItemPath_ResolvesWithoutPriorEnumeration()
    {
        var path = WriteImage("a.png");
        var lib = NewLibrary();
        lib.AddReference(path, GallerySourceKinds.File);
        var id = lib.EnumerateItems().Single().Id;

        // Fresh instance = cold id → path map; must rebuild internally.
        var cold = NewLibrary();

        Assert.Equal(Path.GetFullPath(path), cold.ResolveItemPath(id));
    }

    // ── Auto kind (drag-n-drop sends bare paths) ─────────────────────────────

    [Fact]
    public void AddReference_AutoKind_ResolvesFileAndFolder()
    {
        var lib = NewLibrary();

        var file = lib.AddReference(WriteImage("a.png"), GallerySourceKinds.Auto);
        Assert.Equal(GallerySourceKinds.File, file.Source!.Kind);

        var folder = lib.AddReference(_photosDir, GallerySourceKinds.Auto);
        Assert.Equal(GallerySourceKinds.Folder, folder.Source!.Kind);
    }

    // ── Exclusions ───────────────────────────────────────────────────────────

    [Fact]
    public void ExcludeItem_HidesFolderItem_WithoutTouchingDisk()
    {
        var path = WriteImage("a.png");
        WriteImage("b.png");
        var lib = NewLibrary();
        var source = lib.AddReference(_photosDir, GallerySourceKinds.Folder).Source!;
        var hidden = lib.EnumerateItems().First(i => i.Name == "a.png");

        Assert.True(lib.ExcludeItem(source.Id, hidden.Id));

        Assert.Equal(new[] { "b.png" }, lib.EnumerateItems().Select(i => i.Name).ToArray());
        Assert.True(File.Exists(path));
        // The hidden id no longer resolves for file serving either.
        Assert.Null(lib.ResolveItemPath(hidden.Id));
        // Exclusion is visible on the source for the UI badge.
        Assert.Single(lib.ListSources().Single().Excluded);
    }

    [Fact]
    public void RestoreExclusions_BringsEverythingBack()
    {
        WriteImage("a.png");
        WriteImage("b.png");
        var lib = NewLibrary();
        var source = lib.AddReference(_photosDir, GallerySourceKinds.Folder).Source!;
        lib.ExcludeItem(source.Id, lib.EnumerateItems()[0].Id);

        Assert.True(lib.RestoreExclusions(source.Id));

        Assert.Equal(2, lib.EnumerateItems().Count);
        Assert.Empty(lib.ListSources().Single().Excluded);
    }

    [Fact]
    public void Exclusions_PersistAcrossInstances()
    {
        WriteImage("a.png");
        WriteImage("b.png");
        var lib = NewLibrary();
        var source = lib.AddReference(_photosDir, GallerySourceKinds.Folder).Source!;
        lib.ExcludeItem(source.Id, lib.EnumerateItems()[0].Id);

        var reloaded = NewLibrary();

        Assert.Single(reloaded.EnumerateItems());
        Assert.Single(reloaded.ListSources().Single().Excluded);
    }

    [Fact]
    public void LegacyUploadKind_MigratesToFileReference()
    {
        var path = WriteImage("a.png");
        Directory.CreateDirectory(_rootDir);
        File.WriteAllText(Path.Combine(_rootDir, "sources.json"),
            $$"""{"sources":[{"id":"legacy1","kind":"upload","path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"name":"a.png","addedAtUnixMs":1}]}""");

        var sources = NewLibrary().ListSources();

        Assert.Equal(GallerySourceKinds.File, Assert.Single(sources).Kind);
    }

    [Fact]
    public void RemoveSource_KeepsReferencedFileOnDisk()
    {
        var path = WriteImage("a.png");
        var lib = NewLibrary();
        var source = lib.AddReference(path, GallerySourceKinds.File).Source!;

        Assert.True(lib.RemoveSource(source.Id));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RemoveSource_UnknownId_ReturnsFalse()
    {
        Assert.False(NewLibrary().RemoveSource("nope"));
    }
}
