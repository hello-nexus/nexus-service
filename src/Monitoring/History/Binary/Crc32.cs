using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ArmCrc = System.Runtime.Intrinsics.Arm.Crc32;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// CRC-32 (the IEEE 802.3 / zlib / gzip polynomial, reflected input and
/// output). Hand-rolled rather than taking a package dependency: the
/// algorithm is a few lines, has no reflection or platform surface to
/// verify for AOT, and both RingFile slots and SuperBlock headers only need
/// one integrity check, not a choice of algorithms.
///
/// This is a non-cryptographic integrity check, not a security boundary: it
/// catches accidental corruption (a torn write between two on-disk versions
/// of the same slot - see RingFile) far more reliably than it would catch a
/// deliberately crafted collision.
///
/// Uses the ARM64 CRC32 instructions where present and slicing-by-8
/// elsewhere; both match the byte-at-a-time reference kept for tests. Whole
/// app-usage day segments are CRC-walked on every query, so throughput here
/// is user-visible latency.
/// </summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    // Table[k * 256 + b]: slicing-by-8 needs eight 256-entry tables; the k=0
    // table is the classic byte-at-a-time one.
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (var i = 0u; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }
            table[i] = value;
        }
        for (var k = 1; k < 8; k++)
        {
            for (var i = 0; i < 256; i++)
            {
                var prev = table[(k - 1) * 256 + i];
                table[k * 256 + i] = (prev >> 8) ^ table[prev & 0xFF];
            }
        }
        return table;
    }

    /// <summary>Computes the CRC-32 of <paramref name="data"/>. Deterministic
    /// and pure - the same bytes always produce the same result, which is
    /// the only property RingFile/SuperBlock's torn-write detection relies
    /// on.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        if (ArmCrc.Arm64.IsSupported)
        {
            crc = ComputeArm64(crc, data);
        }
        else
        {
            crc = ComputeSlicing8(crc, data);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeArm64(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            crc = ArmCrc.Arm64.ComputeCrc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        foreach (var b in data)
        {
            crc = ArmCrc.ComputeCrc32(crc, b);
        }
        return crc;
    }

    private static uint ComputeSlicing8(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Table;
        while (data.Length >= 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = table[7 * 256 + (one & 0xFF)]
                ^ table[6 * 256 + ((one >> 8) & 0xFF)]
                ^ table[5 * 256 + ((one >> 16) & 0xFF)]
                ^ table[4 * 256 + (one >> 24)]
                ^ table[3 * 256 + (two & 0xFF)]
                ^ table[2 * 256 + ((two >> 8) & 0xFF)]
                ^ table[1 * 256 + ((two >> 16) & 0xFF)]
                ^ table[two >> 24];
            data = data[8..];
        }
        foreach (var b in data)
        {
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        }
        return crc;
    }

    /// <summary>The plain byte-at-a-time table walk - the reference the
    /// faster paths above are tested against.</summary>
    internal static uint ComputeReference(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Test seam: the slicing-by-8 path regardless of hardware
    /// support, so the x64 fallback is exercised on an ARM64 dev box.</summary>
    internal static uint ComputeSlicing8ForTest(ReadOnlySpan<byte> data) => ComputeSlicing8(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
