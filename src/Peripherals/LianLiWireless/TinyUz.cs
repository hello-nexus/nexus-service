using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure C# port of the TinyUZ wire format (github.com/sisong/tinyuz, MIT) as
/// used by the SLV3 firmware's RF_RgbSync decoder (plans/lianli-wireless-support.md
/// section 2): a 4-byte little-endian dictionary-size header followed by a
/// type-bit stream where each bit selects a literal data byte or a control
/// code. Control codes (codeType 0) carry an Elias-gamma-like length, an
/// optional "reuse last dict position" flag, and a dict_pos byte (0 marks a
/// ctrl code: literal-line=1, clip-end=2, stream-end=3; nonzero is a
/// back-reference distance). Field-by-field semantics are ported from
/// decompress/tuz_dec.c's tuz_decompress_mem and compress/tuz_enc_private/tuz_enc_code.cpp's
/// TTuzCode, both fetched from the upstream repo (default build: dict window
/// bounded, literal-line enabled). lian-li-linux (github.com/sgtaziz/lian-li-linux,
/// MIT) vendors this same upstream unmodified behind a thin C wrapper that only
/// overrides dictSize - confirming no firmware-specific format deviation.
/// The encoder emits real LZ77 back-references (greedy hash-chain match over
/// the 4096-byte window); Decompress is a faithful decoder covering every
/// control code so it stands in for the firmware side in tests.
/// </summary>
public static class TinyUz
{
    /// <summary>LZ77 dictionary window: 4096 (12-bit); a larger window crashes the firmware.</summary>
    public const int DictSize = 4096;

    /// <summary>Little-endian value of the 4-byte stream header: the dictionary window size.</summary>
    public const int StreamHeaderValue = DictSize;

    /// <summary>lzo_rgb_rf_valid_len cap; the firmware throws "out of max uz length" past this.</summary>
    public const int MaxCompressedLength = 12288;

    private const int CodeTypeDict = 0;
    private const int CodeTypeData = 1;

    private const int CtrlLiteralLine = 1;
    private const int CtrlClipEnd = 2;
    private const int CtrlStreamEnd = 3;

    /// <summary>tuz_kMinDictMatchLen: the shortest length a back-reference can encode.</summary>
    private const int MinDictMatchLen = 2;

    /// <summary>tuz_kMinLiteralLen: literal runs at or above this length use the byte-aligned literal-line control code instead of per-byte bit-packed literals.</summary>
    private const int MinLiteralLen = 15;

    /// <summary>tuz_kBigPosForLen: a non-reused dict position past this value borrows one from the length code (undone by <c>+1</c> on decode).</summary>
    private const int BigPosForLen = (1 << 11) + (1 << 9) + (1 << 7) - 1;

    private const int MatchHashBits = 15;
    private const int MatchHashSize = 1 << MatchHashBits;

    /// <summary>
    /// Matches shorter than this are rejected. 3 (not the format's minimum of
    /// 2) so every accepted match's length code stays non-negative after the
    /// <see cref="BigPosForLen"/> borrow without a per-match distance check.
    /// </summary>
    private const int MatchMinLen = 3;

    private const int MatchMaxChainSteps = 128;

