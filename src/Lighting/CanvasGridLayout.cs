using System;

namespace Nexus.Service.Lighting;

/// <summary>
/// Default device-frame positions on the 1000x600 lighting canvas. The grid
/// scales with the total device count so every card lands in a distinct,
/// guaranteed-on-canvas cell.
///
/// nexus-web mirrors this in DeviceCanvas.tsx's defaultSlot(), which the canvas
/// Minimize action uses so a minimized frame lands where a reset would put it.
/// Change both together.
/// </summary>
internal static class CanvasGridLayout
{
    private const float CanvasW = 1000f;
    private const float CanvasH = 600f;
    private const float Pad = 12f;
    private const float MaxCardW = 140f;
    private const float MaxCardH = 120f;
    /// <summary>
    /// Half the vertical offset between neighbouring columns, before the
    /// per-cell slack clamp in <see cref="Slot"/> bounds it. The UI draws a
    /// device's name at its card's center, so cards sharing a row's center line
    /// stack every name onto that line and long names overlap.
    /// </summary>
    private const float ColumnStaggerY = 24f;

    public static (float x, float y, float w, float h) Slot(int index, int totalCount)
    {
        if (totalCount <= 0 || index < 0) return (Pad, Pad, MaxCardW, MaxCardH);

        var n = Math.Max(1, totalCount);
        var availW = CanvasW - 2f * Pad;
        var availH = CanvasH - 2f * Pad;
        // cols ≈ sqrt(n * aspect) so the grid mirrors the canvas shape.
        var aspect = availW / availH;
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * aspect)));
        var rows = Math.Max(1, (int)Math.Ceiling((float)n / cols));

        var cellW = availW / cols;
        var cellH = availH / rows;
        // Card sized as a fraction of the cell - no floor so high-N grids can shrink
        // cards arbitrarily without spilling out of their cell. MaxCard caps low-N from
        // ballooning to canvas-size.
        var cardW = Math.Min(MaxCardW, cellW * 0.92f);
        var cardH = Math.Min(MaxCardH, cellH * 0.7f);
        // Held to the MaxCard aspect at every density: clamping each axis on its
        // own turns the card wide and short in a wide cell, and a grid or ring
        // LED map renders letterboxed in it.
        var fit = Math.Min(cardW / MaxCardW, cardH / MaxCardH);
        cardW = MaxCardW * fit;
        cardH = MaxCardH * fit;

        var s = ((index % (cols * rows)) + cols * rows) % (cols * rows);
        var col = s % cols;
        var row = s / cols;

        var x = Pad + col * cellW + (cellW - cardW) * 0.5f;
        // Clamped to the cell's spare height so a staggered card cannot reach the
        // neighbouring row. Once the grid is dense enough for the clamp to bind,
        // the card sits flush to its cell edge and neighbouring columns separate
        // by twice the slack instead. Names still collide past roughly 28 devices,
        // but horizontally - the cells go narrower than half a name - which no
        // vertical offset can fix; the canvas de-collides labels at render time.
        var slack = (cellH - cardH) * 0.5f;
        var stagger = Math.Min(ColumnStaggerY, slack);
        var y = Pad + row * cellH + slack + (col % 2 == 0 ? -stagger : stagger);

        // Defensive clamp - float residue at the cell edges, which a fully
        // clamped stagger lands a card flush against, can fall a hair outside.
        // Compared against the far edge minus the card rather than against the
        // card's own right/bottom: float addition and subtraction round the
        // other way, so `x + cardW > limit` can read false while x still
        // exceeds `limit - cardW` by an ulp.
        if (x > CanvasW - Pad - cardW) x = CanvasW - Pad - cardW;
        if (y > CanvasH - Pad - cardH) y = CanvasH - Pad - cardH;
        if (x < Pad) x = Pad;
        if (y < Pad) y = Pad;

        return (x, y, cardW, cardH);
    }
}
