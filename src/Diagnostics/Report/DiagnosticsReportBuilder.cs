using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>Plain snapshot of everything the report needs, gathered once so
/// <see cref="DiagnosticsReportBuilder.Build"/> is a pure function of data and
/// fully testable with fakes (mirrors <see cref="DiagnosticsHealthModel"/>'s
/// Compute/BuildHealth split).</summary>
public sealed record ReportSnapshot
{
    public DateTime GeneratedAtUtc { get; init; }
    public string MachineName { get; init; } = "";
    public string NexusVersion { get; init; } = "";
    public string ReportId { get; init; } = "";
    public DiagnosticsHealthResponse Health { get; init; } = new();
    public SystemSpecsResponse Specs { get; init; } = new();
    public MemoryInfoSnapshot MemoryInfo { get; init; } = MemoryInfoSnapshot.Unsupported;
    public GpuHealthSnapshot Gpu { get; init; } = GpuHealthSnapshot.Unsupported;
    public SmartSnapshot Smart { get; init; } = new() { Supported = false };
    public PnpProblemSnapshot Pnp { get; init; } = PnpProblemSnapshot.Unsupported;
    public IReadOnlyDictionary<string, int> Counts30d { get; init; } = new Dictionary<string, int>();
    public MemoryTestResult? LastMemoryTest { get; init; }
    /// <summary>User-ignored health component ids (preferences.diagnostics).
    /// Health already excludes them; the raw drive table marks them so the
    /// two never contradict each other on the page.</summary>
    public IReadOnlySet<string> IgnoredComponents { get; init; } = new HashSet<string>();
}

