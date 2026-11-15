using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Cooling;
using Nexus.Service.Migration;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Migration;

/// <summary>Safety rules of the Nexus 2 fan curve import.</summary>
public class Nexus2FanCurveSafetyTests
{
    private static readonly (double, double)[] SafePoints = { (0, 30), (50, 50), (80, 80), (100, 100) };
    private static readonly string Port1 = $"minihub:{Hw.MiniHub}:port1";
    private static readonly string Port2 = $"minihub:{Hw.MiniHub}:port2";
    private static readonly string Fan0 = $"smarthub:{Hw.SmartHub}:fan0";
    private static readonly string Fan1 = $"smarthub:{Hw.SmartHub}:fan1";
    private static readonly string Fan2 = $"smarthub:{Hw.SmartHub}:fan2";

    private static Nexus2FanCurvePlan Plan(Hw hw, params JsonObject[] curves) =>
        Nexus2FanCurvePlanner.Build(N2.Single(100, curves), hw.Env());

    private static Nexus2PlannedCurve Only(Nexus2FanCurvePlan plan) => Assert.Single(plan.Curves);

    private static string? TargetReason(Hw hw, JsonObject output, bool flat = false)
    {
        var curve = Only(flat
            ? Plan(hw, N2.Flat("T", 60, new[] { output }))
            : Plan(hw, N2.Graph("T", SafePoints, new[] { output })));
        return Assert.Single(curve.Targets).Reason;
    }

    // ── Shared-mode hubs ──

