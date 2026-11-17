using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class IncidentGroupingTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DiagnosticIncident Incident(
        string id, DateTime timeUtc, string source, string title, string? appName = null, string severity = "warning") =>
        new()
        {
            Id = id,
            TimeUtc = timeUtc,
            Source = source,
            Severity = severity,
            Title = title,
            App = appName is null ? null : new DiagnosticAppInfo { Name = appName },
        };

    private static DiagnosticIncident KernelEvent(string id, DateTime timeUtc, string code) =>
        Incident(id, timeUtc, "liveKernel", "Live kernel event", severity: "critical") with
        {
            Data = new Dictionary<string, string> { ["p1"] = code },
        };

    [Fact]
    public void Live_kernel_events_without_p1_group_together()
    {
        var incidents = new List<DiagnosticIncident>
        {
            Incident("a", T0, "liveKernel", "Live kernel event", severity: "critical"),
            Incident("b", T0.AddMinutes(1), "liveKernel", "Live kernel event", severity: "critical"),
        };

        var result = Assert.Single(DiagnosticsHealthRoutes.GroupRepeats(incidents));
        Assert.Equal(2, result.RepeatCount);
    }

    [Fact]
    public void Non_live_kernel_incidents_ignore_p1()
    {
        var incidents = new List<DiagnosticIncident>
        {
            Incident("a", T0, "whea", "Hardware error") with { Data = new Dictionary<string, string> { ["p1"] = "1" } },
            Incident("b", T0.AddMinutes(1), "whea", "Hardware error") with { Data = new Dictionary<string, string> { ["p1"] = "2" } },
        };

        var result = Assert.Single(DiagnosticsHealthRoutes.GroupRepeats(incidents));
        Assert.Equal(2, result.RepeatCount);
    }

    [Fact]
    public void Live_kernel_events_group_per_code()
    {
        var incidents = new List<DiagnosticIncident>
        {
            KernelEvent("a", T0, "124"),
            KernelEvent("b", T0.AddMinutes(1), "124"),
            KernelEvent("c", T0.AddMinutes(2), "144"),
            KernelEvent("d", T0.AddMinutes(3), "117"),
        };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        Assert.Equal(3, grouped.Count);
        Assert.Equal(2, grouped.Single(i => i.Data["p1"] == "124").RepeatCount);
        Assert.Equal(1, grouped.Single(i => i.Data["p1"] == "144").RepeatCount);
        Assert.Equal(1, grouped.Single(i => i.Data["p1"] == "117").RepeatCount);
    }

    [Fact]
    public void Repeats_collapse_into_one_with_count_and_first_utc()
    {
        // Unsorted input (ascending) proves grouping does not depend on the
        // caller having already sorted newest-first.
        var incidents = Enumerable.Range(0, 300)
            .Select(i => Incident($"System/{i}", T0.AddMinutes(i), "liveKernel", "LiveKernelEvent: WATCHDOG"))
            .ToList();

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        var result = Assert.Single(grouped);
        Assert.Equal(300, result.RepeatCount);
        Assert.Equal(T0, result.FirstUtc);
        Assert.Equal(T0.AddMinutes(299), result.TimeUtc);
        Assert.Equal("System/299", result.Id);
    }

    [Fact]
    public void Distinct_titles_do_not_group()
    {
        var incidents = new List<DiagnosticIncident>
        {
            Incident("a", T0, "whea", "Hardware error event 3"),
            Incident("b", T0.AddMinutes(1), "whea", "Fatal hardware error"),
        };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, i => Assert.Equal(1, i.RepeatCount));
        Assert.All(grouped, i => Assert.Null(i.FirstUtc));
    }

    [Fact]
    public void Interleaved_sources_group_independently()
    {
        var incidents = new List<DiagnosticIncident>
        {
            Incident("w1", T0.AddMinutes(4), "whea", "Hardware error event 3"),
            Incident("t1", T0.AddMinutes(3), "tdr", "Display driver stopped responding"),
            Incident("w2", T0.AddMinutes(2), "whea", "Hardware error event 3"),
            Incident("t2", T0.AddMinutes(1), "tdr", "Display driver stopped responding"),
            Incident("w3", T0, "whea", "Hardware error event 3"),
        };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        Assert.Equal(2, grouped.Count);
        var whea = Assert.Single(grouped, i => i.Source == "whea");
        Assert.Equal(3, whea.RepeatCount);
        Assert.Equal("w1", whea.Id);
        Assert.Equal(T0, whea.FirstUtc);

        var tdr = Assert.Single(grouped, i => i.Source == "tdr");
        Assert.Equal(2, tdr.RepeatCount);
        Assert.Equal("t1", tdr.Id);
        Assert.Equal(T0.AddMinutes(1), tdr.FirstUtc);

        Assert.Equal(new[] { "whea", "tdr" }, grouped.Select(i => i.Source));
    }

    [Fact]
    public void Single_incidents_keep_repeat_count_one_and_null_first_utc()
    {
        var incidents = new List<DiagnosticIncident> { Incident("only", T0, "disk", "Bad block detected on disk") };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        var result = Assert.Single(grouped);
        Assert.Equal(1, result.RepeatCount);
        Assert.Null(result.FirstUtc);
    }

    [Fact]
    public void Same_source_and_title_but_different_app_name_does_not_group()
    {
        var incidents = new List<DiagnosticIncident>
        {
            Incident("a", T0, "appCrash", "Application crash", "game1.exe"),
            Incident("b", T0.AddMinutes(1), "appCrash", "Application crash", "game2.exe"),
        };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, i => Assert.Equal(1, i.RepeatCount));
    }

    [Fact]
    public void Same_title_different_severity_does_not_merge()
    {
        // whea's severity is level-driven, so the same title can occur at both
        // warning and critical; an older critical row must not collapse under
        // a newer warning representative (or vice versa).
        var incidents = new List<DiagnosticIncident>
        {
            Incident("a", T0.AddMinutes(2), "whea", "Corrected PCIe/MCE hardware error", severity: "warning"),
            Incident("b", T0.AddMinutes(1), "whea", "Corrected PCIe/MCE hardware error", severity: "critical"),
            Incident("c", T0, "whea", "Corrected PCIe/MCE hardware error", severity: "critical"),
        };

        var grouped = DiagnosticsHealthRoutes.GroupRepeats(incidents);

        Assert.Equal(2, grouped.Count);

        var warning = Assert.Single(grouped, i => i.Severity == "warning");
        Assert.Equal(1, warning.RepeatCount);
        Assert.Null(warning.FirstUtc);
        Assert.Equal("a", warning.Id);

        var critical = Assert.Single(grouped, i => i.Severity == "critical");
        Assert.Equal(2, critical.RepeatCount);
        Assert.Equal(T0, critical.FirstUtc);
        Assert.Equal("b", critical.Id);
    }
}
