using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Byte-level and round-trip tests for the TinyUZ LZ77 encoder/decoder. The
/// exact-byte vectors are computed by transliterating the upstream
/// sisong/tinyuz bit-writer (compress/tuz_enc_private/tuz_enc_code.cpp's
/// TTuzCode::outType/outLen/outDictPos/outDict/outCtrl) into a small Python
/// script and running it, not guessed or reverse-engineered from this port.
/// </summary>
public class TinyUzTests
{
    [Fact]
    public void Compress_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(() => TinyUz.Compress(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compress_header_carries_dict_size_4096_little_endian()
    {
        var encoded = TinyUz.Compress(new byte[] { 0x01 });
        Assert.Equal(0x00, encoded[0]);
        Assert.Equal(0x10, encoded[1]);
        Assert.Equal(0x00, encoded[2]);
        Assert.Equal(0x00, encoded[3]);
    }

    [Fact]
    public void Compress_black_30_frame_animation_matches_the_lconnect_capture_after_the_header()
    {
        // Y70 USBPcap 2026-09-04, lc12-static-red: L-Connect's data part for a
        // 30-frame x 120-LED all-zero buffer was 01 00 00 00 b9 00 ab fb 97 01 00.
        // The code bytes after its 4-byte header are this encoder's, byte for byte.
        var expected = Convert.FromHexString("b900abfb970100");
        var encoded = TinyUz.Compress(new byte[30 * 120 * 3]);
        Assert.Equal(expected, encoded.AsSpan(4).ToArray());
    }

    [Fact]
    public void Compress_single_byte_matches_hand_derived_bytes()
    {
        // Header (4B dictSize=4096 LE) + one control byte (0x19) packing the
        // literal type-bit, the stream-end length code, and the "no reuse"
        // flag + the literal byte + the trailing dict_pos=0 byte.
        var expected = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x19, 0xAB, 0x00 };
        var encoded = TinyUz.Compress(new byte[] { 0xAB });
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_emits_a_back_reference_matching_the_reference_bit_writer()
    {
        // "ABCABC": 3 literal bytes (A,B,C, too short for a literal-line),
        // then one dict match (length 3, distance 3) covering the repeat,
        // then the stream-end control code. Bytes computed by the reference
        // bit-writer script described in the class summary.
        var expected = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x17, 0x41, 0x42, 0x43, 0x03, 0x06, 0x00 };
        var encoded = TinyUz.Compress(new byte[] { 0x41, 0x42, 0x43, 0x41, 0x42, 0x43 });
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Decompress_handles_clip_end_control_code()
    {
        // Literal 'A', a clip-end control code (byte-aligns the type-bit
        // stream and continues decoding with no output), literal 'B', then
        // stream-end. This is a firmware-legal stream our own encoder never
        // emits (it never splits into clips); bytes computed by the
        // reference bit-writer script described in the class summary.
        var encoded = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x09, 0x41, 0x00, 0x19, 0x42, 0x00 };
        var (header, decoded) = TinyUz.Decompress(encoded);
        Assert.Equal(TinyUz.StreamHeaderValue, header);
        Assert.Equal(new byte[] { 0x41, 0x42 }, decoded);
    }

    [Fact]
    public void Compress_decompress_roundtrips_arbitrary_data()
    {
        var data = new byte[64];
        new Random(1234).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (header, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(TinyUz.StreamHeaderValue, header);
        Assert.Equal(data, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(360)]
    public void Compress_decompress_roundtrips_at_type_byte_boundary_lengths(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(5000)]
    public void Compress_decompress_roundtrips_around_dict_size_boundary(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Compress_decompress_roundtrips_a_match_at_the_maximum_dict_distance()
    {
        // A DictSize-byte random head, then a 900-byte copy of its first 900
        // bytes appended: the tail's best match sits at distance exactly
        // DictSize (the largest distance the format allows), forcing the
        // multi-byte dict_pos code and the BigPosForLen length borrow.
        var head = new byte[TinyUz.DictSize];
        new Random(7).NextBytes(head);
        var data = new byte[TinyUz.DictSize + 900];
        Array.Copy(head, 0, data, 0, TinyUz.DictSize);
        Array.Copy(head, 0, data, TinyUz.DictSize, 900);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < data.Length, "the long-distance repeat should be found and shrink the output");
    }

    [Fact]
    public void Compress_solid_color_frame_compresses_well_under_original_size()
    {
        var data = new byte[360];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = 200;
            data[i + 1] = 100;
            data[i + 2] = 50;
        }

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < 50, $"expected well under 360 bytes, got {encoded.Length}");
    }

    [Fact]
    public void Compress_gradient_pattern_compresses_meaningfully()
    {
        // 40 LEDs of a smooth RGB gradient (4 LEDs per shade step), matching
        // the repeat-heavy shape of a real ring animation.
        var data = new byte[40 * 3];
        for (var led = 0; led < 40; led++)
        {
            var shade = (byte)(led / 4);
            data[led * 3] = shade;
            data[led * 3 + 1] = (byte)(255 - shade);
            data[led * 3 + 2] = shade;
        }

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < data.Length / 2, $"expected a meaningful reduction from {data.Length} bytes, got {encoded.Length}");
    }