/// <summary>
/// Builds the single-page PDF diagnostics report. <see cref="GatherAsync"/> is
/// the DI-facing side that reads every live diagnostics module; it takes the
/// health response and SMART snapshot as already-computed values (not the
/// monitors that produce them). <see cref="Build"/> is the pure layout
/// function, safe to unit test with a hand-built snapshot.
///
/// Layout is a fixed budget: every section occupies the same page area and
/// the same number of row slots no matter how much data exists (drive count,
/// error counts). Rows with no data render a "-" placeholder rather than
/// collapsing, and overflow beyond a section's fixed row count becomes a
/// single "+N more" line - never a reflow. This is what keeps the report
/// exactly one page regardless of the machine it was generated on.
/// </summary>
public static class DiagnosticsReportBuilder
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;
    private const double Margin = 40;
    private const double ContentLeft = Margin;
    private const double ContentRight = PageWidth - Margin;
    private const double ContentWidth = ContentRight - ContentLeft;

    private const double BlackGray = 17.0 / 255.0;
    private const double MidGray = 102.0 / 255.0;
    private const double RuleGray = 153.0 / 255.0;

    private const int MaxStorageRows = 4;
    private const string Placeholder = "-";
    private const string NotAvailable = "Not available on this platform";

    public static async Task<ReportSnapshot> GatherAsync(
        DiagnosticsHealthResponse health,
        SystemSpecsCollector specsCollector,
        SmartSnapshot smart,
        GpuHealthMonitor gpu,
        EventLogMonitor events,
        MemoryDiagnosticOrchestrator memDiag,
        PnpProblemScanner pnp,
        IEnumerable<string> ignoredComponents,
        CancellationToken ct = default)
    {
        SystemSpecsResponse specs;
        try
        {
            specs = await specsCollector.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-report] system specs read failed: {ex.Message}");
            specs = new SystemSpecsResponse();
        }

        MemoryInfoSnapshot memInfo;
        try
        {
            memInfo = MemoryInfoProvider.GetSnapshot();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-report] memory info read failed: {ex.Message}");
            memInfo = MemoryInfoSnapshot.Unsupported;
        }

        return new ReportSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            MachineName = ResolveMachineName(specs),
            // BuildInfo.Version may carry a v prefix; the layout adds its own.
            NexusVersion = BuildInfo.Version.TrimStart('v', 'V'),
            ReportId = GenerateReportId(),
            Health = health,
            Specs = specs,
            MemoryInfo = memInfo,
            Gpu = gpu.Snapshot(),
            Smart = smart,
            Pnp = pnp.Snapshot(),
            Counts30d = events.CountsSince(TimeSpan.FromDays(30)),
            LastMemoryTest = memDiag.LastResult(),
            IgnoredComponents = new HashSet<string>(ignoredComponents, StringComparer.Ordinal),
        };
    }

    private static string ResolveMachineName(SystemSpecsResponse specs)
    {
        if (!string.IsNullOrWhiteSpace(specs.PcName))
        {
            return specs.PcName;
        }

        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return Placeholder;
        }
    }

    private static string GenerateReportId()
    {
        Span<byte> bytes = stackalloc byte[4];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] Build(ReportSnapshot s)
    {
        var content = new PdfContentBuilder();
        var y = PageHeight - Margin;

        DrawHeader(content, s, ref y);
        DrawStatusStrip(content, s, ref y);
        DrawHealthSummary(content, s, ref y);
        DrawSystemSpecs(content, s, ref y);
        DrawStorage(content, s, ref y);
        DrawStability(content, s, ref y);
        DrawFooter(content, s);

        return Assemble(content);
    }

    // ── Header ─────────────────────────────────────────────────────────────

    private static void DrawHeader(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        const double markSize = 42;
        var markBottomY = y - markSize;
        var markWidth = markSize * NexusReportAssets.MarkSourceWidth / NexusReportAssets.MarkSourceHeight;
        c.DrawVectorPath(NexusReportAssets.LoadMarkOperators(), NexusReportAssets.MarkSourceWidth,
            NexusReportAssets.MarkSourceHeight, ContentLeft, markBottomY, markWidth, markSize, BlackGray);

        const double wordmarkHeight = 20;
        var wordmarkWidth = wordmarkHeight * NexusReportAssets.WordmarkSourceWidth / NexusReportAssets.WordmarkSourceHeight;
        var wordmarkX = ContentLeft + markWidth + 12;
        var wordmarkY = markBottomY + (markSize - wordmarkHeight) / 2;
        c.DrawVectorPath(NexusReportAssets.LoadWordmarkOperators(), NexusReportAssets.WordmarkSourceWidth,
            NexusReportAssets.WordmarkSourceHeight, wordmarkX, wordmarkY, wordmarkWidth, wordmarkHeight, BlackGray);

        var localNow = s.GeneratedAtUtc.ToLocalTime();
        c.FillGray(BlackGray);
        c.TextRightAligned(ContentRight, y - 11, 14, true, PdfFonts.Sanitize("Diagnostics Report"));

        c.FillGray(MidGray);
        c.TextRightAligned(ContentRight, y - 24, 8, false,
            PdfFonts.Sanitize($"Generated {localNow:yyyy-MM-dd HH:mm} local / {s.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC"));
        c.TextRightAligned(ContentRight, y - 35, 8, false, PdfFonts.Sanitize(NonEmpty(s.MachineName)));
        c.TextRightAligned(ContentRight, y - 46, 8, false, PdfFonts.Sanitize($"Nexus v{NonEmpty(s.NexusVersion)}"));

        y = markBottomY - 8;
        Rule(c, y);
        y -= 20;
    }

    // ── Overall status strip ────────────────────────────────────────────────

    private static void DrawStatusStrip(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        var healthy = string.Equals(s.Health.Overall, HealthStatuses.Ok, StringComparison.Ordinal);
        var label = healthy ? "Overall: HEALTHY" : "Overall: ATTENTION NEEDED";

        c.FillGray(BlackGray);
        c.Text(ContentLeft, y, 12, true, PdfFonts.Sanitize(label));

        var counts = s.Health.Components
            .GroupBy(comp => comp.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        var summary = string.Format(CultureInfo.InvariantCulture,
            "{0} good - {1} watch - {2} attention - {3} unknown",
            counts.GetValueOrDefault(HealthStatuses.Ok),
            counts.GetValueOrDefault(HealthStatuses.Watch),
            counts.GetValueOrDefault(HealthStatuses.Act),
            counts.GetValueOrDefault(HealthStatuses.Unknown));

        c.FillGray(MidGray);
        c.Text(ContentLeft, y - 14, 8, false, PdfFonts.Sanitize(summary));

        y -= 34;
    }

    // ── Health summary (exactly 5 fixed rows) ───────────────────────────────

    private static void DrawHealthSummary(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        SectionTitle(c, "Health summary", ref y);

        var rows = new (string Label, string Kind, bool PlatformGated)[]
        {
            ("Storage", "storage", true),
            ("Memory", "memory", true),
            ("GPU", "gpu", true),
            ("Cooling", "cooling", false),
            ("System", "system", true),
        };

        const double labelX = ContentLeft;
        const double statusX = ContentLeft + 85;
        const double summaryX = ContentLeft + 165;
        var summaryMaxWidth = ContentRight - summaryX;

        foreach (var row in rows)
        {
            var (statusWord, summary) = SummarizeKind(s.Health, row.Kind, row.PlatformGated);

            c.FillGray(BlackGray);
            c.Text(labelX, y, 8, false, PdfFonts.Sanitize(row.Label));
            c.Text(statusX, y, 8, true, PdfFonts.Sanitize(statusWord));

            c.FillGray(MidGray);
            var sanitizedSummary = PdfFonts.Sanitize(summary);
            c.Text(summaryX, y, 8, false, PdfFonts.TruncateToWidth(sanitizedSummary, false, 8, summaryMaxWidth));

            y -= 15;
        }

        y -= 6;
        Rule(c, y);
        y -= 20;
    }

    private static (string StatusWord, string Summary) SummarizeKind(
        DiagnosticsHealthResponse health, string kind, bool platformGated)
    {
        if (platformGated && !health.Supported)
        {
            return ("UNKNOWN", NotAvailable);
        }

        var matches = health.Components.Where(comp => comp.Kind == kind).ToList();
        if (matches.Count == 0)
        {
            return ("GOOD", "No issues detected");
        }

        var worst = WorstOf(matches.Select(comp => comp.Status));
        var topReason = matches
            .SelectMany(comp => comp.Reasons)
            .OrderByDescending(r => SeverityRank(r.Severity))
            .FirstOrDefault();

        return (StatusWord(worst), topReason?.Summary ?? "No issues detected");
    }

    private static int SeverityRank(string status) => status switch
    {
        HealthStatuses.Act => 3,
        HealthStatuses.Watch => 2,
        HealthStatuses.Unknown => 1,
        _ => 0,
    };

    /// <summary>Per-kind roll-up, where "unknown" outranks ok deliberately: this
    /// row reports what is known about one kind, unlike the overall status.</summary>
    private static string WorstOf(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Contains(HealthStatuses.Act))
        {
            return HealthStatuses.Act;
        }
        if (list.Contains(HealthStatuses.Watch))
        {
            return HealthStatuses.Watch;
        }
        if (list.Contains(HealthStatuses.Unknown))
        {
            return HealthStatuses.Unknown;
        }
        return HealthStatuses.Ok;
    }

    private static string StatusWord(string healthStatus) => healthStatus switch
    {
        HealthStatuses.Ok => "GOOD",
        HealthStatuses.Watch => "WATCH",
        HealthStatuses.Act => "ATTENTION",
        _ => "UNKNOWN",
    };

    // ── System specifications (4 rows x 2 columns) ──────────────────────────

    private static void DrawSystemSpecs(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        SectionTitle(c, "System specifications", ref y);

        var memoryValue = NonEmpty(s.Specs.Memory);
        if (s.MemoryInfo.Supported && s.MemoryInfo.XmpLikelyActive == true && memoryValue != Placeholder)
        {
            // Before the module parenthetical, so ellipsis truncation drops
            // module detail rather than the XMP flag.
            var paren = memoryValue.IndexOf(" (", StringComparison.Ordinal);
            memoryValue = paren >= 0
                ? memoryValue[..paren] + ", XMP active" + memoryValue[paren..]
                : memoryValue + ", XMP active";
        }

        var gpuValue = NonEmpty(s.Specs.GraphicsCard);
        var driverVersion = s.Gpu.Supported ? s.Gpu.Gpus.FirstOrDefault()?.DriverVersion : null;
        if (gpuValue != Placeholder && !string.IsNullOrWhiteSpace(driverVersion))
        {
            gpuValue += $" (driver {driverVersion})";
        }

        var cells = new (string Label, string Value)[]
        {
            ("CPU", NonEmpty(s.Specs.Processor)), ("Motherboard", NonEmpty(s.Specs.Motherboard)),
            ("Memory", memoryValue), ("GPU", gpuValue),
            ("OS", NonEmpty(s.Specs.OsBuild)), ("Nexus version", NonEmpty(s.NexusVersion)),
            ("Machine name", NonEmpty(s.MachineName)), ("Report ID", NonEmpty(s.ReportId)),
        };

        const double col1X = ContentLeft;
        var col2X = ContentLeft + ContentWidth / 2 + 10;
        var colWidth = ContentWidth / 2 - 10;

        for (var row = 0; row < 4; row++)
        {
            DrawSpecCell(c, col1X, y, colWidth, cells[row * 2]);
            DrawSpecCell(c, col2X, y, colWidth, cells[row * 2 + 1]);
            y -= 17;
        }

        y -= 6;
        Rule(c, y);
        y -= 20;
    }

    private static void DrawSpecCell(PdfContentBuilder c, double x, double y, double maxWidth, (string Label, string Value) cell)
    {
        c.FillGray(MidGray);
        var label = PdfFonts.Sanitize(cell.Label + ":");
        c.Text(x, y, 8, false, label);

        var labelWidth = PdfFonts.MeasureWidthPt(label, false, 8);
        c.FillGray(BlackGray);
        var value = PdfFonts.Sanitize(cell.Value);
        c.Text(x + labelWidth + 4, y, 8, false, PdfFonts.TruncateToWidth(value, false, 8, maxWidth - labelWidth - 4));
    }

    // ── Storage (header + exactly 4 drive rows) ─────────────────────────────

    private static void DrawStorage(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        SectionTitle(c, "Storage", ref y);

        var columns = new (string Header, double X, double Width)[]
        {
            ("Model", ContentLeft, 190),
            ("Bus", ContentLeft + 190, 45),
            ("Capacity", ContentLeft + 235, 65),
            ("Temp", ContentLeft + 300, 45),
            ("Health", ContentLeft + 345, 50),
            ("Status", ContentLeft + 395, ContentRight - (ContentLeft + 395)),
        };

        c.FillGray(MidGray);
        foreach (var col in columns)
        {
            c.Text(col.X, y, 7, true, PdfFonts.Sanitize(col.Header));
        }
        y -= 12;
        Rule(c, y, RuleGray, 0.5);
        y -= 12;

        if (!s.Smart.Supported)
        {
            c.FillGray(MidGray);
            c.Text(ContentLeft, y, 8, false, PdfFonts.Sanitize(NotAvailable));
            y -= 14 * 3;
        }
        else
        {
            var drives = s.Smart.Drives;
            var overflow = drives.Count > MaxStorageRows;
            var shownCount = overflow ? MaxStorageRows - 1 : Math.Min(drives.Count, MaxStorageRows);

            for (var i = 0; i < shownCount; i++)
            {
                DrawDriveRow(c, columns, y, drives[i], s.IgnoredComponents.Contains(drives[i].Id));
                y -= 14;
            }

            if (overflow)
            {
                c.FillGray(MidGray);
                var more = drives.Count - shownCount;
                c.Text(ContentLeft, y, 8, false, PdfFonts.Sanitize($"+ {more} more drive(s) (see support bundle)"));
                y -= 14;
            }
            else
            {
                for (var i = shownCount; i < MaxStorageRows; i++)
                {
                    DrawBlankDriveRow(c, columns, y);
                    y -= 14;
                }
            }
        }

        y -= 6;
        Rule(c, y);
        y -= 20;
    }

    private static void DrawDriveRow(PdfContentBuilder c, (string Header, double X, double Width)[] columns, double y, SmartDriveInfo drive, bool ignored)
    {
        c.FillGray(BlackGray);
        var model = PdfFonts.Sanitize(NonEmpty(drive.Name));
        c.Text(columns[0].X, y, 8, false, PdfFonts.TruncateToWidth(model, false, 8, columns[0].Width));
        c.Text(columns[1].X, y, 8, false, PdfFonts.Sanitize(drive.Bus.ToUpperInvariant()));
        c.Text(columns[2].X, y, 8, false, PdfFonts.Sanitize(FormatBytesGb(drive.SizeBytes)));
        c.Text(columns[3].X, y, 8, false, PdfFonts.Sanitize(FormatTemperature(drive.TemperatureC)));
        c.Text(columns[4].X, y, 8, false, PdfFonts.Sanitize(FormatHealthPercent(drive.HealthPercent)));
        c.Text(columns[5].X, y, 8, true, PdfFonts.Sanitize(ignored ? "IGNORED" : StatusWord(MapDriveStatus(drive.Status))));
    }

    private static void DrawBlankDriveRow(PdfContentBuilder c, (string Header, double X, double Width)[] columns, double y)
    {
        c.FillGray(RuleGray);
        foreach (var col in columns)
        {
            c.Text(col.X, y, 8, false, Placeholder);
        }
    }

    private static string MapDriveStatus(string smartStatus) => smartStatus switch
    {
        "good" => HealthStatuses.Ok,
        "caution" => HealthStatuses.Watch,
        "warning" => HealthStatuses.Act,
        "bad" => HealthStatuses.Act,
        _ => HealthStatuses.Unknown,
    };

    private static string FormatBytesGb(ulong? bytes)
    {
        if (bytes is not { } b || b == 0)
        {
            return Placeholder;
        }
        var gb = b / 1024.0 / 1024.0 / 1024.0;
        return gb >= 1000
            ? $"{gb / 1024.0:0.#} TB"
            : $"{gb:0.#} GB";
    }

    private static string FormatTemperature(double? celsius) =>
        celsius is { } c ? $"{c:0}C" : Placeholder;

    private static string FormatHealthPercent(int? percent) =>
        percent is { } p ? $"{p}%" : Placeholder;

    // ── Recent stability (30 days): two-column counter grid ─────────────────

    private static void DrawStability(PdfContentBuilder c, ReportSnapshot s, ref double y)
    {
        SectionTitle(c, "Recent stability (30 days)", ref y);

        var counts = s.Counts30d;
        // EventLogMonitor/PnpProblemScanner are both Windows-only and report an
        // empty/zero snapshot on other platforms indistinguishably from "checked,
        // found none" - gate on Supported so that reads as unsupported, not zero.
        var eventsSupported = s.Health.Supported;
        var col1 = new (string Label, int? Value)[]
        {
            ("Bugchecks", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceBugcheck) : null),
            ("Unexpected shutdowns", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDirtyShutdown) : null),
            ("WHEA errors", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceWhea) : null),
            ("Disk errors", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDisk) : null),
        };
        var col2 = new (string Label, int? Value)[]
        {
            ("GPU TDRs", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr) : null),
            ("Live kernel events", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceLiveKernel) : null),
            ("App crashes", eventsSupported ? counts.GetValueOrDefault(DiagnosticEventCatalog.SourceAppCrash) : null),
            ("Device problems", s.Pnp.Supported ? s.Pnp.Devices.Count : null),
        };

        var col2X = ContentLeft + ContentWidth / 2 + 10;
        for (var row = 0; row < 4; row++)
        {
            DrawCounterCell(c, ContentLeft, y, col1[row]);
            DrawCounterCell(c, col2X, y, col2[row]);
            y -= 16;
        }

        y -= 4;
        c.FillGray(MidGray);
        c.Text(ContentLeft, y, 8, false, PdfFonts.Sanitize(FormatMemoryTestLine(s.LastMemoryTest)));
        y -= 20;
    }

    private static void DrawCounterCell(PdfContentBuilder c, double x, double y, (string Label, int? Value) cell)
    {
        c.FillGray(MidGray);
        var label = PdfFonts.Sanitize(cell.Label + ":");
        c.Text(x, y, 8, false, label);
        var labelWidth = PdfFonts.MeasureWidthPt(label, false, 8);

        c.FillGray(BlackGray);
        var hasValue = cell.Value is not null;
        var valueText = hasValue ? cell.Value!.Value.ToString(CultureInfo.InvariantCulture) : Placeholder;
        c.Text(x + labelWidth + 4, y, 8, hasValue, valueText);
    }

    private static string FormatMemoryTestLine(MemoryTestResult? result)
    {
        if (result is null)
        {
            return "Memory test: never run";
        }

        var word = result.Result switch
        {
            MemoryTestResult.Passed => "PASSED",
            MemoryTestResult.Failed => "FAILED",
            _ => "UNKNOWN",
        };
        return $"Memory test: {word} ({result.TimeUtc:yyyy-MM-dd})";
    }

    // ── Footer ───────────────────────────────────────────────────────────────

    private static void DrawFooter(PdfContentBuilder c, ReportSnapshot s)
    {
        const double footerY = Margin + 14;
        Rule(c, footerY + 12);

        c.FillGray(MidGray);
        c.Text(ContentLeft, footerY, 7, false,
            PdfFonts.Sanitize($"Generated by Nexus v{NonEmpty(s.NexusVersion)} - hellonexus.com"));
        c.TextRightAligned(ContentRight, footerY, 7, false,
            PdfFonts.Sanitize($"{s.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC"));
    }

    // ── Shared drawing helpers ───────────────────────────────────────────────

    private static void SectionTitle(PdfContentBuilder c, string title, ref double y)
    {
        c.FillGray(BlackGray);
        c.Text(ContentLeft, y, 9, true, PdfFonts.Sanitize(title));
        y -= 18;
    }

    private static void Rule(PdfContentBuilder c, double y, double gray = RuleGray, double widthPt = 0.5)
    {
        c.StrokeGray(gray);
        c.LineWidth(widthPt);
        c.Line(ContentLeft, y, ContentRight, y);
    }

    private static string NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? Placeholder : value.Trim();

    // ── PDF object assembly ──────────────────────────────────────────────────

    private static byte[] Assemble(PdfContentBuilder content)
    {
        var pdf = new PdfWriter();

        var pagesId = pdf.Reserve();
        var catalogId = pdf.Reserve();
        var fontRegularId = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var fontBoldId = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        var pageId = pdf.Reserve();
        var contentBytes = content.ToBytes();
        var contentId = pdf.Add($"<< /Length {contentBytes.Length} >>", contentBytes);

        var creationDate = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var infoId = pdf.Add($"<< /Producer (Nexus) /Title (Nexus Diagnostics Report) /CreationDate (D:{creationDate}Z) >>");

        pdf.Define(pagesId, $"<< /Type /Pages /Kids [{pageId} 0 R] /Count 1 >>");
        pdf.Define(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        pdf.Define(pageId,
            $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {PdfFonts.Num(PageWidth)} {PdfFonts.Num(PageHeight)}] " +
            $"/Resources << /Font << /{PdfFonts.Regular} {fontRegularId} 0 R /{PdfFonts.Bold} {fontBoldId} 0 R >> >> " +
            $"/Contents {contentId} 0 R >>");

        return pdf.Build(catalogId, infoId);
    }
}
