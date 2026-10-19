using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

internal sealed class TestCooling : IFanControlProvider, ICurveProvider
{
    private readonly StubCoolingProvider _curves;

    public TestCooling(IConfigStore store)
    {
        _curves = new StubCoolingProvider(store);
    }

    public List<string> Released { get; } = new();
    public List<TemperatureSource> Sources { get; } = new()
    {
        new TemperatureSource { Id = "cpu-pkg", Name = "CPU Package", Category = "CPU" },
        new TemperatureSource { Id = "gpu-core", Name = "GPU Core", Category = "GPU" },
    };

    public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Sources;
    public float? ReadTemperature(string sensorId) => null;
    public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
    public void DriveFanSpeed(string channelId, int dutyPercent) { }
    public void ReleaseFan(string channelId) => Released.Add(channelId);
    public void ReleaseAll() { }
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    public void SetCurves(SetCurvesBody body) => _curves.SetCurves(body);
    public object? GetCalculatedById(string id) => null;
}

public class FlexRound2Tests
{
    private const string Tl = "6D1007F517E1";
    private const string Inf = "C5D212506CE1";

    private static (FlexHub Hub, InMemoryConfigStore Store, TestCooling Cooling, FlexFanProfileService Profiles) Create(bool both = false)
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("tl"));
        if (both)
        {
            discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        }
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl"
            ? new FakeFlexTransport(Tl)
            : new FakeFlexTransport(Inf, fansType: 0x2B), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        var cooling = new TestCooling(store);
        return (hub, store, cooling, new FlexFanProfileService(store, cooling, cooling, hub));
    }

    // ----- fan profiles -----

    [Fact]
    public void Rpm_converts_to_duty_by_family()
    {
        Assert.Equal(100, FlexFanProfiles.RpmToDuty(FlexFamily.TlFlex, 1900));
        Assert.Equal(19, FlexFanProfiles.RpmToDuty(FlexFamily.TlFlex, 365));
        Assert.Equal(10, FlexFanProfiles.RpmToDuty(FlexFamily.SlInfFlex, 365));
        Assert.Equal(40, FlexFanProfiles.RpmToDuty(FlexFamily.SlInfFlex, 860));
        Assert.Equal(100, FlexFanProfiles.RpmToDuty(FlexFamily.SlInfFlex, 3000));
    }

    [Fact]
    public void The_catalog_has_four_presets_and_four_custom_slots_for_each_size()
    {
        var catalog = FlexFanProfiles.Catalog();
        Assert.Equal(16, catalog.Count);
        Assert.Equal(new[] { 25, 45, 65, 80, 90 }, catalog.First(p => p.Key == "quiet" && p.FanSize == 120).Points.Select(x => x.Temp).ToArray());
        Assert.All(catalog.Where(p => p.Key == "fullsp"), p => Assert.All(p.Points, x => Assert.Equal(1900, x.Rpm)));
    }

    [Fact]
    public void A_preset_writes_one_managed_graph_curve_on_the_unit_channel()
    {
        var (_, store, _, profiles) = Create();
        Assert.Null(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "quiet", Source = "cpu" }));
        var curve = Assert.Single(store.Load().Cooling.Curves);
        Assert.Equal($"flex-{Tl}", curve.Id);
        Assert.Equal("Graph", curve.Type);
        Assert.Equal("cpu-pkg", curve.Input.Id);
        Assert.Equal($"lianli-flex:{Tl}:port0", Assert.Single(curve.Outputs).Id);
        Assert.Equal(5, curve.Graph!.Points.Count);
        Assert.Equal(19, curve.Graph.Points[0].Speed);
        Assert.Equal("quiet", store.Load().Devices.FlexUnits[Tl].FanProfile.Profile);
    }

    [Fact]
    public void Fixed_writes_a_flat_curve_and_start_stop_adds_a_stop_point()
    {
        var (_, store, _, profiles) = Create();
        Assert.Null(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "fixed", FixedPercent = 42 }));
        var flat = store.Load().Cooling.Curves.Single();
        Assert.Equal("Flat", flat.Type);
        Assert.Equal(42, flat.Flat!.Speed);
        Assert.Null(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "stdsp", StartStop = true }));
        var graph = store.Load().Cooling.Curves.Single();
        Assert.Equal(6, graph.Graph!.Points.Count);
        Assert.Equal(0, graph.Graph.Points[0].Speed);
    }

    [Fact]
    public void A_custom_slot_uses_its_own_points_and_the_default_before_any_are_saved()
    {
        var p = new LianLiFlexFanProfileSettings { Profile = "custom2" };
        Assert.Equal(5, FlexFanProfileService.EffectivePoints(p).Count);
        p.Custom["custom2"] = new List<LianLiFlexCurvePoint> { new() { Temp = 30, Rpm = 500 }, new() { Temp = 70, Rpm = 1500 } };
        Assert.Equal(2, FlexFanProfileService.EffectivePoints(p).Count);
    }

    [Fact]
    public void Mb_sync_removes_the_curve_and_releases_the_fan_and_restores_the_profile()
    {
        var (_, store, cooling, profiles) = Create();
        profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "highsp" });
        profiles.SetMbSync(true);
        Assert.Empty(store.Load().Cooling.Curves);
        Assert.Contains($"lianli-flex:{Tl}:port0", cooling.Released);
        Assert.Equal("mbsync", store.Load().Devices.FlexUnits[Tl].FanProfile.Profile);
        profiles.SetMbSync(false);
        Assert.Equal("highsp", store.Load().Devices.FlexUnits[Tl].FanProfile.Profile);
        Assert.Single(store.Load().Cooling.Curves);
    }

    [Fact]
    public void A_missing_source_or_unknown_profile_is_refused()
    {
        var (_, _, cooling, profiles) = Create();
        Assert.NotNull(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "quiet", Source = "water" }));
        Assert.NotNull(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "nope" }));
        Assert.NotNull(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "quiet", FanSize = 99 }));
        Assert.NotNull(profiles.Apply("000000000000", new LianLiFlexFanProfileSettings { Profile = "quiet" }));
        cooling.Sources.Add(new TemperatureSource { Id = "coolant", Name = "Liquid", Category = "Cooler" });
        Assert.Null(profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "quiet", Source = "water" }));
        Assert.True(profiles.SourcesAvailable().Water);
    }

    [Fact]
    public void Default_restores_the_shipped_standard_profile()
    {
        var (_, store, _, profiles) = Create();
        profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "fullsp", StartStop = true, Source = "gpu" });
        Assert.Null(profiles.Default(Tl));
        var p = store.Load().Devices.FlexUnits[Tl].FanProfile;
        Assert.Equal("stdsp", p.Profile);
        Assert.Equal("cpu", p.Source);
        Assert.False(p.StartStop);
    }

    [Fact]
    public void Profile_points_are_sanitized()
    {
        Assert.Null(FlexFanProfiles.Sanitize(new[] { (30, 500) }));
        var clean = FlexFanProfiles.Sanitize(new[] { (70, 9000), (30, -5), (30, 400) })!;
        Assert.Equal(2, clean.Count);
        Assert.Equal((30, 400), clean[0]);
        Assert.Equal((70, 2000), clean[1]);
    }

    // ----- Quick/Sync -----

    [Fact]
    public void Sync_writes_one_look_to_every_unit_in_order_with_slice_directions()
    {
        var (hub, store, _, _) = Create(both: true);
        var group = new FlexGroupLighting(hub, store);
        Assert.Null(group.Apply(new FlexSyncPatch
        {
            Enabled = true,
            Mode = "rainbow",
            Speed = 2,
            Brightness = 3,
            Members = new[] { (Inf, 1), (Tl, 0) },
        }));
        var units = store.Load().Devices.FlexUnits;
        Assert.Equal(0, units[Inf].SortIndex);
        Assert.Equal(1, units[Tl].SortIndex);
        Assert.Equal(1, units[Inf].MergeDirection);
        Assert.All(units.Values, u =>
        {
            Assert.True(u.Merge);
            Assert.Equal("rainbow", u.Mode);
            Assert.Equal(2, u.Speed);
            Assert.Equal(3, u.Brightness);
        });
        Assert.True(store.Load().Devices.FlexSyncEnabled);
        Assert.Equal(new[] { Inf, Tl }, group.Members().Select(m => m.Mac).ToArray());
        Assert.Null(group.Apply(new FlexSyncPatch { Enabled = false }));
        Assert.All(store.Load().Devices.FlexUnits.Values, u => Assert.False(u.Merge));
    }

    [Fact]
    public void Sync_refuses_bad_members_colors_and_unmergeable_effects()
    {
        var (hub, store, _, _) = Create();
        var group = new FlexGroupLighting(hub, store);
        Assert.NotNull(group.Apply(new FlexSyncPatch { Members = new[] { ("000000000000", 0) } }));
        Assert.NotNull(group.Apply(new FlexSyncPatch { Colors = new[] { "red" } }));
        Assert.NotNull(group.Apply(new FlexSyncPatch { Enabled = true, Mode = "staggered" }));
    }

    // ----- Apply To All -----

    [Fact]
    public void Apply_all_copies_the_look_within_the_family_and_skips_the_effects_the_other_family_lacks()
    {
        var (hub, store, _, profiles) = Create(both: true);
        store.Update(s => s.Devices.FlexUnits[Tl] = new LianLiFlexUnitSettings { Mode = "staggered", Speed = 1, UpDownSwap = true });
        var all = new FlexApplyAll(hub, store, profiles.Apply);
        var (family, _) = all.Apply(Tl, lighting: true, fan: false, "family");
        Assert.Empty(family!.Applied);
        var (cross, _) = all.Apply(Tl, lighting: true, fan: false, "all");
        Assert.Equal(new[] { Inf }, cross!.Skipped.ToArray());
        store.Update(s => s.Devices.FlexUnits[Tl] = new LianLiFlexUnitSettings { Mode = "meteor", Speed = 1, Colors = new() { "#112233" } });
        var (ok, _) = all.Apply(Tl, lighting: true, fan: false, "all");
        Assert.Equal(new[] { Inf }, ok!.Applied.ToArray());
        var copied = store.Load().Devices.FlexUnits[Inf];
        Assert.Equal("meteor", copied.Mode);
        Assert.Equal(1, copied.Speed);
        Assert.Equal("#112233", copied.Colors[0]);
    }

    [Fact]
    public void Apply_all_copies_the_fan_profile_and_validates_input()
    {
        var (hub, store, _, profiles) = Create(both: true);
        profiles.Apply(Tl, new LianLiFlexFanProfileSettings { Profile = "highsp" });
        var all = new FlexApplyAll(hub, store, profiles.Apply);
        var (result, _) = all.Apply(Tl, lighting: false, fan: true, "all");
        Assert.Equal(new[] { Inf }, result!.Applied.ToArray());
        Assert.Equal("highsp", store.Load().Devices.FlexUnits[Inf].FanProfile.Profile);
        Assert.NotNull(all.Apply("000000000000", true, false, "family").Error);
        Assert.NotNull(all.Apply(Tl, false, false, "family").Error);
        Assert.NotNull(all.Apply(Tl, true, false, "everything").Error);
    }

    // ----- Thermal Alert -----

    private sealed class Sink : IFlexAlertSink
    {
        public Dictionary<string, (byte R, byte G, byte B)> Last { get; private set; } = new();
        public void SetAlert(IReadOnlyDictionary<string, (byte R, byte G, byte B)> colors) => Last = new(colors);
    }

    private static (FlexThermalAlert Alert, Sink Sink, FlexClock Clock, InMemoryConfigStore Store) AlertSetup()
    {
        var (hub, store, _, _) = Create(both: true);
        store.Update(s => s.Devices.FlexThermalAlert = new LianLiFlexThermalAlertSettings { Enabled = true });
        var sink = new Sink();
        var clock = new FlexClock();
        var alert = new FlexThermalAlert(store, hub, sink, clock.Func);
        return (alert, sink, clock, store);
    }

    [Fact]
    public void Above_the_threshold_every_unit_gets_the_color_and_it_releases_two_degrees_below()
    {
        var (alert, sink, clock, _) = AlertSetup();
        alert.Tick(70, 50);
        Assert.Empty(sink.Last);
        clock.Now += 1000;
        alert.Tick(85, 50);
        Assert.Equal(2, sink.Last.Count);
        Assert.Equal(((byte)255, (byte)0, (byte)0), sink.Last[Tl]);
        Assert.Equal("cpu", alert.State.Source);
        clock.Now += 1000;
        alert.Tick(79, 50);
        Assert.Equal(2, sink.Last.Count);
        clock.Now += 1000;
        alert.Tick(77, 50);
        Assert.Empty(sink.Last);
        Assert.Null(alert.State.Source);
    }

    [Fact]
    public void The_most_recent_trigger_wins()
    {
        var (alert, sink, clock, _) = AlertSetup();
        alert.Tick(85, 50);
        clock.Now += 2000;
        alert.Tick(85, 90);
        Assert.Equal("gpu", alert.State.Source);
        Assert.Equal(((byte)0, (byte)0, (byte)255), sink.Last[Tl]);
        clock.Now += 2000;
        alert.Tick(85, 50);
        Assert.Equal("cpu", alert.State.Source);
    }

    [Fact]
    public void A_recent_settings_change_postpones_a_new_alert_and_members_limit_the_units()
    {
        var (alert, sink, clock, store) = AlertSetup();
        alert.Tick(50, 50);
        clock.Now += 1000;
        store.Update(s => s.Devices.FlexUnits = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits));
        alert.Tick(90, 50);
        Assert.Empty(sink.Last);
        clock.Now += FlexThermalAlert.StartGraceMs;
        store.Update(s => s.Devices.FlexThermalAlert = new LianLiFlexThermalAlertSettings { Enabled = true, Members = new() { Tl } });
        alert.Tick(90, 50);
        Assert.Equal(new[] { Tl }, sink.Last.Keys.ToArray());
    }

    [Fact]
    public void Disabled_or_missing_readings_never_alert()
    {
        var (alert, sink, clock, store) = AlertSetup();
        alert.Tick(null, null);
        Assert.Empty(sink.Last);
        store.Update(s => s.Devices.FlexThermalAlert = new LianLiFlexThermalAlertSettings { Enabled = false });
        clock.Now += 20_000;
        alert.Tick(110, 110);
        Assert.Empty(sink.Last);
    }

    [Fact]
    public void Thermal_requests_are_validated()
    {
        Assert.Null(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Cpu = new() { Threshold = 80, Color = "#FF0000" }, Unit = "f" }));
        Assert.NotNull(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Cpu = new() { Threshold = 0 } }));
        Assert.NotNull(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Gpu = new() { Threshold = 111 } }));
        Assert.NotNull(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Gpu = new() { Color = "blue" } }));
        Assert.NotNull(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Unit = "k" }));
        Assert.NotNull(LianLiFlexRoutes.ValidateThermal(new LianLiFlexThermalAlertPatch { Members = new[] { "xyz" } }));
    }

    [Fact]
    public void An_active_alert_streams_a_dimmed_flat_color_and_the_stored_look_returns_without_a_flash_save()
    {
        var tx = new FakeFlexTransport(Tl);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.FlexUnits[Tl] = new LianLiFlexUnitSettings { Mode = "rainbow" });
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func, runUpload: work => System.Threading.Tasks.Task.FromResult(work()));
        void Run(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                clock.Now += 100;
                writer.Tick();
                hub.Tick();
            }
        }
        Run(1000);
        Assert.Single(tx.Of(FlexProtocol.CmdEnable));
        Run(FlexHub.SaveSettleMs + 500);
        Assert.Single(tx.Of(FlexProtocol.CmdSave));
        writer.SetAlert(new Dictionary<string, (byte R, byte G, byte B)> { [Tl] = (255, 0, 0) });
        Run(600);
        var live = tx.Of(FlexProtocol.CmdLive).First();
        Assert.Equal(127, live[4]);
        Assert.Equal(0, live[5]);
        writer.SetAlert(new Dictionary<string, (byte R, byte G, byte B)>());
        Run(3000);
        Assert.Equal(2, tx.Of(FlexProtocol.CmdEnable).Count());
        Run(FlexHub.SaveSettleMs + 500);
        Assert.Single(tx.Of(FlexProtocol.CmdSave));
    }
}
