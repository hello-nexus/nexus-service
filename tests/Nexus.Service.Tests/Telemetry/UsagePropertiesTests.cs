using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Telemetry;
using Xunit;

public class UsagePropertiesTests
{
    private static PanelDeviceRecord Panel(string surface, params string[][] pages)
    {
        var layout = new PanelLayoutDto { Surface = surface };
        foreach (var page in pages)
        {
            layout.Pages.Add(new PanelPageDto
            {
                Widgets = page.Select(t => new PanelWidgetDto { Type = t }).ToList(),
            });
        }
        return new PanelDeviceRecord { Layout = layout };
    }

    private static object? Value(List<(string, object?)>? props, string key) =>
        props!.First(p => p.Item1 == key).Item2;

    private static bool Has(List<(string, object?)>? props, string key) =>
        props!.Any(p => p.Item1 == key);

    [Fact]
    public void Counts_widgets_pages_and_distinct_types_across_panels()
    {
        var s = new NexusSettings();
        s.PanelDevices["a"] = Panel("y70", ["clock", "media"], ["clock"]);
        s.PanelDevices["b"] = Panel("q60", ["app:com.hellonexus.aquarium"]);

        var props = SystemProfileService.BuildUsageProperties(s, lightingCount: 3, fanCount: 7);

        Assert.Equal(4, Value(props, "widget_count"));
        Assert.Equal(3, Value(props, "widget_page_count"));
        Assert.Equal(2, Value(props, "panel_devices"));
        // Distinct and sorted, so a reordered layout is not a profile change.
        Assert.Equal(new[] { "app:com.hellonexus.aquarium", "clock", "media" }, Value(props, "widget_types"));
        Assert.Equal(new[] { "q60", "y70" }, Value(props, "panel_surfaces"));
    }

    [Fact]
    public void Carries_device_counts_from_the_providers()
    {
        var props = SystemProfileService.BuildUsageProperties(new NexusSettings(), 5, 12);
        Assert.Equal(5, Value(props, "lighting_devices"));
        Assert.Equal(12, Value(props, "cooling_channels"));
    }

    [Fact]
    public void Omits_a_count_the_provider_could_not_supply()
    {
        // Person properties are last-write-wins, so a failed read must omit
        // the key rather than assert 0 over a previously good value.
        var props = SystemProfileService.BuildUsageProperties(new NexusSettings(), null, 12);
        Assert.False(Has(props, "lighting_devices"));
        Assert.Equal(12, Value(props, "cooling_channels"));
    }

    [Theory]
    [InlineData("clock", true)]
    [InlineData("media", true)]
    [InlineData("app:com.hellonexus.aquarium", true)]
    [InlineData("app:Not A Valid Id", false)]
    [InlineData("My Rig's Widget", false)]
    [InlineData("nicola@example.com", false)]
    [InlineData("C:\\Users\\nicola\\secret.txt", false)]
    public void Reports_only_slug_or_app_id_widget_types(string type, bool reportable)
    {
        // Type is preserved verbatim for unknown values and written from two
        // unvalidated sources, so the send boundary re-checks it.
        Assert.Equal(reportable, SystemProfileService.IsReportableWidgetType(type));
    }

    [Fact]
    public void Drops_an_unreportable_widget_type_but_still_counts_it()
    {
        var s = new NexusSettings();
        s.PanelDevices["a"] = Panel("y70", ["clock", "Some User Text"]);

        var props = SystemProfileService.BuildUsageProperties(s, 0, 0);
        Assert.Equal(2, Value(props, "widget_count"));
        Assert.Equal(new[] { "clock" }, Value(props, "widget_types"));
    }

    [Fact]
    public void Reports_only_the_pillars_that_are_off()
    {
        // Every flag defaults true, so the common install reports an empty list.
        var decided = new NexusSettings { FeaturesOnboardingCompleted = true };
        var defaults = SystemProfileService.BuildUsageProperties(decided, 0, 0);
        Assert.Empty((string[])Value(defaults, "features_off")!);

        // An undecided install behaves as lighting and cooling off, and reports it.
        var undecided = SystemProfileService.BuildUsageProperties(new NexusSettings(), 0, 0);
        Assert.Equal(new[] { "lighting", "cooling" }, Value(undecided, "features_off"));

        var s = new NexusSettings { FeaturesOnboardingCompleted = true };
        s.Features.Cooling = false;
        s.Features.Diagnostics = false;
        var props = SystemProfileService.BuildUsageProperties(s, 0, 0);
        Assert.Equal(new[] { "cooling", "diagnostics" }, Value(props, "features_off"));
    }