    [Fact]
    public void Compress_realistic_incompressible_frame_stays_under_max_compressed_length()
    {
        // A DictSize-sized frame of random (worst-case incompressible) bytes
        // is far larger than any real animation frame the firmware receives.
        var data = new byte[TinyUz.DictSize];
        new Random(99).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length <= TinyUz.MaxCompressedLength);
    }

    [Fact]
    public void Compress_throws_when_output_exceeds_max_compressed_length()
    {
        // Random (incompressible) data comfortably larger than
        // MaxCompressedLength: even the byte-aligned literal-line path (about
        // 8 bits/byte plus a small fixed overhead) cannot fit this many
        // source bytes under the cap.
        var data = new byte[TinyUz.MaxCompressedLength + 4096];
        new Random(555).NextBytes(data);
        Assert.Throws<InvalidOperationException>(() => TinyUz.Compress(data));
    }

    [Fact]
    public void Compress_literal_line_matches_reference_bit_writer()
    {
        // 20 bytes of a collision-free filler (no 3-byte window repeats, per
        // Section2 of the class summary): the whole run stays one
        // literal-line control code plus a raw 20-byte copy, no accidental
        // match. Bytes computed by the reference bit-writer script.
        var data = LcgFiller(20, 1);
        var expected = Combine(
            new byte[] { 0x00, 0x10, 0x00, 0x00, 0x62, 0x00, 0x18 },
            data,
            new byte[] { 0x00 });

        var encoded = TinyUz.Compress(data);

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_multi_byte_dict_pos_matches_reference_bit_writer()
    {
        // 3-byte trigraph, 125 bytes of collision-free filler, the same
        // trigraph again: the only match is the trailing repeat at distance
        // 128, past the 1-byte dict_pos range (>= 128), forcing the
        // multi-byte dict_pos encoding. Bytes computed by the reference
        // bit-writer script.
        var trigraph = new byte[] { 0x41, 0x42, 0x43 };
        var filler = LcgFiller(125, 1);
        var data = Combine(trigraph, filler, trigraph);
        var expected = Combine(
            new byte[] { 0x00, 0x10, 0x00, 0x00, 0x62, 0x00, 0x1F },
            trigraph,
            filler,
            new byte[] { 0x01, 0x80, 0x03, 0x00 });

        var encoded = TinyUz.Compress(data);

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_big_pos_for_len_borrow_matches_reference_bit_writer()
    {
        // Same shape as the distance=128 case, but with a 2685-byte filler
        // pushing the trailing match to distance 2688 (> BigPosForLen=2687),
        // which borrows one from the length code on the wire (undone by the
        // decoder's +1). Bytes computed by the reference bit-writer script.
        var trigraph = new byte[] { 0x11, 0x22, 0x33 };
        var filler = LcgFiller(2685, 1);
        var data = Combine(trigraph, filler, trigraph);
        var expected = Combine(
            new byte[] { 0x00, 0x10, 0x00, 0x00, 0x6A, 0x00, 0xD9, 0x07 },
            trigraph,
            filler,
            new byte[] { 0x48, 0x80, 0x30, 0x00 });

        var encoded = TinyUz.Compress(data);

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_reuse_bit_matches_reference_bit_writer()
    {
        // Two distinct 3-byte-periodic segments back to back: each restarts
        // its own "3 literal bytes then a distance-3 match" cycle (the
        // second segment's bytes never occurred before its own start), so
        // both matches land on distance 3. The second match immediately
        // follows a literal with the same dict position as the first match,
        // so the reuse bit fires and the dict_pos byte is omitted. Bytes
        // computed by the reference bit-writer script.
        var seg1 = Combine(new byte[] { 0xFF, 0x00, 0x00 }, new byte[] { 0xFF, 0x00, 0x00 }, new byte[] { 0xFF, 0x00, 0x00 }, new byte[] { 0xFF, 0x00, 0x00 });
        var seg2 = Combine(new byte[] { 0xAA, 0xBB, 0xCC }, new byte[] { 0xAA, 0xBB, 0xCC }, new byte[] { 0xAA, 0xBB, 0xCC }, new byte[] { 0xAA, 0xBB, 0xCC });
        var data = Combine(seg1, seg2);
        var expected = new byte[]
        {
            0x00, 0x10, 0x00, 0x00, 0xA7, 0xFF, 0x00, 0x00, 0x39, 0x03, 0xAA, 0xBB, 0xCC, 0x2D, 0x03, 0x00,
        };

        var encoded = TinyUz.Compress(data);

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_distance_equal_to_dict_size_matches_reference_bit_writer()
    {
        // The reference encoder's own invariant is dict_pos < dictSize, i.e.
        // distance <= dictSize (compress/tuz_enc_private/tuz_enc_clip.cpp's
        // checkv(dict_pos<props.dictSize) with dict_pos = distance-1); the
        // firmware's circular-buffer decoder (tuz_dec.c's _dict_read_byte)
        // resolves distance==dictSize to dictType_pos=0, reading the oldest
        // still-live ring byte before it gets overwritten, which is exactly
        // dictSize bytes back. distance==DictSize is therefore valid on both
        // sides and this port's matcher accepts it (cur-candidate<=DictSize).
        // Trigraph, 4093 bytes of collision-free filler, trigraph again: the
        // trailing match sits at distance exactly DictSize. Bytes computed
        // by the reference bit-writer script.
        var trigraph = new byte[] { 0x99, 0x88, 0x77 };
        var filler = LcgFiller(4093, 1);
        var data = Combine(trigraph, filler, trigraph);
        Assert.Equal(TinyUz.DictSize, data.Length - trigraph.Length);
        var expected = Combine(
            new byte[] { 0x00, 0x10, 0x00, 0x00, 0xB2, 0x00, 0xDD, 0x07 },
            trigraph,
            filler,
            new byte[] { 0xE8, 0x80, 0x31, 0x00 });

        var encoded = TinyUz.Compress(data);

        Assert.Equal(expected, encoded);
    }

    // Deterministic 24-bit-state LCG (Numerical Recipes constants), top byte
    // taken per step: reproduces the same filler bytes the reference
    // bit-writer script used to derive the exact-byte vectors above, without
    // embedding thousands of literal bytes in this file. Every filler used
    // here was verified in that script to contain no repeated 3-byte window
    // and no accidental occurrence of its paired trigraph, so Compress finds
    // no match inside it.
    private static byte[] LcgFiller(int length, uint seed)
    {
        var result = new byte[length];
        var state = seed;
        for (var i = 0; i < length; i++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            result[i] = (byte)(state >> 24);
        }
        return result;
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