    /// <summary>
    /// Encodes <paramref name="data"/> as a TinyUZ stream: a greedy hash-chain
    /// LZ77 pass over the <see cref="DictSize"/> window emits back-references,
    /// falling back to literals (bit-packed, or byte-aligned literal-line runs
    /// at or above <see cref="MinLiteralLen"/>) where no window match is worth
    /// its encoding cost. Throws if <paramref name="data"/> is empty or the
    /// encoded output exceeds <see cref="MaxCompressedLength"/>.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new ArgumentException("data cannot be empty", nameof(data));
        }

        var buf = data.ToArray();
        var code = new List<byte>(buf.Length + buf.Length / 8 + 8);
        // Stream header: the reference's little-endian dictSize. L-Connect's
        // yuz.dll writes 01 00 00 00 here instead (Y70 USBPcap 2026-09-04); its
        // code bytes after the header match this encoder exactly, but the only
        // L-Connect streams captured were all-black (distance-1 matches only),
        // and the reference decoder rejects any match distance >= the header's
        // dictSize, so a 1 here would break every real frame. 4096 is the value
        // the fans have rendered from since 2026-07-03.
        for (var shift = 0; shift < 4; shift++)
        {
            code.Add((byte)((StreamHeaderValue >> (8 * shift)) & 0xFF));
        }

        var typeCount = 0;
        var typesIndex = -1;
        var dictPosBack = 1;
        var isHaveDataBack = false;

        void OutType(int bit)
        {
            if (typeCount == 0)
            {
                typesIndex = code.Count;
                code.Add(0);
            }
            code[typesIndex] = (byte)(code[typesIndex] | ((bit & 1) << typeCount));
            typeCount = (typeCount + 1) % 8;
            if (typeCount == 0)
            {
                typesIndex = -1;
            }
        }

        // Elias-gamma-like length code: decompose value into (count, remainder)
        // by repeatedly subtracting the next power-of-(1<<packBit) threshold,
        // then emit each chunk MSB-first with a continuation bit.
        void OutLen(int value, int packBit)
        {
            var count = 1;
            var v = value;
            while (v >= (1 << (count * packBit)))
            {
                v -= 1 << (count * packBit);
                count++;
            }
            for (var idx = count - 1; idx >= 0; idx--)
            {
                for (var bitIndex = 0; bitIndex < packBit; bitIndex++)
                {
                    var shift = idx * packBit + bitIndex;
                    OutType((v >> shift) & 1);
                }
                OutType(idx > 0 ? 1 : 0);
            }
        }

        // dict_pos is a raw byte (bit 7 = continuation), not bit-packed like
        // every other field; pos=0 marks a control code.
        void OutDictPos(int pos)
        {
            var isOutLen = pos >= (1 << 7) ? 1 : 0;
            if (isOutLen == 1)
            {
                pos -= 1 << 7;
            }
            code.Add((byte)((pos & ((1 << 7) - 1)) | (isOutLen << 7)));
            if (isOutLen == 1)
            {
                OutLen(pos >> 7, 2);
            }
        }

        void OutCtrlCode(int ctrlValue)
        {
            OutType(CodeTypeDict);
            OutLen(ctrlValue, 1);
            if (isHaveDataBack)
            {
                OutType(0);
            }
            OutDictPos(0);
        }

        void EmitLiteral(int start, int len)
        {
            if (len >= MinLiteralLen)
            {
                OutCtrlCode(CtrlLiteralLine);
                OutLen(len - MinLiteralLen, 2);
                for (var i = 0; i < len; i++)
                {
                    code.Add(buf[start + i]);
                }
            }
            else
            {
                for (var i = 0; i < len; i++)
                {
                    OutType(CodeTypeData);
                    code.Add(buf[start + i]);
                }
            }
            isHaveDataBack = true;
        }

        void EmitMatch(int matchLen, int distance)
        {
            OutType(CodeTypeDict);
            var reuse = dictPosBack == distance && isHaveDataBack;
            var len = matchLen - MinDictMatchLen;
            if (!reuse && distance > BigPosForLen)
            {
                len--;
            }
            OutLen(len, 1);
            if (isHaveDataBack)
            {
                OutType(reuse ? 1 : 0);
            }
            if (!reuse)
            {
                OutDictPos(distance);
            }
            isHaveDataBack = false;
            dictPosBack = distance;
        }

        var head = new int[MatchHashSize];
        Array.Fill(head, -1);
        var prev = new int[buf.Length];

        int InsertAndGetPrevHead(int position)
        {
            var v = (uint)(buf[position] | (buf[position + 1] << 8) | (buf[position + 2] << 16));
            var h = (int)((v * 2654435761u) >> (32 - MatchHashBits));
            var prevHead = head[h];
            prev[position] = prevHead;
            head[h] = position;
            return prevHead;
        }

        int MatchLengthAt(int candidate, int cur)
        {
            var max = buf.Length - cur;
            var len = 0;
            while (len < max && buf[candidate + len] == buf[cur + len])
            {
                len++;
            }
            return len;
        }

        var back = 0;
        var cur = 0;
        while (cur < buf.Length)
        {
            var matchLen = 0;
            var matchDist = 0;
            if (cur + MatchMinLen <= buf.Length)
            {
                var candidate = InsertAndGetPrevHead(cur);
                var steps = 0;
                while (candidate >= 0 && cur - candidate <= DictSize && steps < MatchMaxChainSteps)
                {
                    var len = MatchLengthAt(candidate, cur);
                    if (len > matchLen)
                    {
                        matchLen = len;
                        matchDist = cur - candidate;
                        if (len >= DictSize)
                        {
                            break;
                        }
                    }
                    candidate = prev[candidate];
                    steps++;
                }
            }

            if (matchLen >= MatchMinLen)
            {
                if (cur > back)
                {
                    EmitLiteral(back, cur - back);
                }
                EmitMatch(matchLen, matchDist);
                var matchEnd = cur + matchLen;
                cur++;
                for (; cur < matchEnd; cur++)
                {
                    if (cur + MatchMinLen <= buf.Length)
                    {
                        InsertAndGetPrevHead(cur);
                    }
                }
                back = cur;
            }
            else
            {
                cur++;
            }
        }
        if (cur > back)
        {
            EmitLiteral(back, cur - back);
        }
        OutCtrlCode(CtrlStreamEnd);

        var result = code.ToArray();
        if (result.Length > MaxCompressedLength)
        {
            throw new InvalidOperationException("out of max uz length");
        }
        return result;
    }

    /// <summary>
    /// Decodes a stream produced by <see cref="Compress"/> (or any conforming
    /// TinyUZ encoder): literals, back-references, literal-line runs, and
    /// clip-end/stream-end control codes. Ported from tuz_decompress_mem, the
    /// reference's whole-buffer decoder; its bounded circular-dictionary
    /// sibling (tuz_TStream_decompress_partial, what the firmware runs under
    /// fixed RAM) is behaviorally identical for any encoder that keeps every
    /// back-reference distance within <see cref="DictSize"/>, which
    /// <see cref="Compress"/> guarantees. Used by tests as the firmware stand-in.
    /// </summary>
    internal static (int StreamHeader, byte[] Data) Decompress(ReadOnlySpan<byte> encodedSpan)
    {
        // Local functions below share mutable state across closures, which the
        // compiler cannot do over a ref-like Span; copy to an array first.
        var encoded = encodedSpan.ToArray();
        // The header is returned raw for the caller to check; decoding always
        // runs over the fixed DictSize window the firmware uses.
        var streamHeader = encoded[0] | (encoded[1] << 8) | (encoded[2] << 16) | (encoded[3] << 24);
        var pos = 4;
        var typeBits = 0;
        var bitsLeft = 0;

        byte ReadByte() => encoded[pos++];

        int ReadTypeBit()
        {
            if (bitsLeft == 0)
            {
                typeBits = ReadByte();
                bitsLeft = 8;
            }
            var bit = typeBits & 1;
            typeBits >>= 1;
            bitsLeft--;
            return bit;
        }

        int ReadLen(int packBit)
        {
            var value = 0;
            while (true)
            {
                var low = 0;
                for (var i = 0; i < packBit; i++)
                {
                    low |= ReadTypeBit() << i;
                }
                var flag = ReadTypeBit();
                value = (value << packBit) + low;
                if (flag == 0)
                {
                    return value;
                }
                value += 1;
            }
        }

        int ReadDictPos()
        {
            var b = ReadByte();
            if (b < (1 << 7))
            {
                return b;
            }
            var extra = ReadLen(2);
            return ((b & ((1 << 7) - 1)) | (extra << 7)) + (1 << 7);
        }

        var output = new List<byte>();
        var dictPosBack = 1;
        var isHaveDataBack = false;

        while (true)
        {
            var codeType = ReadTypeBit();
            if (codeType == CodeTypeData)
            {
                output.Add(ReadByte());
                isHaveDataBack = true;
                continue;
            }

            var savedLen = ReadLen(1);
            int savedDictPos;
            if (isHaveDataBack && ReadTypeBit() == 1)
            {
                savedDictPos = dictPosBack;
            }
            else
            {
                savedDictPos = ReadDictPos();
                if (savedDictPos > BigPosForLen)
                {
                    savedLen += 1;
                }
            }
            isHaveDataBack = false;

            if (savedDictPos != 0)
            {
                var matchLen = savedLen + MinDictMatchLen;
                dictPosBack = savedDictPos;
                if (savedDictPos > output.Count)
                {
                    throw new InvalidOperationException("TinyUZ dict position out of range");
                }
                var srcIndex = output.Count - savedDictPos;
                for (var k = 0; k < matchLen; k++)
                {
                    output.Add(output[srcIndex + k]);
                }
                continue;
            }

            if (savedLen == CtrlLiteralLine)
            {
                var literalLen = ReadLen(2) + MinLiteralLen;
                for (var k = 0; k < literalLen; k++)
                {
                    output.Add(ReadByte());
                }
                isHaveDataBack = true;
                continue;
            }

            dictPosBack = 1;
            bitsLeft = 0;
            if (savedLen == CtrlClipEnd)
            {
                continue;
            }
            if (savedLen == CtrlStreamEnd)
            {
                break;
            }
            throw new NotSupportedException($"unsupported TinyUZ control code {savedLen}");
        }
        return (streamHeader, output.ToArray());
    }
}
