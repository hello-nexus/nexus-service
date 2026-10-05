using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// A keyboard's LEDs in key units (one key pitch = 1 on both axes), derived
/// from the frame's normalised LED positions, plus the key-name lookup that
/// turns a press into an LED. Immutable; rebuilt when the frame's layout arrays
/// are swapped.
/// </summary>
public sealed class KeyboardGeometry
{
    private readonly Dictionary<string, int> _byName;
    private readonly bool[] _active;

    public int LedCount { get; }
    public float[] X { get; }
    public float[] Y { get; }
    public float MinX { get; }
    public float MaxX { get; }
    public float MinY { get; }
    public float MaxY { get; }
    /// <summary>LEDs whose name resolved into the key-name space.</summary>
    public int NamedKeys => _byName.Count;

    private KeyboardGeometry(float[] x, float[] y, bool[] active, Dictionary<string, int> byName)
    {
        LedCount = x.Length;
        X = x;
        Y = y;
        _active = active;
        _byName = byName;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < x.Length; i++)
        {
            if (!active[i]) continue;
            minX = Math.Min(minX, x[i]);
            maxX = Math.Max(maxX, x[i]);
            minY = Math.Min(minY, y[i]);
            maxY = Math.Max(maxY, y[i]);
        }
        if (minX > maxX) { minX = maxX = minY = maxY = 0; }
        MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY;
    }

    public bool IsActive(int led) => led >= 0 && led < _active.Length && _active[led];

    /// <param name="u">Normalised x per LED.</param>
    /// <param name="v">Normalised y per LED.</param>
    /// <param name="names">Hardware LED names per LED, or null when the board reports none.</param>
    /// <param name="disabled">LEDs the map turned off; they never react.</param>
    public static KeyboardGeometry Build(float[] u, float[] v, IReadOnlyList<string?>? names, bool[]? disabled)
    {
        var n = Math.Min(u.Length, v.Length);
        var active = new bool[n];
        for (var i = 0; i < n; i++)
        {
            active[i] = !(disabled is not null && i < disabled.Length && disabled[i])
                && float.IsFinite(u[i]) && float.IsFinite(v[i]);
        }
        var (unitU, unitV) = EstimatePitch(u, v, active);
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = u[i] / unitU;
            y[i] = v[i] / unitV;
        }
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        if (names is not null)
        {
            for (var i = 0; i < n && i < names.Count; i++)
            {
                if (!active[i]) continue;
                var key = KeyNames.Normalize(names[i]);
                if (key is not null) byName.TryAdd(key, i);
            }
        }
        return new KeyboardGeometry(x, y, active, byName);
    }

    /// <summary>The LED a pressed key lights, or -1 when the board has none there.</summary>
    public int Resolve(string canonical)
    {
        foreach (var candidate in KeyNames.Candidates(canonical))
        {
            if (_byName.TryGetValue(candidate, out var led)) return led;
        }
        // Named boards are trusted: a key they do not list is a key they lack.
        if (_byName.Count > 0) return -1;
        return ResolveByPosition(canonical);
    }

    /// <summary>Nearest LED to the key's nominal spot scaled onto this board; a board with fewer LEDs than a full-size one is taken as tenkeyless.</summary>
    private int ResolveByPosition(string canonical)
    {
        if (!KeyNames.NominalPositions.TryGetValue(canonical, out var nominal)) return -1;
        var width = CountActive() >= 100 ? KeyNames.NominalFullWidth : KeyNames.NominalTklWidth;
        if (nominal.X > width + 0.5f) return -1;
        var spanX = Math.Max(MaxX - MinX, 1f);
        var spanY = Math.Max(MaxY - MinY, 1f);
        var tx = MinX + nominal.X / width * spanX;
        var ty = MinY + nominal.Y / KeyNames.NominalBottomRow * spanY;
        return Nearest(tx, ty, 1.5f);
    }

    /// <summary>The active LED nearest (x, y) within <paramref name="maxDistance"/> key units, or -1.</summary>
    public int Nearest(float x, float y, float maxDistance)
    {
        var best = -1;
        var bestD = maxDistance * maxDistance;
        for (var i = 0; i < LedCount; i++)
        {
            if (!_active[i]) continue;
            var dx = X[i] - x;
            var dy = Y[i] - y;
            var d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Distance from (x, y) to the farthest active LED.</summary>
    public float MaxDistanceFrom(float x, float y)
    {
        var max = 0f;
        for (var i = 0; i < LedCount; i++)
        {
            if (!_active[i]) continue;
            var dx = X[i] - x;
            var dy = Y[i] - y;
            max = Math.Max(max, dx * dx + dy * dy);
        }
        return MathF.Sqrt(max);
    }

    private int CountActive()
    {
        var c = 0;
        foreach (var a in _active) if (a) c++;
        return c;
    }

    /// <summary>One key pitch per axis: median row gap and median in-row LED gap, so group gaps and stray LEDs do not skew it.</summary>
    private static (float U, float V) EstimatePitch(float[] u, float[] v, bool[] active)
    {
        var points = new List<(float U, float V)>();
        for (var i = 0; i < active.Length; i++) if (active[i]) points.Add((u[i], v[i]));
        if (points.Count < 2) return (1f, 1f);

        // Rows: cluster sorted v values; anything within a sliver is one row.
        const float SameRow = 0.004f;
        points.Sort((a, b) => a.V.CompareTo(b.V));
        var rows = new List<List<float>>();
        var rowV = new List<float>();
        foreach (var p in points)
        {
            if (rowV.Count == 0 || p.V - rowV[^1] > SameRow)
            {
                rowV.Add(p.V);
                rows.Add(new List<float>());
            }
            rows[^1].Add(p.U);
        }
        var rowGaps = new List<float>();
        for (var i = 1; i < rowV.Count; i++) rowGaps.Add(rowV[i] - rowV[i - 1]);
        var colGaps = new List<float>();
        foreach (var row in rows)
        {
            row.Sort();
            for (var i = 1; i < row.Count; i++)
            {
                var gap = row[i] - row[i - 1];
                if (gap > 1e-4f) colGaps.Add(gap);
            }
        }
        var unitV = rowGaps.Count > 0 ? Median(rowGaps) : 0f;
        var unitU = colGaps.Count > 0 ? Median(colGaps) : 0f;
        // A single row or a single column borrows the other axis's pitch.
        if (unitU <= 0 && unitV <= 0) return (1f, 1f);
        if (unitU <= 0) unitU = unitV;
        if (unitV <= 0) unitV = unitU;
        return (unitU, unitV);
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
