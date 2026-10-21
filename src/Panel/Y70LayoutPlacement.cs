using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>
/// First-free-rect widget placement on a Y70 layout, mirroring nexus-web's
/// panelLayoutOps.appendWidget / paginate.firstFreeRect exactly (same stride
/// and scan order), so a widget lands where the editor would have put it.
/// Y70 grid dimensions are fixed (see GridCols/GridRows); overflow spills to a
/// new page, capped at MaxPages.
/// </summary>
internal static class Y70LayoutPlacement
{
    public const int GridCols = 4;
    public const int GridRows = 16;
    public const int MaxPages = 10;

    /// <summary>Span for a stored size: the legacy enum names plus any
    /// <c>"{cols}x{rows}"</c> pair of positive ints, clamped to the grid.
    /// Anything unparseable is a 2x2.</summary>
    public static (int Cols, int Rows) SpanForSize(string? size)
    {
        if (size == "2x2round")
        {
            return (2, 2);
        }
        var parts = size?.Split('x');
        if (parts is { Length: 2 }
            && int.TryParse(parts[0], out var cols)
            && int.TryParse(parts[1], out var rows)
            && cols > 0
            && rows > 0)
        {
            return (cols > GridCols ? GridCols : cols, rows > GridRows ? GridRows : rows);
        }
        return (2, 2);
    }

    private static int SnapStride(int span) => span <= 1 ? 1 : 2;

    /// <summary>Appends one widget to the growing page list: scans every existing
    /// page in order for a free rect, creates a new page (capped at
    /// <see cref="MaxPages"/>) when none fits. Returns false when the widget
    /// could not be placed (page cap reached).</summary>
    public static bool Append(List<PanelPageDto> pages, PanelWidgetDto widget, string size)
    {
        var (colSpan, rowSpan) = SpanForSize(size);
        foreach (var page in pages)
        {
            var slot = FirstFreeRect(page.Widgets, colSpan, rowSpan);
            if (slot is { } found)
            {
                widget.Col = found.Col;
                widget.Row = found.Row;
                page.Widgets.Add(widget);
                return true;
            }
        }
        if (pages.Count >= MaxPages)
        {
            return false;
        }
        widget.Col = 0;
        widget.Row = 0;
        pages.Add(new PanelPageDto { Id = System.Guid.NewGuid().ToString(), Widgets = { widget } });
        return true;
    }

    /// <summary>Places <paramref name="widget"/> on the first page at the exact cell when its rect is inside the grid and clear of every widget there; false leaves the pages untouched.</summary>
    public static bool TryPlaceAt(List<PanelPageDto> pages, PanelWidgetDto widget, string size, int col, int row)
    {
        var (colSpan, rowSpan) = SpanForSize(size);
        if (col < 0 || row < 0 || col + colSpan > GridCols || row + rowSpan > GridRows)
        {
            return false;
        }
        var target = (col, row, colSpan, rowSpan);
        if (pages.Count > 0)
        {
            foreach (var w in pages[0].Widgets)
            {
                var (ecs, ers) = SpanForSize(w.Size);
                if (RectsOverlap((w.Col, w.Row, ecs > GridCols ? GridCols : ecs, ers), target))
                {
                    return false;
                }
            }
        }
        widget.Col = col;
        widget.Row = row;
        if (pages.Count == 0)
        {
            pages.Add(new PanelPageDto { Id = System.Guid.NewGuid().ToString() });
        }
        pages[0].Widgets.Add(widget);
        return true;
    }

    private static (int Col, int Row)? FirstFreeRect(List<PanelWidgetDto> existing, int colSpan, int rowSpan)
    {
        var cs = colSpan > GridCols ? GridCols : colSpan;
        var rs = rowSpan;
        var rects = new List<(int Col, int Row, int ColSpan, int RowSpan)>(existing.Count);
        foreach (var w in existing)
        {
            var (ecs, ers) = SpanForSize(w.Size);
            rects.Add((w.Col, w.Row, ecs > GridCols ? GridCols : ecs, ers));
        }

        // Stride-aligned pass first (snap aesthetics), then a stride-1 pass so
        // off-stride holes (left by a 1x1 neighbour) still accept the widget.
        foreach (var (colStep, rowStep) in StrideScanSteps(cs, rs))
        {
            for (var r = 0; r + rs <= GridRows; r += rowStep)
            {
                for (var c = 0; c + cs <= GridCols; c += colStep)
                {
                    var overlaps = false;
                    foreach (var rect in rects)
                    {
                        if (RectsOverlap(rect, (c, r, cs, rs)))
                        {
                            overlaps = true;
                            break;
                        }
                    }
                    if (!overlaps)
                    {
                        return (c, r);
                    }
                }
            }
        }
        return null;
    }

    private static IEnumerable<(int Col, int Row)> StrideScanSteps(int colSpan, int rowSpan)
    {
        var col = SnapStride(colSpan);
        var row = SnapStride(rowSpan);
        yield return (col, row);
        if (col != 1 || row != 1)
        {
            yield return (1, 1);
        }
    }

    private static bool RectsOverlap((int Col, int Row, int ColSpan, int RowSpan) a, (int Col, int Row, int ColSpan, int RowSpan) b) =>
        a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan &&
        a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;
}
