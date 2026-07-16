using System;
using System.IO;
using System.Security.Cryptography;
using Nexus.Service.Deck;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public sealed class DeckImageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-deck-image-store-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DeckImageStore _store;

    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    };

    private static readonly byte[] TinyJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

    public DeckImageStoreTests()
    {
        _store = new DeckImageStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Store_ReturnsTheSha256HexOfTheBytes()
    {
        var id = _store.Store(TinyPng);
        var expected = Convert.ToHexString(SHA256.HashData(TinyPng)).ToLowerInvariant();
        Assert.Equal(expected, id);
    }

    [Fact]
    public void Store_WritesAPngExtensionFile()
    {
        var id = _store.Store(TinyPng);
        Assert.True(File.Exists(Path.Combine(_root, id + ".png")));
    }

    [Fact]
    public void Store_WritesAJpgExtensionFile()
    {
        var id = _store.Store(TinyJpeg);
        Assert.True(File.Exists(Path.Combine(_root, id + ".jpg")));
    }

    [Fact]
    public void Store_IsIdempotentForTheSameContent()
    {
        var id1 = _store.Store(TinyPng);
        var writeTime1 = File.GetLastWriteTimeUtc(Path.Combine(_root, id1! + ".png"));
        var id2 = _store.Store(TinyPng);
        Assert.Equal(id1, id2);
        Assert.Equal(writeTime1, File.GetLastWriteTimeUtc(Path.Combine(_root, id2! + ".png")));
    }

    [Fact]
    public void Store_RejectsNonImageBytes()
    {
        Assert.Null(_store.Store(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void Store_RejectsOversizeBytes()
    {
        var oversized = new byte[DeckImageStore.MaxBytes + 1];
        Array.Copy(TinyPng, oversized, TinyPng.Length);
        Assert.Null(_store.Store(oversized));
    }

    [Fact]
    public void Store_RejectsEmptyBytes()
    {
        Assert.Null(_store.Store(Array.Empty<byte>()));
    }

    [Fact]
    public void TryLoad_RoundTripsStoredPngBytesAndContentType()
    {
        var id = _store.Store(TinyPng)!;
        var loaded = _store.TryLoad(id);
        Assert.NotNull(loaded);
        Assert.Equal(TinyPng, loaded!.Value.Bytes);
        Assert.Equal("image/png", loaded.Value.ContentType);
    }

    [Fact]
    public void TryLoad_RoundTripsStoredJpegContentType()
    {
        var id = _store.Store(TinyJpeg)!;
        var loaded = _store.TryLoad(id);
        Assert.NotNull(loaded);
        Assert.Equal("image/jpeg", loaded!.Value.ContentType);
    }

    [Fact]
    public void TryLoad_ReturnsNullForUnknownId()
    {
        var unknownButValidId = new string('0', 64);
        Assert.Null(_store.TryLoad(unknownButValidId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("../../etc/passwd")]
    [InlineData("UPPERCASE0000000000000000000000000000000000000000000000000000")]
    public void IsValidId_RejectsMalformedIds(string? id)
    {
        Assert.False(DeckImageStore.IsValidId(id));
    }

    [Fact]
    public void IsValidId_AcceptsA64CharLowercaseHexString()
    {
        Assert.True(DeckImageStore.IsValidId(new string('a', 64)));
    }

    [Fact]
    public void TryLoad_RejectsPathTraversalId()
    {
        Assert.Null(_store.TryLoad("../../../../etc/passwd"));
    }
}
