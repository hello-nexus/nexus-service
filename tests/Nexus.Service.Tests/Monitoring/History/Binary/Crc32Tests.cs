using System;
using System.Text;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

public class Crc32Tests
{
    [Fact]
    public void Compute_MatchesTheStandardCheckValue()
    {
        // "123456789" is the standard CRC-32/ISO-HDLC (zlib/gzip) check
        // string; every conformant implementation of this polynomial
        // produces this exact value for it.
        var bytes = Encoding.ASCII.GetBytes("123456789");

        Assert.Equal(0xCBF43926u, Crc32.Compute(bytes));
    }

    [Fact]
    public void Compute_EmptyInput_IsZero()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compute_IsDeterministic_ForTheSameBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.Equal(Crc32.Compute(bytes), Crc32.Compute(bytes));
    }

    [Fact]
    public void Compute_ChangesWhenAnyByteChanges()
    {
        var original = new byte[] { 0, 0, 0, 0 };
        var flippedOneBit = new byte[] { 1, 0, 0, 0 };

        Assert.NotEqual(Crc32.Compute(original), Crc32.Compute(flippedOneBit));
    }

    [Fact]
    public void Compute_OfAllZeroBytes_IsNotZero()
    {
        // A defense-in-depth check alongside RingFile's explicit
        // UnwrittenStamp rejection (see RingFile.TryValidateAndCopy): a
        // freshly zero-filled body's Crc must not equal a zero-filled Crc
        // field for any real ring's body length, including the scalar ring's.
        var zeros = new byte[ScalarRingStore.BodyLength];

        Assert.NotEqual(0u, Crc32.Compute(zeros));
    }

    [Fact]
    public void Compute_MatchesTheByteAtATimeReference_ForEveryLengthAndAlignment()
    {
        // Covers the 8-byte-block paths (hardware on ARM64, slicing-by-8
        // elsewhere) plus every tail length, starting from every offset so
        // unaligned block reads are exercised too.
        var rng = new Random(1234);
        var bytes = new byte[4096];
        rng.NextBytes(bytes);

        for (var offset = 0; offset < 16; offset++)
        {
            for (var length = 0; length < 80; length++)
            {
                var slice = bytes.AsSpan(offset, length);
                Assert.Equal(Crc32.ComputeReference(slice), Crc32.Compute(slice));
                Assert.Equal(Crc32.ComputeReference(slice), Crc32.ComputeSlicing8ForTest(slice));
            }
        }

        Assert.Equal(Crc32.ComputeReference(bytes), Crc32.Compute(bytes));
        Assert.Equal(Crc32.ComputeReference(bytes), Crc32.ComputeSlicing8ForTest(bytes));
    }

    [Fact]
    public void ComputeSlicing8_MatchesTheStandardCheckValue()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");

        Assert.Equal(0xCBF43926u, Crc32.ComputeSlicing8ForTest(bytes));
    }
}
