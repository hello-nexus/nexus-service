using System;
using System.Collections.Generic;

namespace Nexus.Service.Common;

/// <summary>
/// Case-insensitive ordering that compares digit runs by numeric value, so
/// "2" sorts before "10". Pure managed and culture-free, so it behaves the same
/// on every platform. Digit runs of any length compare without overflow. Ties
/// (case-only or leading-zero-only differences) fall back to ordinal, which
/// keeps the order total and stable.
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (IsDigit(x[i]) && IsDigit(y[j]))
            {
                var xs = i;
                var ys = j;
                while (i < x.Length && IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && IsDigit(y[j]))
                {
                    j++;
                }

                var xd = x.AsSpan(xs, i - xs).TrimStart('0');
                var yd = y.AsSpan(ys, j - ys).TrimStart('0');
                if (xd.Length != yd.Length)
                {
                    return xd.Length < yd.Length ? -1 : 1;
                }

                var c = xd.SequenceCompareTo(yd);
                if (c != 0)
                {
                    return c < 0 ? -1 : 1;
                }

                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy)
            {
                return cx < cy ? -1 : 1;
            }

            i++;
            j++;
        }

        var rx = x.Length - i;
        var ry = y.Length - j;
        if (rx != ry)
        {
            return rx < ry ? -1 : 1;
        }

        return string.CompareOrdinal(x, y);
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
