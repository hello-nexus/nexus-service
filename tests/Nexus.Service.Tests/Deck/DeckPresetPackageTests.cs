using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// .nexus-deck package reader/writer (src/Deck/DeckPresetPackage.cs): the
/// round trip through DeckImageStore, and every rejection DeckRoutes'
/// POST /deck/presets/import relies on before it even reaches policy checks.
/// </summary>
public sealed class DeckPresetPackageTests : IDisposable
{
    private static readonly byte[] OnePxPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "nexus-deckpkg-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DeckImageStore _images;

    public DeckPresetPackageTests() => _images = new DeckImageStore(_imagesDir);

    public void Dispose()
    {
        try { Directory.Delete(_imagesDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteThenRead_RoundTripsManifestAndImageAsset()
    {
        var id = _images.Store(OnePxPng)!;
        var preset = new DeckPreset
        {
            Id = "p-abc123",
            Name = "Test Preset",
            Cols = 2,
            Rows = 2,
            Author = "Nexus",
            Version = "1.0.0",
            Description = "A test preset.",
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id } } } } },
            },
        };

        var zip = DeckPresetPackage.Write(preset, _images);
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));

        Assert.True(result.Ok);
        Assert.Equal(1, result.Manifest!.Format);
        Assert.Equal("p-abc123", result.Manifest.Id);
        Assert.Equal("Test Preset", result.Manifest.Name);
        Assert.Equal("Nexus", result.Manifest.Author);
        Assert.Equal("1.0.0", result.Manifest.Version);
        Assert.Equal("A test preset.", result.Manifest.Description);
        Assert.Single(result.Assets);
        var asset = result.Assets[id];
        Assert.Equal(".png", asset.Ext);
        Assert.Equal(id, Convert.ToHexString(SHA256.HashData(asset.Bytes)).ToLowerInvariant());
    }

    [Fact]
    public void Write_UsesTemplateIdAsManifestIdWhenSet()
    {
        var preset = new DeckPreset { Id = "p-xyz", Name = "Discord Copy", TemplateId = "discord", Cols = 2, Rows = 2 };
        var zip = DeckPresetPackage.Write(preset, _images);
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));
        Assert.True(result.Ok);
        Assert.Equal("discord", result.Manifest!.Id);
    }

    [Fact]
    public void Read_MissingPresetJson_Fails()
    {
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(BuildZip())));
        Assert.False(result.Ok);
        Assert.Contains("preset.json", result.Error);
    }

    [Fact]
    public void Read_UnsupportedFormat_Fails()
    {
        var manifest = Encoding.UTF8.GetBytes("""{"format":2,"id":"x","name":"X","cols":2,"rows":2,"deck":{"pages":[]}}""");
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(BuildZip(("preset.json", manifest)))));
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(9, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 9)]
    public void Read_ColsOrRowsOutOfRange_Fails(int cols, int rows)
    {
        var manifest = Encoding.UTF8.GetBytes(ManifestWithNoIcon(cols, rows));
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(BuildZip(("preset.json", manifest)))));
        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_MissingReferencedAsset_Fails()
    {
        var id = new string('a', 64);
        var manifest = Encoding.UTF8.GetBytes(ManifestWithIcon(id));
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(BuildZip(("preset.json", manifest)))));
        Assert.False(result.Ok);
        Assert.Contains(id, result.Error);
    }

    [Fact]
    public void Read_AssetHashMismatch_Fails()
    {
        var id = Convert.ToHexString(SHA256.HashData(OnePxPng)).ToLowerInvariant();
        var manifest = Encoding.UTF8.GetBytes(ManifestWithIcon(id));
        var wrongBytes = new byte[] { 1, 2, 3, 4 };
        var zip = BuildZip(("preset.json", manifest), ($"assets/{id}.png", wrongBytes));
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));
        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_OversizeAsset_Fails()
    {
        var big = new byte[DeckPresetPackage.MaxAssetBytes + 1];
        var id = Convert.ToHexString(SHA256.HashData(big)).ToLowerInvariant();
        var manifest = Encoding.UTF8.GetBytes(ManifestWithIcon(id));
        var zip = BuildZip(("preset.json", manifest), ($"assets/{id}.png", big));
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));
        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_OversizePackage_Fails()
    {
        var manifest = Encoding.UTF8.GetBytes("""{"format":1,"id":"x","name":"X","cols":2,"rows":2,"deck":{"pages":[]}}""");
        var filler = new byte[DeckPresetPackage.MaxPackageBytes];
        var zip = BuildZip(("preset.json", manifest), ("assets/filler.png", filler));
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));
        Assert.False(result.Ok);
        Assert.Contains("20 MB", result.Error);
    }

    /// <summary>A source that never returns 0, simulating a decompression stream whose actual output runs far past what its declared length promised (a zip bomb's shape from the reader's side).</summary>
    private sealed class InfiniteStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)1, offset, count);
            return count;
        }
    }

    [Fact]
    public void CopyBounded_CopiesExactlyWhenSourceMatchesTheLimit()
    {
        var bytes = new byte[500];
        new Random(1).NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();

        ZipPackageSource.CopyBounded(source, destination, limit: 500);

        Assert.Equal(bytes, destination.ToArray());
    }

    [Fact]
    public void CopyBounded_ThrowsInsteadOfDrainingASourceThatExceedsTheLimit()
    {
        var source = new InfiniteStream();
        using var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => ZipPackageSource.CopyBounded(source, destination, limit: 1000));

        // A source that would otherwise keep yielding gigabytes is stopped
        // within one buffer chunk past the limit, never drained to EOF.
        Assert.True(destination.Length <= 1000 + 81920);
    }

    private static string ManifestWithIcon(string iconId) =>
        "{\"format\":1,\"id\":\"x\",\"name\":\"X\",\"cols\":2,\"rows\":2,\"deck\":{\"pages\":[{\"slots\":[{\"icon\":{\"kind\":\"image\",\"value\":\""
        + iconId + "\"}}]}]}}";

    private static string ManifestWithNoIcon(int cols, int rows) =>
        "{\"format\":1,\"id\":\"x\",\"name\":\"X\",\"cols\":" + cols + ",\"rows\":" + rows + ",\"deck\":{\"pages\":[]}}";

    private static byte[] BuildZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var entryStream = zip.CreateEntry(name).Open();
                entryStream.Write(bytes);
            }
        }
        return ms.ToArray();
    }
}
