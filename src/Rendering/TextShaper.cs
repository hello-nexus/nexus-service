using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Nexus.Service.Rendering;

/// <summary>
/// HarfBuzz shaping over one typeface, from a copy of its tables minus the bitmap and
/// colour ones: shaping never reads them, and they are most of a colour-emoji font.
/// Immutable once built, so concurrent Shape calls are safe.
/// </summary>
internal sealed unsafe class TextShaper
{
    /// <summary>HarfBuzz positions come back in units of size / Scale.</summary>
    private const int Scale = 512;

    private static readonly HashSet<uint> BitmapTables = new()
    {
        TagOf("sbix"), TagOf("CBDT"), TagOf("CBLC"), TagOf("EBDT"), TagOf("EBLC"), TagOf("EBSC"),
        TagOf("bdat"), TagOf("bloc"), TagOf("SVG "), TagOf("COLR"), TagOf("CPAL"),
    };

    private readonly HarfBuzzSharp.Font _font;

    private TextShaper(HarfBuzzSharp.Font font) => _font = font;

    /// <summary>A shaper for the typeface, or null when Skia exposes no character map for it (shaping would map every character to the missing glyph).</summary>
    public static TextShaper? Create(SKTypeface typeface)
    {
        var tables = ShapingTables(typeface);
        if (!tables.Exists(t => t.Tag == TagOf("cmap")))
        {
            return null;
        }
        var size = SfntSize(tables);
        var native = (byte*)NativeMemory.Alloc((nuint)size);
        WriteSfnt(tables, new Span<byte>(native, size));
        // The face keeps its own blob reference and the font its own face reference, so the
        // managed wrappers can go; HarfBuzz frees the copy through the release delegate.
        using var blob = new Blob((IntPtr)native, size, MemoryMode.ReadOnly, () => NativeMemory.Free(native));
        using var face = new Face(blob, 0) { UnitsPerEm = typeface.UnitsPerEm };
        var font = new HarfBuzzSharp.Font(face);
        font.SetScale(Scale, Scale);
        font.SetFunctionsOpenType();
        return new TextShaper(font);
    }

    /// <summary>Glyph ids and pen positions from x = 0 on the baseline, and the run's advance width.</summary>
    public (ushort[] Glyphs, SKPoint[] Points, float Width) Shape(string text, SKFont font)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        _font.Shape(buffer, Array.Empty<Feature>());

        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
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

    private static List<(uint Tag, byte[] Data)> ShapingTables(SKTypeface typeface)
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
        // HarfBuzz binary-searches a large table directory, so it is sorted by tag.
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return tables;
    }

    private static int SfntSize(List<(uint Tag, byte[] Data)> tables)
    {
        var size = 12 + 16 * tables.Count;
        foreach (var (_, data) in tables)
        {
            size += (data.Length + 3) & ~3;
        }
        return size;
    }

    /// <summary>An sfnt of the given tables, zero-filled; HarfBuzz reads no checksums or search fields.</summary>
    private static void WriteSfnt(List<(uint Tag, byte[] Data)> tables, Span<byte> span)
    {
        span.Clear();
        var offset = 12 + 16 * tables.Count;
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
    }

    private static uint TagOf(string tag) =>
        (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
}
