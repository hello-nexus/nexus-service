using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Report;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

public class DiagnosticsReportBuilderTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ReportSnapshot BuildSnapshot(int driveCount, string? driveNameOverride = null)
    {
        var drives = new List<SmartDriveInfo>();
        for (var i = 0; i < driveCount; i++)
        {
            drives.Add(new SmartDriveInfo
            {
                Id = $"storage:drive{i}",
                Name = driveNameOverride ?? $"Test NVMe SSD {i}",
                Bus = "nvme",
                SizeBytes = 1_000_000_000_000UL,
                TemperatureC = 40 + i,
                HealthPercent = 100,
                Status = "good",
            });
        }

        var smart = new SmartSnapshot { Supported = true, Drives = drives };
        var health = DiagnosticsHealthModel.Compute(
            smart: smart,
            cooling: new CoolingStallSnapshot(true, Array.Empty<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: new PnpProblemSnapshot(true, Array.Empty<PnpProblemDevice>()),
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: true,
            generatedAtUtc: T0);

        return new ReportSnapshot
        {
            GeneratedAtUtc = T0,
            MachineName = "TEST-PC",
            NexusVersion = "3.1.0",
            ReportId = "abcd1234",
            Health = health,
            Specs = new SystemSpecsResponse
            {
                PcName = "TEST-PC",
                OsBuild = "Windows 11 Pro (10.0.26100)",
                Processor = "Test CPU 9800X3D",
                Motherboard = "Test Motherboard X870E",
                Memory = "32 GB DDR5-6000 (2 x 16 GB Corsair)",
                GraphicsCard = "Test GPU RTX 5080",
            },
            MemoryInfo = new MemoryInfoSnapshot(true, Array.Empty<MemoryModuleInfo>(), true),
            Gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
            {
                new("Test GPU RTX 5080", "566.36", 55, 220, new GpuThrottleInfo(Array.Empty<string>(), null, null, null, null)),
            }),
            Smart = smart,
            Pnp = new PnpProblemSnapshot(true, Array.Empty<PnpProblemDevice>()),
            Counts30d = new Dictionary<string, int>
            {
                [DiagnosticEventCatalog.SourceDirtyShutdown] = 1,
            },
            LastMemoryTest = null,
        };
    }

    [Fact]
    public void Build_ProducesStructurallyValidPdf()
    {
        var pdf = DiagnosticsReportBuilder.Build(BuildSnapshot(1));
        PdfTestSupport.AssertValidStructure(pdf);
    }

    [Fact]
    public void Build_ContentStream_ContainsExpectedText()
    {
        var snapshot = BuildSnapshot(1) with { MachineName = "MY-TEST-HOST" };
        var pdf = DiagnosticsReportBuilder.Build(snapshot);
        var content = PdfTestSupport.ExtractContentStream(pdf);

        Assert.Contains("Diagnostics Report", content);
        Assert.Contains("MY-TEST-HOST", content);
        Assert.Contains("Storage", content);
    }

    [Fact]
    public void Build_MarksAnIgnoredDrive_InsteadOfItsSmartStatus()
    {
        var snapshot = BuildSnapshot(2) with { IgnoredComponents = new HashSet<string> { "storage:drive1" } };
        var content = PdfTestSupport.ExtractContentStream(DiagnosticsReportBuilder.Build(snapshot));

        Assert.Contains("IGNORED", content);
        Assert.DoesNotContain("IGNORED", PdfTestSupport.ExtractContentStream(DiagnosticsReportBuilder.Build(BuildSnapshot(2))));
    }

    [Fact]
    public void Build_LayoutIsIdentical_RegardlessOfDriveCount()
    {
        var oneDrivePdf = DiagnosticsReportBuilder.Build(BuildSnapshot(1));
        var eightDrivesPdf = DiagnosticsReportBuilder.Build(BuildSnapshot(8));

        Assert.Equal(PdfTestSupport.ObjectCount(oneDrivePdf), PdfTestSupport.ObjectCount(eightDrivesPdf));

        var oneContent = PdfTestSupport.ExtractContentStream(oneDrivePdf);
        var eightContent = PdfTestSupport.ExtractContentStream(eightDrivesPdf);

        // The next section always starts at the same Y regardless of drive
        // count - proof the storage table's fixed row budget never reflows.
        Assert.Equal(
            ExtractYBeforeMarker(oneContent, "Recent stability"),
            ExtractYBeforeMarker(eightContent, "Recent stability"));

        // Same number of distinct row lines within the storage table frame
        // (header row + the 4 fixed data-row slots), whether those slots hold
        // real drives, blank placeholders, or the overflow line.
        Assert.Equal(
            CountDistinctRowYPositions(oneContent, "Storage", "Recent stability"),
            CountDistinctRowYPositions(eightContent, "Storage", "Recent stability"));

        Assert.DoesNotContain("more drive", oneContent, StringComparison.Ordinal);
        // Parens in the literal PDF string are backslash-escaped by PdfFonts.Escape.
        Assert.Contains("+ 5 more drive\\(s\\)", eightContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DriveNameWithEmojiAndKanji_DoesNotCorruptPdf()
    {
        var snapshot = BuildSnapshot(1, driveNameOverride: "\U0001F525 サムスン 990 PRO");
        var pdf = DiagnosticsReportBuilder.Build(snapshot);

        PdfTestSupport.AssertValidStructure(pdf);
        var content = PdfTestSupport.ExtractContentStream(pdf);
        foreach (var ch in content)
        {
            Assert.True(ch <= 0x7E, $"non-ASCII byte leaked into content stream: 0x{(int)ch:X}");
        }
    }

    [Fact]
    public void Build_NonWindowsSnapshot_RendersNotAvailablePlaceholders()
    {
        var health = DiagnosticsHealthModel.Compute(
            smart: new SmartSnapshot { Supported = false, Drives = Array.Empty<SmartDriveInfo>() },
            cooling: new CoolingStallSnapshot(true, Array.Empty<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        var snapshot = new ReportSnapshot
        {
            GeneratedAtUtc = T0,
            MachineName = "MAC-TEST",
            NexusVersion = "3.1.0",
            ReportId = "deadbeef",
            Health = health,
            Specs = new SystemSpecsResponse { PcName = "MAC-TEST", Processor = "Apple M-series" },
            MemoryInfo = MemoryInfoSnapshot.Unsupported,
            Gpu = GpuHealthSnapshot.Unsupported,
            Smart = new SmartSnapshot { Supported = false, Drives = Array.Empty<SmartDriveInfo>() },
            Pnp = PnpProblemSnapshot.Unsupported,
            Counts30d = new Dictionary<string, int>(),
            LastMemoryTest = null,
        };

        var pdf = DiagnosticsReportBuilder.Build(snapshot);
        PdfTestSupport.AssertValidStructure(pdf);
        var content = PdfTestSupport.ExtractContentStream(pdf);
        Assert.Contains("Not available on this platform", content);
        Assert.Contains("Memory test: never run", content);

        // Unsupported event/pnp sources render "-", never a misleading "0".
        Assert.Equal("-", PdfTestSupport.ExtractNextLiteralAfter(content, "(Bugchecks:) Tj"));
        Assert.Equal("-", PdfTestSupport.ExtractNextLiteralAfter(content, "(WHEA errors:) Tj"));
        Assert.Equal("-", PdfTestSupport.ExtractNextLiteralAfter(content, "(GPU TDRs:) Tj"));
        Assert.Equal("-", PdfTestSupport.ExtractNextLiteralAfter(content, "(Device problems:) Tj"));
    }

    [Fact]
    public void Build_WindowsSnapshot_StabilityCountersShowRealZero()
    {
        var pdf = DiagnosticsReportBuilder.Build(BuildSnapshot(1));
        var content = PdfTestSupport.ExtractContentStream(pdf);

        // Supported platform with a real (counted, not gated) zero.
        Assert.Equal("0", PdfTestSupport.ExtractNextLiteralAfter(content, "(Bugchecks:) Tj"));
        Assert.Equal("0", PdfTestSupport.ExtractNextLiteralAfter(content, "(Device problems:) Tj"));
    }

    private static double ExtractYBeforeMarker(string content, string markerText)
    {
        var markerIdx = content.IndexOf("(" + markerText, StringComparison.Ordinal);
        Assert.True(markerIdx >= 0, $"marker not found: {markerText}");

        var tdIdx = content.LastIndexOf(" Td (", markerIdx + 1, StringComparison.Ordinal);
        Assert.True(tdIdx >= 0, $"Td operator not found before marker: {markerText}");

        var beforeTd = content.Substring(0, tdIdx);
        var match = Regex.Match(beforeTd, @"(-?[0-9.]+)\s+(-?[0-9.]+)\s*$");
        Assert.True(match.Success, "could not parse Td operands");
        return double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    private static int CountDistinctRowYPositions(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf("(" + startMarker + ")", StringComparison.Ordinal);
        Assert.True(start >= 0, $"section start marker not found: {startMarker}");
        var end = content.IndexOf("(" + endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"section end marker not found: {endMarker}");

        var section = content.Substring(start, end - start);
        var matches = Regex.Matches(section, @"(-?[0-9.]+) (-?[0-9.]+) Td");
        return matches.Select(m => m.Groups[2].Value).Distinct().Count();
    }
}
