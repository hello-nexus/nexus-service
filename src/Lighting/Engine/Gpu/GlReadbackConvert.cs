using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Turns a bottom-up glReadPixels block (RGBA on ES, RGB on desktop GL) into the
/// top-left-origin RGB888 rows a <see cref="CanvasBuffer"/> holds.
/// </summary>
internal static class GlReadbackConvert
{
    /// <summary>
    /// Writes <paramref name="h"/> rows of <paramref name="w"/> pixels into <paramref name="dst"/>, last source row first,
    /// dropping alpha when <paramref name="rgba"/>. <paramref name="src"/> holds at least h rows of w pixels
    /// at 4 (rgba) or 3 bytes each.
    /// </summary>
    public static void FlipRows(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, bool rgba)
    {
        var srcStride = w * (rgba ? 4 : 3);
        var rowBytes = w * 3;
        for (var y = 0; y < h; y++)
        {
            var srcRow = src.Slice((h - 1 - y) * srcStride, srcStride);
            var dstRow = dst.Slice(y * rowBytes, rowBytes);
            if (rgba)
            {
                RgbaRowToRgb(srcRow, dstRow, w);
            }
            else
            {
                srcRow.CopyTo(dstRow);
            }
        }
    }

    private static void RgbaRowToRgb(ReadOnlySpan<byte> src, Span<byte> dst, int w)
    {
        // Observed in the shipped x64 AOT build: Vector128.Shuffle stayed an out-of-line software call, so SSSE3 is taken
        // through a runtime CPU check. ARM64 gets Vector128.Shuffle as a table lookup.
        if (Ssse3.IsSupported)
        {
            RowSsse3(src, dst, w);
        }
        else
        {
            RowVector128(src, dst, w);
        }
    }

    internal static void RowSsse3(ReadOnlySpan<byte> src, Span<byte> dst, int w)
    {
        var x = 0;
        if (Ssse3.IsSupported && src.Length >= w * 4 && dst.Length >= w * 3)
        {
            var pick = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0, 0, 0, 0);
            ref var s = ref MemoryMarshal.GetReference(src);
            ref var d = ref MemoryMarshal.GetReference(dst);
            // Each store is a full vector but carries fewer RGB bytes; the surplus is overwritten by the next step or the tail.
            for (; x + 4 <= w && (x * 3) + 16 <= w * 3; x += 4)
            {
                Ssse3.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(x * 4)), pick).StoreUnsafe(ref d, (nuint)(x * 3));
            }
        }
        Tail(src, dst, w, x);
    }

    internal static void RowVector128(ReadOnlySpan<byte> src, Span<byte> dst, int w)
    {
        var x = 0;
        if (Vector128.IsHardwareAccelerated && src.Length >= w * 4 && dst.Length >= w * 3)
        {
            ref var s = ref MemoryMarshal.GetReference(src);
            ref var d = ref MemoryMarshal.GetReference(dst);
            // The mask stays a literal inside the call: measured faster on ARM64 AOT than a hoisted local.
            for (; x + 4 <= w && (x * 3) + 16 <= w * 3; x += 4)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(x * 4)), Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0, 0, 0, 0)).StoreUnsafe(ref d, (nuint)(x * 3));
            }
        }
        Tail(src, dst, w, x);
    }

    private static void Tail(ReadOnlySpan<byte> src, Span<byte> dst, int w, int x)
    {
        for (; x < w; x++)
        {
            dst[x * 3] = src[x * 4];
            dst[(x * 3) + 1] = src[(x * 4) + 1];
            dst[(x * 3) + 2] = src[(x * 4) + 2];
        }
    }
}
