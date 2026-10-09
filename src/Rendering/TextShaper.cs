using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Nexus.Service.Rendering;

/// <summary>
/// HarfBuzz shaping over one typeface's shaping tables. Skia's own SKShaper copies the
/// whole font file into native memory, and colour-emoji files are mostly glyph bitmaps
/// that shaping never reads (Apple Color Emoji cost hundreds of MB resident), so this
/// rebuilds a font file without them. Immutable once built: shaping is thread-safe.
/// </summary>
internal sealed class TextShaper
{
    /// <summary>HarfBuzz positions come back in units of size / Scale.</summary>
    private const int Scale = 512;

    private static readonly HashSet<uint> BitmapTables = new()
    {
        TagOf("sbix"), TagOf("CBDT"), TagOf("CBLC"), TagOf("EBDT"), TagOf("EBLC"), TagOf("EBSC"),
        TagOf("bdat"), TagOf("bloc"), TagOf("SVG "), TagOf("COLR"), TagOf("CPAL"),
    };

    private readonly HarfBuzzSharp.Font _font;

    public TextShaper(SKTypeface typeface)
    {
        var sfnt = BuildSfnt(typeface);
        var pinned = GCHandle.Alloc(sfnt, GCHandleType.Pinned);
        // The face keeps its own blob reference and the font its own face reference, so the
        // managed wrappers can go; the release delegate unpins once HarfBuzz lets go.
        using var blob = new Blob(pinned.AddrOfPinnedObject(), sfnt.Length, MemoryMode.ReadOnly, () => pinned.Free());
        using var face = new Face(blob, 0) { UnitsPerEm = typeface.UnitsPerEm };
        _font = new HarfBuzzSharp.Font(face);
        _font.SetScale(Scale, Scale);
        _font.SetFunctionsOpenType();
    }

    /// <summary>Glyph ids and pen positions from x = 0 on the baseline, and the run's advance width.</summary>
    public (ushort[] Glyphs, SKPoint[] Points, float Width) Shape(string text, SKFont font)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        _font.Shape(buffer, Array.Empty<Feature>());

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var scaleY = font.Size / Scale;
        var scaleX = scaleY * font.ScaleX;
        var glyphs = new ushort[infos.Length];
        var points = new SKPoint[infos.Length];
        float x = 0, y = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            points[i] = new SKPoint(x + positions[i].XOffset * scaleX, y - positions[i].YOffset * scaleY);
            x += positions[i].XAdvance * scaleX;
            y += positions[i].YAdvance * scaleY;
        }
        return (glyphs, points, x);
    }

    /// <summary>An sfnt of every table but the bitmap/colour ones; HarfBuzz reads no checksums.</summary>
    private static byte[] BuildSfnt(SKTypeface typeface)
    {
        var tables = new List<(uint Tag, byte[] Data)>();
        if (typeface.TryGetTableTags(out var tags))
        {
            foreach (var tag in tags)
            {
                if (!BitmapTables.Contains(tag) && typeface.TryGetTableData(tag, out var data) && data.Length > 0)
                {
                    tables.Add((tag, data));
                }
            }
        }
        // HarfBuzz binary-searches the table directory, so it is sorted by tag.
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        var offset = 12 + 16 * tables.Count;
        var size = offset;
        foreach (var (_, data) in tables)
        {
            size += (data.Length + 3) & ~3;
        }
        var sfnt = new byte[size];
        var span = sfnt.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, tables.Exists(t => t.Tag == TagOf("CFF ")) ? TagOf("OTTO") : 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)tables.Count);
        for (var i = 0; i < tables.Count; i++)
        {
            var record = span.Slice(12 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(span[offset..]);
            offset += (tables[i].Data.Length + 3) & ~3;
        }
        return sfnt;
    }

    private static uint TagOf(string tag) =>
        (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
}