    [Fact]
    public void Carries_both_dashboard_modes()
    {
        var s = new NexusSettings();
        s.Ui.LightingDashboardMode = "advanced";
        s.Ui.CoolingDashboardMode = "simple";

        var props = SystemProfileService.BuildUsageProperties(s, 0, 0);
        Assert.Equal("advanced", Value(props, "lighting_mode"));
        Assert.Equal("simple", Value(props, "cooling_mode"));
    }

    [Fact]
    public void Skips_panels_that_have_no_layout_yet()
    {
        var s = new NexusSettings();
        s.PanelDevices["fresh"] = new PanelDeviceRecord();

        var props = SystemProfileService.BuildUsageProperties(s, 0, 0);
        Assert.Equal(0, Value(props, "widget_count"));
        Assert.Equal(1, Value(props, "panel_devices"));
        Assert.Empty((string[])Value(props, "widget_types")!);
    }

    [Fact]
    public void Emits_no_user_authored_text()
    {
        var s = new NexusSettings();
        var panel = Panel("y70", ["clock"]);
        panel.DisplayName = "Nicola's rig in the office";
        panel.Layout!.Pages[0].Label = "my secret page name";
        s.PanelDevices["a"] = panel;

        var flat = string.Join('|', SystemProfileService.BuildUsageProperties(s, 0, 0)!
            .Select(p => p.Item1 + "=" + (p.Item2 is string[] arr ? string.Join(',', arr) : p.Item2?.ToString())));

        Assert.DoesNotContain("Nicola", flat);
        Assert.DoesNotContain("secret", flat);
    }

    [Fact]
    public void Counts_paired_phones_and_those_seen_in_the_last_week()
    {
        var now = DateTimeOffset.UtcNow;
        long Ms(TimeSpan ago) => now.Subtract(ago).ToUnixTimeMilliseconds();
        var s = new NexusSettings();
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { LastSeenAt = Ms(TimeSpan.FromHours(1)) });
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { LastSeenAt = Ms(TimeSpan.FromDays(6)) });
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { LastSeenAt = Ms(TimeSpan.FromDays(8)) });
        // No LastSeenAt yet: falls back to CreatedAt.
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { CreatedAt = Ms(TimeSpan.FromDays(1)) });
        s.Auth!.PanelPhoneSessions.Add(new PanelPhoneSessionToken { CreatedAt = Ms(TimeSpan.FromDays(30)) });

        var props = SystemProfileService.BuildUsageProperties(s, 0, 0, now);

        Assert.Equal(5, Value(props, "paired_phones"));
        Assert.Equal(3, Value(props, "phones_active_7d"));
    }

    [Fact]
    public void Reports_zero_phones_and_relay_off_by_default()
    {
        var props = SystemProfileService.BuildUsageProperties(new NexusSettings(), 0, 0);
        Assert.Equal(0, Value(props, "paired_phones"));
        Assert.Equal(0, Value(props, "phones_active_7d"));
        Assert.Equal(false, Value(props, "relay_enabled"));
    }

    [Fact]
    public void Reports_the_relay_opt_in()
    {
        var s = new NexusSettings();
        s.Auth!.RelayEnabled = true;
        Assert.Equal(true, Value(SystemProfileService.BuildUsageProperties(s, 0, 0), "relay_enabled"));
    }

    [Fact]
    public async Task Phone_props_reach_the_usage_mirror_on_identify_and_layout_snapshot()
    {
        var phoneKeys = new[] { "paired_phones", "phones_active_7d", "relay_enabled" };
        var props = SystemProfileService.BuildUsageProperties(new NexusSettings(), 0, 0)!
            .Select(p => new KeyValuePair<string, object?>(p.Item1, p.Item2)).ToList();

        var sink = new UsageTelemetrySink(true);
        await sink.SendAsync("id", new[]
        {
            new TelemetryEvent { Id = "a", Name = "$identify", Timestamp = DateTimeOffset.UtcNow, Set = props },
            new TelemetryEvent { Id = "b", Name = TelemetryEvents.LayoutSnapshot, Timestamp = DateTimeOffset.UtcNow, Properties = props },
        }, default);

        var queued = sink.Take(10);
        Assert.Equal(new[] { TelemetryEvents.ProfileRefresh, TelemetryEvents.LayoutSnapshot }, queued.Select(e => e.Name));
        foreach (var e in queued)
        {
            foreach (var k in phoneKeys)
            {
                Assert.Contains(e.Props, p => p.Key == k);
            }
        }
    }
}