    [Fact]
    public void Q_series_and_portal_targets_are_unsupported_until_verified_on_hardware()
    {
        var hw = Hw.Y70().AddNp50().AddQSeries();
        hw.AllowHubs = false;
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Out("NP50Fan", "Legacy", Hw.Np50)));
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Out("NP50 Channel1Fan2", "Fan", Hw.Np50)));
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Out("Q60 Channel1Fan2", "Fan", Hw.QSeries)));
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Out("Q60 Channel2Fan1/1", "Duo", Hw.QSeries)));
    }

    [Fact]
    public void A_mini_hub_port_alone_is_unsupported_while_its_sibling_is_not_software_driven()
    {
        var hw = Hw.Y70();
        var curve = Only(Plan(hw, N2.Flat("F", 60, new[] { N2.Mini(0), N2.Pwm(1) })));
        Assert.Equal("unsupportedTarget", curve.Targets[0].Reason);
        Assert.True(curve.Import);
        Assert.Equal(Fan0, Assert.Single(curve.Document!.Outputs).Id);
    }

    [Fact]
    public void Both_mini_hub_ports_import_together()
    {
        var curve = Only(Plan(Hw.Y70(), N2.Flat("F", 60, new[] { N2.Mini(0), N2.Mini(1) })));
        Assert.All(curve.Targets, t => Assert.True(t.Import));
    }

    [Fact]
    public void A_sibling_already_driven_by_a_curve_or_a_manual_duty_lets_a_port_import()
    {
        var byManual = Hw.Y70();
        byManual.ManualDriven.Add(Port2);
        Assert.Null(TargetReason(byManual, N2.Mini(0)));

        var byCurve = Hw.Y70();
        byCurve.Curves.Add(new CurveDocument { Id = "user", Type = "Flat", Flat = new FlatCurveData { Speed = 50 }, Outputs = { new CurveOutputDocument { Id = Port2, Type = "Fan" } } });
        Assert.Null(TargetReason(byCurve, N2.Mini(0)));
    }

    [Fact]
    public void An_uncontrolled_sibling_blocks_a_mini_hub_port_because_the_write_still_reaches_it()
    {
        var hw = Hw.Y70();
        hw.Uncontrolled.Add(Port2);
        Assert.Equal("unsupportedTarget", TargetReason(hw, N2.Mini(0)));
    }

    [Fact]
    public void With_an_uncontrolled_sibling_an_import_leaves_the_mini_hub_untouched()
    {
        var hw = Hw.Y70();
        hw.Uncontrolled.Add(Port2);
        var cooling = UserSettings(hw, "custom");
        hw.Curves.AddRange(cooling.Curves);
        var before = WriteRecorder.Tick(cooling, hw.Channels);
        Nexus2FanCurveApplier.Apply(cooling, Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Mini(0), N2.Pwm(1) }))));
        var after = WriteRecorder.Tick(cooling, hw.Channels);
        Assert.Empty(after.WritesTo(Port1));
        Assert.Empty(after.WritesTo(Port2));
        Assert.Equal(before.DeviceMode, after.DeviceMode);
    }

    [Fact]
    public void The_service_reads_the_stores_pin_overrides_when_it_plans()
    {
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json");
        store.Update(s => s.Cooling.FanPinOverrides[Fan0] = true);
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;

        service.ApplyFanCurves(root);

        var outputs = store.Load().Cooling.Curves.SelectMany(c => c.Outputs.Select(o => o.Id)).ToList();
        Assert.DoesNotContain(Fan0, outputs);
    }

    [Fact]
    public void A_plan_made_before_a_sibling_became_uncontrolled_is_replanned_not_patched()
    {
        var (staleService, staleStore, _) = Stale();
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;

        var result = staleService.ApplyFanCurves(root);

        Assert.Equal("applied", result.Status);
        var outputs = staleStore.Load().Cooling.Curves.SelectMany(c => c.Outputs.Select(o => o.Id)).ToList();
        Assert.DoesNotContain(outputs, o => o.StartsWith("minihub:", StringComparison.Ordinal));
        Assert.Contains(Fan0, outputs);
    }

    private static (Nexus2MigrationService Service, InMemoryConfigStore Store, Hw Hw) Stale()
    {
        InMemoryConfigStore? holder = null;
        var built = FanCurveServices.Service("n2-217-final.json", configureFans: fans =>
            fans.OnFirstSourceRead = () => holder!.Update(s => s.Cooling.UncontrolledFanChannels.Add(Port2)));
        holder = built.Store;
        return built;
    }

    [Fact]
    public void Two_curves_that_each_hold_one_port_import_only_when_both_do()
    {
        var hw = Hw.Y70();
        var both = Plan(hw, N2.Flat("p1", 60, new[] { N2.Mini(0) }), N2.Flat("p2", 60, new[] { N2.Mini(1) }));
        Assert.All(both.Curves, c => Assert.True(c.Import));

        var split = Plan(hw, N2.Flat("p1", 60, new[] { N2.Mini(0) }), N2.Graph("p2", SafePoints, new[] { N2.Mini(1) }, input: N2.Input("/intelcpu/0/temperature/99")));
        Assert.All(split.Curves, c => Assert.False(c.Import));
        Assert.Equal("unsupportedTarget", split.Curves[0].Targets[0].Reason);
    }

    [Theory]
    [InlineData(1, 3, 0, true)]
    [InlineData(1, 3, 1, true)]
    [InlineData(1, 1, 1, true)]
    [InlineData(null, 3, 0, false)]
    [InlineData(0, 3, 0, false)]
    [InlineData(2, 3, 0, false)]
    [InlineData(1, null, 1, false)]
    [InlineData(1, 0, 1, false)]
    [InlineData(1, 4, 1, false)]
    public void A_mini_hub_digit_maps_only_when_the_stored_layout_can_have_produced_it(int? fanPort1, int? fanPort2, int digit, bool maps)
    {
        var hw = Hw.Y70();
        hw.ManualDriven.Add(digit == 0 ? Port2 : Port1);
        var root = N2.RootWith(N2.MiniLayout(fanPort1, fanPort2), N2.Profile("P", true, 100, false, N2.Flat("F", 60, new[] { N2.Mini(digit) })));
        var target = Assert.Single(Only(Nexus2FanCurvePlanner.Build(root, hw.Env())).Targets);
        Assert.Equal(maps, target.Import);
        Assert.Equal(maps ? null : "unsupportedTarget", target.Reason);
    }

    [Fact]
    public void A_file_without_a_mini_hub_layout_cannot_prove_the_digit()
    {
        var hw = Hw.Y70();
        hw.ManualDriven.Add(Port2);
        var root = N2.RootWith(null, N2.Profile("P", true, 100, false, N2.Flat("F", 60, new[] { N2.Mini(0) })));
        Assert.Equal("unsupportedTarget", Assert.Single(Only(Nexus2FanCurvePlanner.Build(root, hw.Env())).Targets).Reason);
    }

    // ── Hardware writes ──

    private static CoolingSettings UserSettings(Hw hw, string mode)
    {
        var cooling = new CoolingSettings { ActivePreset = mode };
        cooling.Curves.Add(new CurveDocument
        {
            Id = "user-gpu", Name = "GPU mine", Type = "Flat", Flat = new FlatCurveData { Speed = 55 },
            Outputs = { new CurveOutputDocument { Id = "/gpu-nvidia/0/control/1", Type = "Fan" } },
        });
        cooling.Curves.Add(new CurveDocument
        {
            Id = "user-hub", Name = "Hub mine", Type = "Flat", Flat = new FlatCurveData { Speed = 45 },
            Outputs = { new CurveOutputDocument { Id = Fan2, Type = "Fan" } },
        });
        return cooling;
    }

    private static List<CurveDocument> Imported(Hw hw, JsonElement root) =>
        Nexus2FanCurvePlanner.Build(root, hw.Env()).Curves.Where(c => c.Import).Select(c => c.Document!).ToList();

    [Theory]
    [InlineData("off")]
    [InlineData("custom")]
    public void An_import_writes_nothing_to_a_channel_or_device_it_did_not_claim(string mode)
    {
        var hw = Hw.Y70();
        var cooling = UserSettings(hw, mode);
        hw.Curves.AddRange(cooling.Curves);
        var before = WriteRecorder.Tick(cooling, hw.Channels);

        // The first Mini Hub port alone would flip the hub to software and re-send the second port's duty.
        var root = N2.Single(100, N2.Flat("B", 60, new[] { N2.Mini(0), N2.Pwm(1) }));
        var imported = Imported(hw, root);
        Nexus2FanCurveApplier.Apply(cooling, imported);
        var after = WriteRecorder.Tick(cooling, hw.Channels);

        var claimed = imported.SelectMany(c => c.Outputs.Select(o => o.Id)).ToHashSet();
        Assert.Equal(new[] { Fan0 }, claimed.ToArray());
        foreach (var channel in hw.Channels.Where(c => !claimed.Contains(c.Id)))
        {
            Assert.Equal(before.WritesTo(channel.Id), after.WritesTo(channel.Id));
        }
        Assert.Equal(before.DeviceMode, after.DeviceMode);
    }

    [Fact]
    public void The_recorder_does_see_a_single_port_write_when_the_port_is_forced_in()
    {
        var hw = Hw.Y70();
        var cooling = UserSettings(hw, "custom");
        cooling.Curves.Add(new CurveDocument { Id = "n2-forced", Type = "Flat", Flat = new FlatCurveData { Speed = 60 }, Outputs = { new CurveOutputDocument { Id = Port1, Type = "Fan" } } });
        var rec = WriteRecorder.Tick(cooling, hw.Channels);
        Assert.Equal("software", rec.DeviceMode[$"minihub:{Hw.MiniHub}"]);
        Assert.Equal(new[] { 0 }, rec.WritesTo(Port2));
    }

    [Fact]
    public void Both_mini_hub_ports_together_write_only_those_two_channels_and_their_device()
    {
        var hw = Hw.Y70();
        var cooling = UserSettings(hw, "custom");
        hw.Curves.AddRange(cooling.Curves);
        var before = WriteRecorder.Tick(cooling, hw.Channels);
        var imported = Imported(hw, N2.Single(100, N2.Flat("B", 60, new[] { N2.Mini(0), N2.Mini(1) })));
        Nexus2FanCurveApplier.Apply(cooling, imported);
        var after = WriteRecorder.Tick(cooling, hw.Channels);

        foreach (var channel in hw.Channels.Where(c => c.Id != Port1 && c.Id != Port2))
        {
            Assert.Equal(before.WritesTo(channel.Id), after.WritesTo(channel.Id));
        }
        Assert.Equal(60, after.WritesTo(Port1)[^1]);
        Assert.Equal(60, after.WritesTo(Port2)[^1]);
        Assert.Equal("software", after.DeviceMode[$"minihub:{Hw.MiniHub}"]);
    }

    // ── Cooler-owned, followed and guard-owned channels ──

    [Fact]
    public void A_channel_on_a_cooler_is_a_valid_target_like_any_fan()
    {
        var hw = Hw.Y70();
        hw.Channels.First(c => c.Id == Fan0).IsAio = true;
        Assert.Null(TargetReason(hw, N2.Pwm(1)));
    }

    [Fact]
    public void A_channel_another_curve_syncs_to_is_skipped_as_followed()
    {
        var hw = Hw.Y70();
        hw.Curves.Add(new CurveDocument { Id = "mirror", Type = "Sync", Sync = new SyncCurveData { SourceChannelId = Fan0 }, Outputs = { new CurveOutputDocument { Id = Fan1, Type = "Fan" } } });
        Assert.Equal("followedChannel", TargetReason(hw, N2.Pwm(1)));
        Assert.Null(TargetReason(hw, N2.Pwm(3)));
    }

    [Fact]
    public void A_channel_the_guard_curve_or_a_heal_owns_is_skipped()
    {
        var hw = Hw.Y70();
        hw.Curves.Add(new CurveDocument { Id = CoolingConfigLint.GuardCurveId, Type = "Graph", Outputs = { new CurveOutputDocument { Id = Fan1, Type = "Fan" } } });
        hw.HealedIds.Add(Fan2);
        Assert.Equal("guardHealed", TargetReason(hw, N2.Pwm(2)));
        Assert.Equal("guardHealed", TargetReason(hw, N2.Pwm(3)));
        Assert.Null(TargetReason(hw, N2.Pwm(1)));
    }

    // ── Feature gate ──

    [Fact]
    public void With_cooling_switched_off_nothing_is_offered()
    {
        var hw = Hw.Y70();
        hw.CoolingEnabled = false;
        var plan = Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }));
        Assert.Equal("coolingDisabled", plan.Note);
        Assert.Empty(plan.Curves);
    }

    [Fact]
    public void With_cooling_switched_off_the_service_reports_unavailable_writes_nothing_and_never_makes_the_offer_pending()
    {
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json");
        store.Update(s => s.Features.Cooling = false);
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;

        var cat = service.BuildFanCurvesCategory(root);
        Assert.False(cat.Available);
        Assert.Equal("coolingDisabled", cat.FanCurvesNote);
        Assert.Null(cat.FanCurves);

        var result = service.ApplyFanCurves(root);
        Assert.Equal(("skipped", "coolingDisabled"), (result.Status, result.Detail));
        Assert.Empty(store.Load().Cooling.Curves);
        Assert.False(service.HasImportableFanCurves());
    }

    // ── Response time ──

    [Theory]
    [InlineData(61.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(60.0, true)]
    [InlineData(0.0, true)]
    public void A_graph_response_time_outside_what_nexus_2_accepts_means_it_never_ran(double responseTime, bool imports)
    {
        var curve = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        curve["graph"]!["responseTime"] = responseTime;
        var planned = Only(Plan(Hw.Y70(), curve));
        Assert.Equal(imports, planned.Import);
        Assert.Equal(imports ? null : "badPoints", planned.Reason);
    }

    [Fact]
    public void A_linear_response_time_outside_the_range_and_a_non_numeric_one_are_badPoints()
    {
        var late = N2.Linear("L", 30, 75, 45, 100, new[] { N2.Pwm(1) });
        late["linear"]!["responseTime"] = 61;
        Assert.Equal("badPoints", Only(Plan(Hw.Y70(), late)).Reason);

        var text = N2.Graph("G", SafePoints, new[] { N2.Pwm(1) });
        text["graph"]!["responseTime"] = "fast";
        Assert.Equal("badPoints", Only(Plan(Hw.Y70(), text)).Reason);
    }

    // ── Flat needs the guard behind it ──

    [Fact]
    public void A_flat_curve_never_drives_a_gpu_fan_whatever_its_role_nor_a_board_fan_given_a_gpu_role()
    {
        var hw = Hw.Y70();
        hw.FanRoles["/gpu-nvidia/0/control/0"] = FanRoleKind.Cpu;
        Assert.Equal("gpuFlat", TargetReason(hw, N2.Gpu(1), flat: true));

        hw.FanRoles[Fan0] = FanRoleKind.Gpu;
        Assert.Equal("gpuFlat", TargetReason(hw, N2.Pwm(1), flat: true));
    }

    // ── Sensor, floors and the NaN-safe checks ──

    [Theory]
    [InlineData(14.9f, false)]
    [InlineData(15f, true)]
    public void The_plausible_sensor_band_starts_above_the_bogus_board_reading(float reading, bool imports)
    {
        var hw = Hw.Y70();
        hw.Sources.First(s => s.Id == "/intelcpu/0/temperature/14").Value = reading;
        var curve = Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) })));
        Assert.Equal(imports, curve.Import);
        Assert.Equal(imports ? null : "sensorImplausible", curve.Reason);
    }

    [Fact]
    public void A_nan_global_modifier_from_nexus_3_fails_the_floors_instead_of_passing_them()
    {
        var hw = Hw.Y70();
        hw.NexusGlobal = double.NaN;
        Assert.Equal("unsafeCurve", Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Reason);
        Assert.Equal("unsafeCurve", Only(Plan(hw, N2.Flat("F", 80, new[] { N2.Pwm(1) }))).Reason);
    }

    [Theory]
    [InlineData(96.7, true, 30)]
    [InlineData(94.6, false, 0)]
    public void The_flat_floor_is_judged_on_the_stored_whole_percent(double global, bool imports, int stored)
    {
        var root = N2.Root(N2.Profile("P", true, global, false, N2.Flat("F", 31, new[] { N2.Pwm(1) })));
        var curve = Only(Nexus2FanCurvePlanner.Build(root, Hw.Y70().Env()));
        Assert.Equal(imports, curve.Import);
        if (imports)
        {
            Assert.Equal(stored, curve.Document!.Flat!.Speed);
        }
    }

    // ── Ids and names ──

    [Fact]
    public void A_curve_id_never_lands_on_an_existing_kept_copy()
    {
        var hw = Hw.Y70();
        hw.Curves.Add(new CurveDocument { Id = "n2-x-kept", Type = "Flat", Flat = new FlatCurveData { Speed = 50 } });
        var planned = Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }, id: "x-kept")));
        Assert.Equal("n2-x-kept-2", planned.Document!.Id);
    }

    [Fact]
    public void An_unnamed_curve_is_named_by_its_nexus_2_id_not_by_english_text()
    {
        var planned = Only(Plan(Hw.Y70(), N2.Graph("", SafePoints, new[] { N2.Pwm(1) }, id: "d8f4-aa")));
        Assert.Equal("d8f4-aa", planned.Name);
    }

    [Fact]
    public void A_flat_curve_needs_the_thermal_guard_running_but_a_graph_does_not()
    {
        var hw = Hw.Y70();
        hw.GuardEnabled = false;
        Assert.Equal("unsafeCurve", Only(Plan(hw, N2.Flat("F", 80, new[] { N2.Pwm(1) }))).Reason);
        Assert.True(Only(Plan(hw, N2.Graph("G", SafePoints, new[] { N2.Pwm(1) }))).Import);
    }

    [Fact]
    public void A_fan_role_changed_while_planning_makes_the_plan_stale()
    {
        var config = N2.Single(100, N2.Flat("F", 80, new[] { N2.Pwm(1) })).GetRawText();
        InMemoryConfigStore? holder = null;
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json", configText: config,
            configureFans: fans => fans.OnFirstSourceRead = () => holder!.Update(s => s.Cooling.FanRoles[Fan0] = FanRoleKind.Gpu));
        holder = store;

        var result = service.ApplyFanCurves(JsonDocument.Parse(config).RootElement);

        Assert.Equal("skipped", result.Status);
        Assert.DoesNotContain(store.Load().Cooling.Curves, c => c.Id.StartsWith("n2-", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_curve_edited_while_planning_makes_the_plan_stale()
    {
        InMemoryConfigStore? holder = null;
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json",
            configureFans: fans => fans.OnFirstSourceRead = () => holder!.Update(s => s.Cooling.Curves[0].Flat!.Speed = 99));
        holder = store;
        store.Update(s => s.Cooling.Curves.Add(FlatCurve("user", 50, "/unrelated")));
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;

        var result = service.ApplyFanCurves(root);

        Assert.Equal("applied", result.Status);
        // The write came from a plan made after the edit, so the edit survived it.
        Assert.Equal(99, store.Load().Cooling.Curves.Single(c => c.Id == "user").Flat!.Speed);
    }

    // ── Applier ──

    [Fact]
    public void Leftovers_from_two_source_documents_never_share_a_curve()
    {
        var oldKept = FlatCurve("n2-a-kept", 50, Fan1);
        var newer = FlatCurve("n2-a", 70, Fan0, Fan2);
        var composed = Nexus2FanCurveApplier.Compose(new List<CurveDocument> { oldKept, newer }, new[] { FlatCurve("n2-z", 90, Fan0) });

        Assert.Equal(composed.Count, composed.Select(c => c.Id).Distinct().Count());
        var onFan1 = composed.Single(c => c.Outputs.Any(o => o.Id == Fan1));
        var onFan2 = composed.Single(c => c.Outputs.Any(o => o.Id == Fan2));
        Assert.Equal(("n2-a-kept", 50), (onFan1.Id, onFan1.Flat!.Speed));
        Assert.Equal(70, onFan2.Flat!.Speed);
        Assert.NotEqual(onFan1.Id, onFan2.Id);
    }

    [Fact]
    public void Three_imports_in_a_row_keep_every_channel_on_the_document_it_last_ran()
    {
        var cooling = new CoolingSettings();
        Nexus2FanCurveApplier.Apply(cooling, new[] { FlatCurve("n2-a", 50, Fan0, Fan1) });
        Nexus2FanCurveApplier.Apply(cooling, new[] { FlatCurve("n2-a", 70, Fan0, Fan2) });
        Nexus2FanCurveApplier.Apply(cooling, new[] { FlatCurve("n2-z", 90, Fan0) });

        int SpeedOn(string channel) => cooling.Curves.Single(c => c.Outputs.Any(o => o.Id == channel)).Flat!.Speed;
        Assert.Equal(90, SpeedOn(Fan0));
        Assert.Equal(50, SpeedOn(Fan1));
        Assert.Equal(70, SpeedOn(Fan2));
        Assert.Equal(cooling.Curves.Count, cooling.Curves.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void No_two_curves_share_an_id_when_a_retained_import_meets_a_taken_suffix()
    {
        var mix = new CurveDocument { Id = "user-mix", Type = "Mixed", Mixed = new MixedCurveData { CurveIds = { "n2-a" } } };
        var current = new List<CurveDocument> { FlatCurve("n2-a", 50, Fan0), mix, FlatCurve("n2-a-2", 40, Fan1) };
        var composed = Nexus2FanCurveApplier.Compose(current, new[] { FlatCurve("n2-a", 60, Fan0) });
        Assert.Equal(composed.Count, composed.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void No_two_curves_share_an_id_when_a_kept_copy_meets_an_imported_id()
    {
        var current = new List<CurveDocument> { FlatCurve("n2-b", 50, Fan0, Fan1) };
        var composed = Nexus2FanCurveApplier.Compose(current, new[] { FlatCurve("n2-b-kept", 60, Fan0) });
        Assert.Equal(composed.Count, composed.Select(c => c.Id).Distinct().Count());
        Assert.Equal(60, composed.Single(c => c.Outputs.Any(o => o.Id == Fan0)).Flat!.Speed);
        Assert.Equal(50, composed.Single(c => c.Outputs.Any(o => o.Id == Fan1)).Flat!.Speed);
    }

    [Fact]
    public void No_two_curves_share_an_id_when_an_import_arrives_before_the_kept_copy_in_any_order()
    {
        var current = new List<CurveDocument> { FlatCurve("n2-b", 50, Fan0, Fan1), FlatCurve("n2-b-kept", 45, Fan2) };
        var composed = Nexus2FanCurveApplier.Compose(current, new[] { FlatCurve("n2-b", 60, Fan0), FlatCurve("n2-b-kept", 70, Fan1) });
        Assert.Equal(composed.Count, composed.Select(c => c.Id).Distinct().Count());
    }

    private static CurveDocument FlatCurve(string id, int speed, params string[] outputs) => new()
    {
        Id = id, Name = id, Type = "Flat", Flat = new FlatCurveData { Speed = speed },
        Outputs = outputs.Select(o => new CurveOutputDocument { Id = o, Type = "Fan" }).ToList(),
    };

    [Fact]
    public void An_imported_curve_a_mixed_curve_references_keeps_its_id_and_document()
    {
        var mix = new CurveDocument { Id = "user-mix", Type = "Mixed", Mixed = new MixedCurveData { CurveIds = { "n2-a" } } };
        var current = new List<CurveDocument> { FlatCurve("n2-a", 50, Fan0, Fan1), mix };
        var composed = Nexus2FanCurveApplier.Compose(current, new[] { FlatCurve("n2-a", 60, Fan0) });

        var kept = composed.Single(c => c.Id == "n2-a");
        Assert.Equal(50, kept.Flat!.Speed);
        Assert.Equal(new[] { Fan1 }, kept.Outputs.Select(o => o.Id).ToArray());
        var replacement = composed.Single(c => c.Id != "n2-a" && c.Id.StartsWith("n2-a", StringComparison.Ordinal));
        Assert.Equal(60, replacement.Flat!.Speed);
        Assert.Equal(new[] { Fan0 }, replacement.Outputs.Select(o => o.Id).ToArray());
    }

    [Fact]
    public void A_referenced_import_is_never_deleted_even_when_the_re_import_claims_all_its_channels()
    {
        var mix = new CurveDocument { Id = "user-mix", Type = "Mixed", Mixed = new MixedCurveData { CurveIds = { "n2-a" } } };
        var composed = Nexus2FanCurveApplier.Compose(new List<CurveDocument> { FlatCurve("n2-a", 50, Fan0), mix }, new[] { FlatCurve("n2-a", 60, Fan0) });
        Assert.Contains(composed, c => c.Id == "n2-a" && c.Flat!.Speed == 50);
    }

    // ── Service ──

    [Fact]
    public void The_curves_and_the_preset_label_land_in_one_store_update()
    {
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json");
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;
        var updates = 0;
        store.OnChanged += () => updates++;

        var result = service.ApplyFanCurves(root);

        Assert.Equal("applied", result.Status);
        Assert.Equal(1, updates);
        Assert.Equal("custom", store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public void The_cached_importable_answer_yields_to_the_cooling_gate()
    {
        var reader = new CountingReader("n2-217-final.json");
        var (service, store, _) = FanCurveServices.Service("n2-217-final.json", reader);
        Assert.True(service.HasImportableFanCurves());
        store.Update(s => s.Features.Cooling = false);
        Assert.False(service.HasImportableFanCurves());
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void The_importable_check_is_cached_briefly_and_dropped_by_an_apply()
    {
        var reader = new CountingReader("n2-217-final.json");
        var (service, _, _) = FanCurveServices.Service("n2-217-final.json", reader);
        Assert.True(service.HasImportableFanCurves());
        Assert.True(service.HasImportableFanCurves());
        Assert.Equal(1, reader.Reads);

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling", "n2-217-final.json"))).RootElement;
        service.ApplyFanCurves(root);
        service.HasImportableFanCurves();
        Assert.Equal(2, reader.Reads);
    }

    // ── Fixtures ──

    private static readonly System.Text.RegularExpressions.Regex FakeSerial = new(@"^20[0-9A-F]{2}([A-F])\1{3}000[1-3]\d?$");
    private static readonly System.Text.RegularExpressions.Regex Serial = new(@"^[0-9A-F]{12,14}$");
    private static readonly System.Text.RegularExpressions.Regex PnpInstance = new(@"#\d&([0-9a-f]+)&\d&[0-9a-f]+#");
    private const string FakePnpInstance = "fa4e0000";
    private const string FakeTimestamp = "2026-01-01T00:00:00.000Z";
    private const string FakeStreamingScale = "{\"width\":1000,\"height\":520}";
    private static readonly System.Text.RegularExpressions.Regex IsoTimestamp = new(@"^\d{4}-\d\d-\d\dT");

    private static IEnumerable<string> Strings(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                yield return el.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    foreach (var s in Strings(item))
                    {
                        yield return s;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    yield return prop.Name;
                    foreach (var s in Strings(prop.Value))
                    {
                        yield return s;
                    }
                }
                break;
        }
    }

    private static IEnumerable<double> Numbers(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                yield return el.GetDouble();
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    foreach (var n in Numbers(item))
                    {
                        yield return n;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    foreach (var n in Numbers(prop.Value))
                    {
                        yield return n;
                    }
                }
                break;
        }
    }

    [Fact]
    public void Every_fixture_carries_only_fake_machine_values()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "cooling");
        var files = Directory.GetFiles(dir);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(text);
            Assert.False(doc.RootElement.TryGetProperty("windowBounds", out _), file);
            Assert.False(doc.RootElement.TryGetProperty("last-apk-install", out _), file);
            if (doc.RootElement.TryGetProperty("q60-assets-modified", out var stamp))
            {
                Assert.Equal(FakeTimestamp, stamp.GetString());
            }
            Assert.Equal(FakeStreamingScale, doc.RootElement.GetProperty("lighting").GetProperty("streamingScale").GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", ""));
            Assert.DoesNotContain(Numbers(doc.RootElement), n => n >= 100_000_000_000d);
            foreach (var value in Strings(doc.RootElement))
            {
                if (IsoTimestamp.IsMatch(value))
                {
                    Assert.Equal(FakeTimestamp, value);
                }
                if (Serial.IsMatch(value))
                {
                    Assert.Matches(FakeSerial, value);
                }
                foreach (System.Text.RegularExpressions.Match m in PnpInstance.Matches(value))
                {
                    Assert.Equal(FakePnpInstance, m.Groups[1].Value);
                }
            }
        }
    }

    [Fact]
    public void The_fake_serial_pattern_accepts_the_test_machine_and_rejects_a_real_looking_one()
    {
        Assert.Matches(FakeSerial, Hw.SmartHub);
        Assert.Matches(FakeSerial, Hw.MiniHub + "0");
        Assert.DoesNotMatch(FakeSerial, "20AB12CD34EF");
    }
}
