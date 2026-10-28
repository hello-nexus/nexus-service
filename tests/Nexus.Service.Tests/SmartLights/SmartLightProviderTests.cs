using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Models.SmartLights;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

public class SmartLightProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonConfigStore _store;
    private readonly NetworkSendThrottle _throttle = new();
    private readonly FakeDriver _driver = new();
    private readonly SmartLightProvider _provider;
    private long _now = 1_000_000;

    public SmartLightProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-sl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _provider = new SmartLightProvider(new ILightDriver[] { _driver }, _store, _throttle,
            TimeSpan.FromMilliseconds(50), () => Interlocked.Read(ref _now));
        // Brands default OFF; enable the fake brand so the existing visibility/probe
        // assertions hold. The default-off behavior is covered by a dedicated test.
        _provider.SetBrandEnabled("fake", true);
    }

    [Fact]
    public void Owns_matchesBrandPrefix()
    {
        Assert.True(_provider.Owns("fake:bridge:1"));
        Assert.False(_provider.Owns("hue:bridge:1"));   // no hue driver registered here
        Assert.False(_provider.Owns("openrgb-0"));
        Assert.False(_provider.Owns(""));
    }

    [Fact]
    public void GetAll_emptyWhenNonePaired()
    {
        var all = _provider.GetAll();
        Assert.False(all.IsInit);
        Assert.Empty(all.Devices);
        Assert.False(_provider.IsConnected);
    }

    [Fact]
    public async Task Pair_persistsDevices_andSurfacesCard()
    {
        var res = await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal(1, res.Added);
        Assert.True(_provider.IsConnected);

        var all = _provider.GetAll();
        var card = Assert.Single(all.Devices);
        Assert.Equal("fake:bridge:1", card.Id);
        Assert.Equal("L1", card.Name);
        Assert.True(card.LedsOn);

        // Re-pair is idempotent (updates in place, adds 0).
        var again = await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal(0, again.Added);
        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task SetPower_off_marksCardOff_andRemove_clears()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        _provider.SetPower("fake:bridge:1", false);
        Assert.False(_provider.GetAll().Devices[0].LedsOn);

        _provider.SetPower("fake:bridge:1", true);
        Assert.True(_provider.GetAll().Devices[0].LedsOn);

        _provider.Remove("fake:bridge:1");
        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public void BuildFrames_buildsAveragingGrid()
    {
        // Seed config directly, then build frames.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var frames = _provider.BuildFrames(7);
        var frame = Assert.Single(frames);
        Assert.Equal(7, frame.Index);
        Assert.Equal("fake:bridge:1", frame.Id);
        Assert.Equal(16, frame.LedCount);
        Assert.NotNull(frame.LedU);
        Assert.NotNull(frame.LedV);
        Assert.Equal(16, frame.LedU!.Length);
    }

    [Fact]
    public void BuildFrames_usesDriverUvMap_whenPlanProvidesOne()
    {
        _driver.Plan = new LightFramePlan(2, AverageToSingle: false,
            LedU: new[] { 0.1f, 0.9f }, LedV: new[] { 0.2f, 0.8f });
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var frame = Assert.Single(_provider.BuildFrames(0));
        Assert.Equal(2, frame.LedCount);
        Assert.Equal(new[] { 0.1f, 0.9f }, frame.LedU);
        Assert.Equal(new[] { 0.2f, 0.8f }, frame.LedV);
    }

    [Fact]
    public async Task SubmitEffectFrame_attachesZones_forZonePlans()
    {
        _driver.Plan = new LightFramePlan(3, AverageToSingle: false);
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0); // captures the plan + populates the cache

        var tcs = new TaskCompletionSource<LightFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = f => tcs.TrySetResult(f);

        var leds = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        _provider.SubmitEffectFrame("fake:bridge:1", leds, 3, 0.5f);

        var sent = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sent.On);
        Assert.Equal(leds, sent.Zones);
        Assert.Equal((byte)85, sent.R); // channel averages across the 3 zones
        Assert.Equal((byte)85, sent.G);
        Assert.Equal((byte)85, sent.B);
        Assert.Equal(0.5f, sent.Brightness01);
    }

    [Fact]
    public async Task SubmitEffectFrame_noZones_forAveragingPlans()
    {
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0);

        var tcs = new TaskCompletionSource<LightFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = f => tcs.TrySetResult(f);

        _provider.SubmitEffectFrame("fake:bridge:1", new byte[] { 10, 20, 30 }, 1, 1f);

        var sent = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(sent.Zones);
        Assert.Equal((byte)10, sent.R);
        Assert.Equal((byte)20, sent.G);
        Assert.Equal((byte)30, sent.B);
    }

    [Fact]
    public async Task AccumulateOrSubmit_routesToRest_withUnscaledFrame_whenStreamerCannotStream()
    {
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0); // populates the cache AccumulateOrSubmit reads

        _driver.CanStreamResult = false;
        var accumulated = false;
        _driver.OnAccumulate = (_, _, _) => accumulated = true;
        var tcs = new TaskCompletionSource<LightFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = f => tcs.TrySetResult(f);

        _provider.AccumulateOrSubmit("fake:bridge:1", new LightFrame(On: true, 200, 100, 50, 0.5f));

        var sent = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(accumulated);
        // REST gets the frame as submitted - SendAsync applies Brightness01 itself.
        Assert.Equal((byte)200, sent.R);
        Assert.Equal((byte)100, sent.G);
        Assert.Equal((byte)50, sent.B);
        Assert.Equal(0.5f, sent.Brightness01);
    }

    [Fact]
    public void AccumulateOrSubmit_routesToAccumulate_withPreScaledRgb_whenStreamerCanStream()
    {
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        _provider.BuildFrames(0);

        _driver.CanStreamResult = true;
        (byte r, byte g, byte b)? accumulated = null;
        _driver.OnAccumulate = (r, g, b) => accumulated = (r, g, b);
        var sent = false;
        _driver.OnSend = _ => sent = true;

        _provider.AccumulateOrSubmit("fake:bridge:1", new LightFrame(On: true, 200, 100, 50, 0.5f));

        Assert.False(sent);
        Assert.NotNull(accumulated);
        Assert.Equal((byte)100, accumulated!.Value.r); // 200 * 0.5
        Assert.Equal((byte)50, accumulated.Value.g);    // 100 * 0.5
        Assert.Equal((byte)25, accumulated.Value.b);    // 50 * 0.5
    }

    [Fact]
    public async Task GetAll_hidesOfflineDevice_andRestoresOnReconnect()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.Single(_provider.GetAll().Devices);   // online -> shown

        await GoOfflineAsync();
        Assert.Empty(_provider.GetAll().Devices);     // offline -> card hidden

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.Single(_provider.GetAll().Devices);   // reconnect -> shown again
    }

    [Fact]
    public async Task OnlineProbe_raisesOnlineChanged_onlyOnTransition()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        var events = 0;
        _provider.OnlineChanged += () => Interlocked.Increment(ref events);

        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // absent->online: no transition
        await GoOfflineAsync();  // online->offline: +1
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // still offline: no event
        _driver.PingResult = true;
        await _provider.GetSmartLightDtosAsync(CancellationToken.None);  // offline->online: +1

        Assert.Equal(2, events);
    }

    [Fact]
    public async Task BuildFrames_stillBuildsOfflineDevice()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        await GoOfflineAsync();

        Assert.Empty(_provider.GetAll().Devices);   // card hidden
        Assert.Single(_provider.BuildFrames(0));      // engine keeps sending (the reconnect signal)
    }

    [Fact]
    public async Task SuccessfulSend_doesNotReviveOfflineCard()
    {
        // Regression: a fire-and-forget UDP send (Govee) always "succeeds" even
        // to a dead device, so the send path must not mark it online and undo
        // the probe's offline verdict.
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        _provider.BuildFrames(0); // populate the per-tick cache SubmitFrame needs

        await GoOfflineAsync(); // probes -> offline
        Assert.Empty(_provider.GetAll().Devices);

        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnSend = _ => sent.TrySetResult();
        _provider.SubmitFrame("fake:bridge:1", new LightFrame(On: true, 1, 2, 3, 1f, null));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(_provider.GetAll().Devices); // still hidden; the send didn't revive it
    }

    [Fact]
    public void GetStructures_exposesLinearStrip_forZoneAddressableLight()
    {
        _driver.Plan = new LightFramePlan(20, AverageToSingle: false);
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));

        var st = Assert.Single(_provider.GetStructures());
        Assert.Equal("fake:bridge:1", st.DeviceId);
        var seg = Assert.Single(st.Segments);
        Assert.Equal(20, seg.LedCount);
        Assert.Equal(20, seg.FrameLedCount);
        Assert.False(seg.Resizable);
        Assert.Equal("linear", seg.ZoneType);

        var zone = Assert.Single(st.DefaultZones);
        Assert.Equal("fake:bridge:1", zone.Id); // MUST equal the card id so RgbBridge matches the frame
        var slice = Assert.Single(zone.Slices);
        Assert.Equal(0, slice.Segment);
        Assert.Equal(0, slice.Start);
        Assert.Equal(20, slice.Count);
    }

    [Fact]
    public void GetStructures_seedsDriverUv_andSkipsSingleColorLamps()
    {
        // Single-color lamp (default averaging plan) has nothing to arrange.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = "1.2.3.4", StableKey = "bridge", Extra = "1",
        }));
        Assert.Empty(_provider.GetStructures());

        // A zone-addressable light's driver sweep seeds the segment defaults.
        _driver.Plan = new LightFramePlan(2, AverageToSingle: false,
            LedU: new[] { 0.1f, 0.9f }, LedV: new[] { 0.5f, 0.5f });
        var seg = Assert.Single(Assert.Single(_provider.GetStructures()).Segments);
        Assert.Equal(new[] { 0.1f, 0.9f }, seg.DefaultU);
        Assert.Equal(new[] { 0.5f, 0.5f }, seg.DefaultV);
    }

    [Fact]
    public async Task ReachabilityPolling_probesImmediately()
    {
        // A long interval: only an immediate first probe can satisfy the wait.
        var provider = new SmartLightProvider(new ILightDriver[] { _driver }, _store, _throttle,
            TimeSpan.FromHours(1), () => Interlocked.Read(ref _now));
        provider.SetBrandEnabled("fake", true);
        await provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.OnPing = () => probed.TrySetResult();

        provider.StartReachabilityPolling();
        try { await probed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { provider.StopReachabilityPolling(); }
    }

    [Fact]
    public async Task ScanBrand_keepsLight_whoseOfflineFlagIsStale()
    {
        await PairOneAsync();
        await GoOfflineAsync();
        Advance(3_600_000);                 // idle gap, then the light came back and streams
        await ProbeAsync();                 // page-open route probe misses: a fresh streak
        await _provider.ScanBrandAsync("fake", CancellationToken.None);

        Assert.Contains(_store.Load().SmartLights.Devices, d => d.Id == "fake:bridge:1");
    }

    private Task PairOneAsync() => _provider.PairAsync(
        new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

    private Task<GetSmartLightsResponse> ProbeAsync() => _provider.GetSmartLightDtosAsync(CancellationToken.None);

    private void Advance(long ms) => Interlocked.Add(ref _now, ms);

    // Two misses a full window apart: the earliest a light can go offline.
    private async Task GoOfflineAsync()
    {
        _driver.PingResult = false;
        await ProbeAsync();
        Advance(15_000);
        await ProbeAsync();
    }

    [Fact]
    public async Task OneMiss_keepsLightOnline()
    {
        await PairOneAsync();
        _driver.PingResult = true;
        await ProbeAsync();
        Advance(60_000);
        _driver.PingResult = false;
        await ProbeAsync();

        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task TwoMissesInsideTheWindow_keepLightOnline()
    {
        await PairOneAsync();
        var events = 0;
        _provider.OnlineChanged += () => Interlocked.Increment(ref events);
        _driver.PingResult = true;
        await ProbeAsync();
        _driver.PingResult = false;
        for (var i = 0; i < 5; i++)
        {
            Advance(2_000);
            await ProbeAsync();
        }

        Assert.Single(_provider.GetAll().Devices);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task TwoMissesAfterTheWindow_markOfflineWithOneEvent()
    {
        await PairOneAsync();
        var events = 0;
        _provider.OnlineChanged += () => Interlocked.Increment(ref events);
        _driver.PingResult = true;
        await ProbeAsync();
        _driver.PingResult = false;
        await ProbeAsync();                 // first miss: still online
        Assert.Single(_provider.GetAll().Devices);
        Advance(15_000);
        await ProbeAsync();                 // second miss, window elapsed since the first: offline
        await ProbeAsync();                 // already offline: no further event

        Assert.Empty(_provider.GetAll().Devices);
        Assert.Equal(1, events);
        Assert.False((await ProbeAsync()).Devices[0].Online);
    }

    [Fact]
    public async Task AnsweredProbe_restoresImmediately_andResetsTheMissCount()
    {
        await PairOneAsync();
        await GoOfflineAsync();
        Assert.Empty(_provider.GetAll().Devices);

        _driver.PingResult = true;
        await ProbeAsync();
        Assert.Single(_provider.GetAll().Devices);

        _driver.PingResult = false;
        await ProbeAsync();                 // one miss since the answer
        Assert.Single(_provider.GetAll().Devices);

        Advance(15_000);
        await ProbeAsync();                 // second miss, window elapsed
        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task CancelledProbe_recordsNoMiss()
    {
        await PairOneAsync();
        _driver.PingResult = true;
        await ProbeAsync();
        _driver.PingResult = false;
        Advance(60_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetSmartLightDtosAsync(cts.Token));

        Advance(15_000);
        await ProbeAsync();                 // the only recorded miss; a leaked one would flip the light
        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task ScanBrand_keepsOnlineLight_thatFailsItsScanPing()
    {
        await PairOneAsync();
        _driver.PingResult = true;
        await ProbeAsync();
        _driver.PingResult = false;
        await _provider.ScanBrandAsync("fake", CancellationToken.None);

        Assert.Contains(_store.Load().SmartLights.Devices, d => d.Id == "fake:bridge:1");
    }

    [Fact]
    public async Task ScanBrand_prunesOfflineLight_thatFailsItsScanPing()
    {
        await PairOneAsync();
        await GoOfflineAsync();
        await _provider.ScanBrandAsync("fake", CancellationToken.None);

        Assert.DoesNotContain(_store.Load().SmartLights.Devices, d => d.Id == "fake:bridge:1");
    }

    [Fact]
    public async Task PageOpenDoubleMiss_afterIdleGap_keepsLightOnline()
    {
        await PairOneAsync();
        _driver.PingResult = true;
        await ProbeAsync();
        Advance(600_000);
        _driver.PingResult = false;
        await ProbeAsync();                 // route refresh
        await ProbeAsync();                 // the loop's immediate probe

        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task SlowSpacedMisses_keepTheirStreak_andGoOffline()
    {
        await PairOneAsync();
        _driver.PingResult = false;
        await ProbeAsync();
        Advance(20_000);                    // beyond the offline window, inside the stale gap
        await ProbeAsync();

        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task MissesFromBeforeAGap_doNotCarryForward()
    {
        await PairOneAsync();
        _driver.PingResult = false;
        await ProbeAsync();                 // streak starts
        Advance(600_000);
        await ProbeAsync();                 // gap: fresh streak, one miss
        await ProbeAsync();                 // two misses, no span

        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task ProbeFinishingAfterRemove_doesNotResurrectState()
    {
        await PairOneAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.PingResult = false;
        _driver.OnPing = () => entered.TrySetResult();
        _driver.PingGate = release.Task;
        var probe = ProbeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _provider.Remove("fake:bridge:1");
        release.SetResult();
        await probe;
        _driver.PingGate = null;
        _driver.OnPing = null;

        // A resurrected streak would make this single miss the offline one.
        await PairOneAsync();
        Advance(15_000);
        await ProbeAsync();

        Assert.Single(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task PollLoop_recordsReachability()
    {
        await PairOneAsync();
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.OnlineChanged += () => offline.TrySetResult();
        _driver.PingResult = false;
        _driver.OnPing = () => Advance(15_000); // each polled probe lands a window later

        _provider.StartReachabilityPolling();
        try { await offline.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { _provider.StopReachabilityPolling(); }

        Assert.Empty(_provider.GetAll().Devices);
    }

    [Fact]
    public async Task CancelledScan_doesNotPrune()
    {
        await PairOneAsync();
        await GoOfflineAsync();             // prunable unless the scan is cancelled
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.ScanBrandAsync("fake", cts.Token));
        Assert.Contains(_store.Load().SmartLights.Devices, d => d.Id == "fake:bridge:1");
    }

    [Fact]
    public async Task Brand_defaultsOff_hidingAllItsLights()
    {
        // Fresh provider/store: no brand enabled, so a paired light stays hidden.
        var dir = Path.Combine(Path.GetTempPath(), "nexus-sl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var throttle = new NetworkSendThrottle();
        var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
        var provider = new SmartLightProvider(new ILightDriver[] { new FakeDriver() }, store, throttle);
        await provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        Assert.Empty(provider.GetAll().Devices);  // brand off by default
        Assert.Empty(provider.BuildFrames(0));
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BrandToggle_gatesVisibilityAndFrames()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        Assert.Single(_provider.GetAll().Devices);   // fake brand on (ctor)

        _provider.SetBrandEnabled("fake", false);
        Assert.Empty(_provider.GetAll().Devices);     // off -> hidden
        Assert.Empty(_provider.BuildFrames(0));

        _provider.SetBrandEnabled("fake", true);
        Assert.Single(_provider.GetAll().Devices);   // on -> shown
        Assert.Single(_provider.BuildFrames(0));
    }

    [Fact]
    public async Task GetSmartLightDtos_omitsBrandEnabledEntry_forAnUnregisteredDriver()
    {
        // A brand toggled on before its driver was unregistered (e.g. Nanoleaf)
        // stays in settings, but the response must not advertise it as a brand.
        _provider.SetBrandEnabled("nanoleaf", true);

        var resp = await _provider.GetSmartLightDtosAsync(CancellationToken.None);

        Assert.True(resp.BrandEnabled["fake"]);
        Assert.False(resp.BrandEnabled.ContainsKey("nanoleaf"));
        Assert.True(_store.Load().SmartLights.BrandEnabled["nanoleaf"]);   // persisted key untouched
    }

    [Fact]
    public void IsConnected_ignoresDevicesOfAnUnregisteredBrand()
    {
        // Reporting connected while GetAll returns nothing drives the lighting
        // routes' Scanning flag off a device no surface can show.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "nanoleaf:panel1", Brand = "nanoleaf", Name = "Nanoleaf Panel", Host = "5.6.7.8",
        }));

        Assert.False(_provider.IsConnected);
    }

    [Fact]
    public async Task UnregisteredBrandDevice_isOmittedFromEverySurface_butStaysInStore()
    {
        // A device paired under a brand whose driver has since been unregistered
        // (e.g. Nanoleaf) must not surface in the DTO list, GetAll, or a canvas
        // frame, while the persisted entry survives for a later re-registration.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "nanoleaf:panel1", Brand = "nanoleaf", Name = "Nanoleaf Panel", Host = "5.6.7.8",
        }));
        _provider.SetBrandEnabled("nanoleaf", true);

        var resp = await _provider.GetSmartLightDtosAsync(CancellationToken.None);
        Assert.DoesNotContain(resp.Devices, d => d.Id == "nanoleaf:panel1");
        Assert.DoesNotContain(_provider.BuildFrames(0), f => f.Id == "nanoleaf:panel1");
        Assert.DoesNotContain(_provider.GetAll().Devices, d => d.Id == "nanoleaf:panel1");
        Assert.Contains(_store.Load().SmartLights.Devices, d => d.Id == "nanoleaf:panel1");
    }

    [Fact]
    public async Task GetSmartLightDtos_doesNotProbeOrRaiseOnlineChanged_forUnregisteredBrand()
    {
        // ProbeReachabilityAsync must skip an orphaned brand entirely: a
        // driver-less probe would default it unreachable and fire OnlineChanged.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "nanoleaf:panel1", Brand = "nanoleaf", Name = "Nanoleaf Panel", Host = "5.6.7.8",
        }));
        _provider.SetBrandEnabled("nanoleaf", true);
        var raised = false;
        _provider.OnlineChanged += () => raised = true;

        await _provider.GetSmartLightDtosAsync(CancellationToken.None);

        Assert.False(raised);
    }

    [Fact]
    public async Task ScanBrand_rejectsUnregisteredBrand_withoutPruningItsDevices()
    {
        // Without the guard, ScanBrandAsync's driver-less probe defaults every
        // host of an orphaned brand to unreachable and prunes it from disk.
        _store.Update(s => s.SmartLights.Devices.Add(new SmartLightConfig
        {
            Id = "nanoleaf:panel1", Brand = "nanoleaf", Name = "Nanoleaf Panel", Host = "5.6.7.8",
        }));

        var result = await _provider.ScanBrandAsync("nanoleaf", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("unknown-brand", result.Error);
        Assert.Contains(_store.Load().SmartLights.Devices, d => d.Id == "nanoleaf:panel1");
    }

    [Fact]
    public void SetDisabled_leavesAnOrphanedBrandsDisabledState_untouched()
    {
        // A device paired under a brand whose driver has since been unregistered
        // (e.g. Nanoleaf) keeps whatever disabled flag it already had; SetDisabled
        // only owns ids Owns() claims.
        _store.Update(s =>
        {
            s.SmartLights.Devices.Add(new SmartLightConfig
            {
                Id = "nanoleaf:panel1", Brand = "nanoleaf", Name = "Nanoleaf Panel", Host = "5.6.7.8",
            });
            s.Devices.DisabledLightingDevices = new List<string> { "nanoleaf:panel1" };
        });

        _provider.SetDisabled(Array.Empty<string>());

        Assert.Contains("nanoleaf:panel1", _store.Load().Devices.DisabledLightingDevices);
    }

    [Fact]
    public async Task ScanBrand_prunesUnreachableLight_butKeepsItsMapping()
    {
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);
        // Stand in a saved LED mapping for the light.
        _store.Update(s => s.Devices.DeviceLedOverrides["fake:bridge:1"] =
            new List<SegmentLedOverride> { new() { Segment = 0, LedIndex = 0, U = 0.5f, V = 0.5f } });

        await GoOfflineAsync(); // unreachable -> the light was removed
        await _provider.ScanBrandAsync("fake", CancellationToken.None);

        Assert.DoesNotContain(_store.Load().SmartLights.Devices, d => d.Id == "fake:bridge:1");
        // Mapping survives so a re-add restores it.
        Assert.True(_store.Load().Devices.DeviceLedOverrides.ContainsKey("fake:bridge:1"));
    }

    [Fact]
    public async Task ScanBrand_keepsReachableLight_evenWhenDiscoveryFindsNothing()
    {
        // The Govee case: multicast discovery returns nothing over WiFi, but the
        // light is reachable by IP, so the scan must NOT prune it.
        await _provider.PairAsync(
            new PairSmartLightBody { Brand = "fake", Host = "1.2.3.4", StableKey = "bridge" }, CancellationToken.None);

        _driver.PingResult = true;       // reachable
        _driver.DiscoverResult.Clear();  // but discovery sees nothing
        await _provider.ScanBrandAsync("fake", CancellationToken.None);

        Assert.Single(_store.Load().SmartLights.Devices); // kept
    }

    public void Dispose()
    {
        _provider.StopReachabilityPolling();
        _throttle.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Also an ISessionStreamer (like Hue) so AccumulateOrSubmit's routing is
    // testable; CanStreamResult defaults false so every pre-existing test here
    // (which assumes the REST/OnSend path) keeps behaving unchanged.
    private sealed class FakeDriver : ILightDriver, ISessionStreamer
    {
        public LightFramePlan Plan = new(16, true);
        public Action<LightFrame>? OnSend;
        public Action<byte, byte, byte>? OnAccumulate;
        public bool CanStreamResult;
        public bool PingResult = true;
        public Action? OnPing;
        public Task? PingGate;
        // What a scan discovers; defaults to the one paired light's host/key.
        public List<DiscoveredLight> DiscoverResult = new() { new DiscoveredLight("fake", "1.2.3.4", "Fake", "bridge") };

        public string Brand => "fake";
        public Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiscoveredLight>>(DiscoverResult);
        public Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
            => Task.FromResult(new PairResult
            {
                Ok = true,
                Devices = new List<SmartLightConfig>
                {
                    new() { Id = "fake:bridge:1", Brand = "fake", Name = "L1", Host = target.Host, StableKey = "bridge", Token = "t", Extra = "1" },
                },
            });
        public LightFramePlan PlanFrames(SmartLight dev) => Plan;
        public Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
        { OnSend?.Invoke(frame); return Task.CompletedTask; }
        public Task IdentifyAsync(SmartLight dev, CancellationToken ct) => Task.CompletedTask;
        public int MinIntervalMs(SmartLight dev) => 10;
        public string RateLimitKey(SmartLight dev) => dev.Id;
        public async Task<bool> PingAsync(SmartLight dev, CancellationToken ct)
        {
            // Like the real drivers: cancellation reads as "not reachable", never throws.
            if (ct.IsCancellationRequested) return false;
            var result = PingResult;
            OnPing?.Invoke();
            if (PingGate is { } gate) await gate;
            return result;
        }

        public bool CanStream(SmartLight dev) => CanStreamResult;
        public void Accumulate(SmartLight dev, byte r, byte g, byte b) => OnAccumulate?.Invoke(r, g, b);
        public void Flush() { }
        public void StopAll() { }
    }
}
