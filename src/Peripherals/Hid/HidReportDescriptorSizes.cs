using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Max input, output and feature report lengths from a raw HID report descriptor,
/// in the same convention as the Windows <c>HidP_GetCaps</c> fields: bytes INCLUDING
/// the leading report-ID byte, which is counted even when the descriptor declares no
/// report IDs. A kind with no reports is 0.
/// </summary>
internal static class HidReportDescriptorSizes
{
    private const int MaxBits = 1 << 24;

    /// <summary>Returns all zeros for a malformed descriptor instead of throwing.</summary>
    public static (int Input, int Output, int Feature) Compute(ReadOnlySpan<byte> desc)
    {
        try
        {
            return ComputeCore(desc);
        }
        catch (Exception)
        {
            return (0, 0, 0);
        }
    }

    private static (int Input, int Output, int Feature) ComputeCore(ReadOnlySpan<byte> desc)
    {
        // bits per (kind, report id); kind 0 input, 1 output, 2 feature
        var bits = new Dictionary<(int Kind, int Id), long>();
        var stack = new Stack<(long Size, long Count, int Id)>();
        long size = 0, count = 0;
        var id = 0;

        var i = 0;
        while (i < desc.Length)
        {
            var prefix = desc[i];
            if (prefix == 0xFE)
            {
                // long item: [0xFE][data size][tag][data...]
                if (i + 1 >= desc.Length)
                {
                    return (0, 0, 0);
                }
                i += 3 + desc[i + 1];
                if (i > desc.Length)
                {
                    return (0, 0, 0);
                }
                continue;
            }

            var dataLen = (prefix & 0x03) == 3 ? 4 : (prefix & 0x03);
            var type = (prefix >> 2) & 0x03; // 0 Main, 1 Global, 2 Local
            var tag = (prefix >> 4) & 0x0F;
            if (i + 1 + dataLen > desc.Length)
            {
                return (0, 0, 0);
            }

            long data = 0;
            for (var k = 0; k < dataLen; k++)
            {
                data |= (long)desc[i + 1 + k] << (8 * k);
            }
            i += 1 + dataLen;

            if (type == 1)
            {
                switch (tag)
                {
                    case 0x7:
                        size = data;
                        break;
                    case 0x8:
                        if (data < 1 || data > 255)
                        {
                            return (0, 0, 0);
                        }
                        id = (int)data;
                        break;
                    case 0x9:
                        count = data;
                        break;
                    case 0xA:
                        stack.Push((size, count, id));
                        break;
                    case 0xB:
                        if (stack.Count == 0)
                        {
                            return (0, 0, 0);
                        }
                        (size, count, id) = stack.Pop();
                        break;
                }
            }
            else if (type == 0 && (tag == 0x8 || tag == 0x9 || tag == 0xB))
            {
                var kind = tag == 0x8 ? 0 : tag == 0x9 ? 1 : 2;
                var add = size * count;
                if (size < 0 || count < 0 || add > MaxBits)
                {
                    return (0, 0, 0);
                }
                var key = (kind, id);
                bits.TryGetValue(key, out var cur);
                bits[key] = cur + add;
            }
        }

        var max = new int[3];
        foreach (var ((kind, _), total) in bits)
        {
            if (total > MaxBits)
            {
                return (0, 0, 0);
            }
            var len = (int)((total + 7) / 8) + 1;
            if (len > max[kind])
            {
                max[kind] = len;
            }
        }
        return (max[0], max[1], max[2]);
    }
}
