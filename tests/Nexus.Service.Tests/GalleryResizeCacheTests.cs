using Nexus.Service.Gallery;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

/// <summary>
/// The derivative cache behind <c>GET /gallery/items/{id}/file?w=</c>. The
/// invariants that matter are the safety ones: it never touches the user's
/// file, it never returns a half-written JPEG, and every failure degrades to
/// "serve the original" instead of surfacing an error. Encode tests are gated
/// on ffmpeg being present - the same gate Slv3LcdMediaLibraryTests uses.
/// </summary>
public sealed class GalleryResizeCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _photosDir;
    private readonly string _cacheDir;

    public GalleryResizeCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-gallery-resize-" + Guid.NewGuid().ToString("N")[..8]);
        _photosDir = Path.Combine(_tempDir, "photos");
        _cacheDir = Path.Combine(_tempDir, "resized");
        Directory.CreateDirectory(_photosDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Stands in for GalleryLibrary.ItemIdForPath - the cache only needs it to
    // be the stable per-item handle that names the derivative.
    private const string ItemId = "fcbe367ada7551ef";

    // Wide enough that a downscale is visible in the byte count; Small is
    // below every bucket, for the no-upscale guard.
    private const string WideFixture = "wide.jpg";
    private const string SmallFixture = "small.jpg";

    private GalleryResizeCache NewCache() => new(_cacheDir);

    // ── Width bucketing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 320)]
    [InlineData(320, 320)]
    [InlineData(321, 480)]
    [InlineData(640, 640)]
    [InlineData(961, 1280)]
    [InlineData(1920, 1920)]
    [InlineData(4000, 1920)]   // clamped, never a bespoke width
    public void SnapWidth_rounds_up_to_a_bucket(int requested, int expected)
    {
        Assert.Equal(expected, GalleryResizeCache.SnapWidth(requested));
    }

    // ── Format gate ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.bmp", true)]
    // The four a flattened first-frame JPEG would silently damage: gif and
    // webp animate, png and avif carry alpha.
    [InlineData("a.png", false)]
    [InlineData("a.gif", false)]
    [InlineData("a.webp", false)]
    [InlineData("a.avif", false)]
    public void CanDerive_only_accepts_formats_a_jpeg_can_replace(string name, bool expected)
    {
        Assert.Equal(expected, GalleryResizeCache.CanDerive(name));
    }

    // ── Degrade-to-original paths ────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_returns_null_for_a_format_it_will_not_flatten()
    {
        var path = Path.Combine(_photosDir, "shot.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        Assert.Null(await NewCache().GetAsync(ItemId, path, 640, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_a_missing_file()
    {
        var path = Path.Combine(_photosDir, "gone.jpg");
        Assert.Null(await NewCache().GetAsync(ItemId, path, 640, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_a_file_ffmpeg_cannot_decode()
    {
        if (FfmpegResolver.Path is null) return;
        // A .jpg extension over bytes that are not a JPEG: ffmpeg exits
        // non-zero and the caller must fall back, not 500.
        var path = Path.Combine(_photosDir, "corrupt.jpg");
        await File.WriteAllTextAsync(path, "this is not an image");
        Assert.Null(await NewCache().GetAsync(ItemId, path, 640, CancellationToken.None));
        // And nothing partial is left behind to be served as a cache hit.
        Assert.Empty(Directory.Exists(_cacheDir) ? Directory.GetFiles(_cacheDir) : Array.Empty<string>());
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_writes_a_smaller_jpeg_and_leaves_the_original_alone()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "big.jpg");
        var originalBytes = await File.ReadAllBytesAsync(source);

        var derived = await NewCache().GetAsync(ItemId, source, 480, CancellationToken.None);

        Assert.NotNull(derived);
        Assert.StartsWith(_cacheDir, derived);
        Assert.True(new FileInfo(derived!).Length < originalBytes.Length);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task GetAsync_serves_the_second_request_from_disk()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "cached.jpg");
        var cache = NewCache();

        var first = await cache.GetAsync(ItemId, source, 640, CancellationToken.None);
        Assert.NotNull(first);
        var writtenAt = File.GetLastWriteTimeUtc(first!);

        var second = await cache.GetAsync(ItemId, source, 640, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second!));
        Assert.Single(Directory.GetFiles(_cacheDir, "*.jpg"));
    }

    [Fact]
    public async Task GetAsync_keys_separately_per_bucket()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "multi.jpg");
        var cache = NewCache();

        var small = await cache.GetAsync(ItemId, source, 320, CancellationToken.None);
        var large = await cache.GetAsync(ItemId, source, 1280, CancellationToken.None);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.NotEqual(small, large);
        Assert.True(new FileInfo(small!).Length < new FileInfo(large!).Length);
    }

    [Fact]
    public async Task GetAsync_reencodes_when_the_source_file_changes()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "swapped.jpg");
        var cache = NewCache();
        var before = await cache.GetAsync(ItemId, source, 640, CancellationToken.None);
        Assert.NotNull(before);

        // Same path, different photo - the URL cannot express that (item ids
        // hash the path alone), so size+mtime in the key is what stops a stale
        // derivative being served forever.
        CopyFixture(SmallFixture, "swapped.jpg");
        var after = await cache.GetAsync(ItemId, source, 640, CancellationToken.None);

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task GetAsync_collapses_concurrent_requests_onto_one_encode()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "shared.jpg");
        var cache = NewCache();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(ItemId, source, 640, CancellationToken.None)));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Single(results.Distinct());
        Assert.Single(Directory.GetFiles(_cacheDir, "*.jpg"));
    }

    [Fact]
    public async Task GetAsync_never_upscales_a_source_smaller_than_the_bucket()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(SmallFixture, "tiny.jpg");

        var derived = await NewCache().GetAsync(ItemId, source, 1920, CancellationToken.None);

        Assert.NotNull(derived);
        // A derivative larger than the file it replaces would be a
        // pessimization, not a speed-up.
        Assert.True(new FileInfo(derived!).Length <= new FileInfo(source).Length * 1.1);
    }

    // ── Removal / purge ──────────────────────────────────────────────────────

    [Fact]
    public async Task Derivative_is_named_for_its_item()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "named.jpg");

        var derived = await NewCache().GetAsync(ItemId, source, 640, CancellationToken.None);

        Assert.NotNull(derived);
        // PurgeExcept can only find a removed item's files through this prefix.
        Assert.StartsWith(ItemId + "-", Path.GetFileName(derived));
    }

    [Fact]
    public async Task PurgeExcept_drops_a_removed_items_derivatives_and_keeps_the_rest()
    {
        if (FfmpegResolver.Path is null) return;
        var keep = CopyFixture(WideFixture, "keep.jpg");
        var drop = CopyFixture(WideFixture, "drop.jpg");
        var cache = NewCache();
        const string keepId = "aaaaaaaaaaaaaaaa";
        const string dropId = "bbbbbbbbbbbbbbbb";
        // Two widths of the removed item: hiding one image must not leave a
        // half-cleaned set behind.
        Assert.NotNull(await cache.GetAsync(keepId, keep, 640, CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(dropId, drop, 640, CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(dropId, drop, 320, CancellationToken.None));
        Assert.Equal(3, Directory.GetFiles(_cacheDir, "*.jpg").Length);

        cache.PurgeExcept(new HashSet<string> { keepId });

        var left = Directory.GetFiles(_cacheDir, "*.jpg").Select(Path.GetFileName).ToList();
        Assert.Single(left);
        Assert.StartsWith(keepId + "-", left[0]);
    }

    [Fact]
    public async Task PurgeExcept_on_an_empty_gallery_clears_everything()
    {
        if (FfmpegResolver.Path is null) return;
        var source = CopyFixture(WideFixture, "gone.jpg");
        var cache = NewCache();
        Assert.NotNull(await cache.GetAsync(ItemId, source, 640, CancellationToken.None));

        cache.PurgeExcept(new HashSet<string>());

        Assert.Empty(Directory.GetFiles(_cacheDir, "*.jpg"));
    }

    [Fact]
    public async Task A_source_ffmpeg_rejects_stays_rejected_without_caching_anything()
    {
        if (FfmpegResolver.Path is null) return;
        var path = Path.Combine(_photosDir, "corrupt2.jpg");
        await File.WriteAllTextAsync(path, "this is not an image");
        var cache = NewCache();

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await cache.GetAsync(ItemId, path, 640, CancellationToken.None));
        }
        Assert.Empty(Directory.Exists(_cacheDir) ? Directory.GetFiles(_cacheDir) : Array.Empty<string>());
    }

    /// <summary>
    /// Copies a committed JPEG into this test's photo dir.
    ///
    /// Deliberately NOT synthesized with ffmpeg: FfmpegResolver prefers the
    /// bundled build over Homebrew, and the bundled one is minimal - it has
    /// neither lavfi nor rawvideo, so a generated source silently failed and
    /// took every encode assertion with it. Feeding a real file is also the
    /// honest gate, since it exercises the ffmpeg that actually ships.
    /// </summary>
    private string CopyFixture(string fixture, string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gallery", fixture);
        // Loud rather than skipped: a fixture missing from the output means the
        // csproj Content glob stopped copying it, and silently passing on an
        // absent input is how a suite comes back green having checked nothing.
        Assert.True(File.Exists(source), $"missing test fixture {source}");
        var path = Path.Combine(_photosDir, name);
        File.Copy(source, path, overwrite: true);
        return path;
    }
}
