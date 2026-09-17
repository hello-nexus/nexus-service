using System;

namespace Nexus.Service.Lighting;

/// <summary>
/// How a point curve travels between its points. Mirror of nexus-web's
/// <c>lib/curveEasing.ts</c>, which draws the same curve; keep the two in step
/// so a schedule reads the same on the graph and on the LEDs.
/// </summary>
public static class CurveEasing
{
    /// <summary>Value of the curve at <paramref name="x"/>. Points must be
    /// sorted by x. <paramref name="smooth"/> selects a monotone cubic spline
    /// (Fritsch-Carlson) through every point with no overshoot; otherwise the
    /// rule is piecewise-linear. A <paramref name="wrapSpan"/> above zero makes
    /// the axis circular over <c>[wrapMin, wrapMin + wrapSpan)</c>, joining the
    /// outermost points across the seam; without one the curve holds its end
    /// values outside the point range.</summary>
    public static double Interpolate(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double x,
        bool smooth, double wrapMin = 0, double wrapSpan = 0)
    {
        var n = xs.Length;
        if (n == 0) return 0;
        if (n == 1) return ys[0];
        var wrap = wrapSpan > 0;
        var xx = x;
        if (wrap)
        {
            xx = wrapMin + ((x - wrapMin) % wrapSpan + wrapSpan) % wrapSpan;
        }
        else
        {
            if (xx <= xs[0]) return ys[0];
            if (xx >= xs[n - 1]) return ys[n - 1];
        }
        // Segment i runs from point i to point i+1; on a wrapping axis the last
        // segment runs from the last point back to the first, one span later,
        // and owns both ends of the axis.
        var i = n - 1;
        for (var k = 0; k < n - 1; k++)
        {
            if (xx >= xs[k] && xx < xs[k + 1]) { i = k; break; }
        }
        if (i == n - 1 && xx < xs[0]) xx += wrapSpan;
        var next = (i + 1) % n;
        var x0 = xs[i];
        var x1 = xs[next] + (next == 0 ? wrapSpan : 0);
        var h = x1 - x0;
        if (h <= 0) return ys[i];
        var t = (xx - x0) / h;
        if (!smooth) return ys[i] + (ys[next] - ys[i]) * t;
        Span<double> m = n <= 64 ? stackalloc double[n] : new double[n];
        Tangents(xs, ys, wrap ? wrapSpan : 0, m);
        var t2 = t * t;
        var t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * ys[i]
            + (t3 - 2 * t2 + t) * h * m[i]
            + (-2 * t3 + 3 * t2) * ys[next]
            + (t3 - t2) * h * m[next];
    }

    // Fritsch-Carlson tangents: the average of the neighbouring secants, zero
    // at an extremum, then limited so no segment overshoots. A span above zero
    // treats the points as circular, so every point has two neighbours.
    private static void Tangents(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double span, Span<double> m)
    {
        var n = xs.Length;
        var wrap = span > 0;
        var segments = wrap ? n : n - 1;
        Span<double> d = segments <= 64 ? stackalloc double[segments] : new double[segments];
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % n;
            var dx = xs[next] - xs[i] + (next == 0 ? span : 0);
            d[i] = dx > 0 ? (ys[next] - ys[i]) / dx : 0;
        }
        for (var i = 0; i < n; i++)
        {
            var hasPrev = wrap || i > 0;
            var hasNext = wrap || i < n - 1;
            if (hasPrev && hasNext)
            {
                var dp = d[(i - 1 + segments) % segments];
                var dn = d[i % segments];
                m[i] = dp * dn <= 0 ? 0 : (dp + dn) / 2;
            }
            else
            {
                m[i] = hasNext ? d[i] : d[i - 1];
            }
        }
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % n;
            if (d[i] == 0) { m[i] = 0; m[next] = 0; continue; }
            var a = m[i] / d[i];
            var b = m[next] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var tau = 3 / Math.Sqrt(s);
                m[i] = tau * a * d[i];
                m[next] = tau * b * d[i];
            }
        }
    }
}
