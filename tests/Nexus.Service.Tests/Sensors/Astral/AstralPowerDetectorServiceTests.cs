using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Power;
using Nexus.Service.Sensors;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralPowerDetectorServiceTests
{
    // Only the pin currents and GetGpus matter here; the rest are never called.
    private sealed class PinSensors : ISensorProvider
    {
        public List<GpuPinCurrents> Cards { get; } = new();
        public List<GpuPinCurrents>? FoundOnGpuRead { get; set; }
        public int GpuReads { get; private set; }

        public IReadOnlyList<GpuPinCurrents> ReadGpuPinCurrents() => Cards.ToList();

        public IReadOnlyList<GpuReadout> GetGpus()
        {
            GpuReads++;
            if (FoundOnGpuRead is { } found)
            {
                Cards.AddRange(found);
                FoundOnGpuRead = null;
            }
            return Array.Empty<GpuReadout>();
        }

        public string GetCpuModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => throw new NotSupportedException();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => throw new NotSupportedException();
        public IReadOnlyList<string> GetGpuModels() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => throw new NotSupportedException();
        public string GetMemoryTotalFormatted() => throw new NotSupportedException();
        public string GetRamBrandModel() => throw new NotSupportedException();
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => throw new NotSupportedException();
        public IReadOnlyList<string> GetStoragePartitions() => throw new NotSupportedException();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => throw new NotSupportedException();
        public string GetStorageBrandModel() => throw new NotSupportedException();
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => throw new NotSupportedException();
        public string GetMotherboardModel() => throw new NotSupportedException();
        public SensorExtras GetSensorExtras() => throw new NotSupportedException();
        public string GetOsVersion() => throw new NotSupportedException();
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // Never asked to do anything: the tests catch the shutdown at ShutdownSink.
    private sealed class NoPower : ISystemPowerProvider
    {
        public bool Lock() => throw new NotSupportedException();
        public bool Sleep() => throw new NotSupportedException();
        public bool Shutdown() => throw new NotSupportedException();
        public bool Restart() => throw new NotSupportedException();
        public bool Logout() => throw new NotSupportedException();
    }

    private sealed class Rig
    {
        public PinSensors Sensors { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public ManualTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        public List<PowerDetectorNotice> Notices { get; } = new();
        public List<string> Shutdowns { get; } = new();
        public bool ShutdownWorks { get; set; } = true;
        public AstralPowerDetectorService Service { get; }

        public Rig()
        {
            Service = new AstralPowerDetectorService(Sensors, Store, new NoPower(), Time);
            Service.NoticeRaised += Notices.Add;
            Service.ShutdownSink = reason =>
            {
                Shutdowns.Add(reason);
                return ShutdownWorks;
            };
        }

        // A fresh reading with pin 3 at `amps` and the other pins at 7 A, then one tick.
        public void Tick(float amps, int seconds = 1, long? readAtMs = null)
        {
            for (var i = 0; i < seconds; i++)
            {
                Sensors.Cards.Clear();
                Sensors.Cards.Add(Card(amps, readAtMs ?? Time.GetUtcNow().ToUnixTimeMilliseconds()));
                Service.Tick();
                Time.Advance(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static GpuPinCurrents Card(float pin3Amps, long readAtMs) =>
        new("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", new[] { 7f, 7f, pin3Amps, 7f, 7f, 7f }, readAtMs);

    [Fact]
    public void Overcurrent_goes_to_windows_and_to_the_in_app_pop_up()
    {
        var rig = new Rig();

        rig.Tick(13f);

        var notice = Assert.Single(rig.Notices);
        Assert.Equal("High current in the GPU power cable", notice.Title);
        Assert.StartsWith("Pin 3 of the ROG Astral RTX 5090 power connector is carrying 13.0 A, over its 12.5 A limit.", notice.Text);
        Assert.Contains("\nUsing a ROG Equalizer cable?", notice.Text);
        Assert.False(notice.CancelsShutdown);
        Assert.Equal("/system/device/curated-asus-astral", notice.Path);
        Assert.Equal("ASUS ROG Astral RTX 5090", rig.Service.FirstCardName);

        var state = rig.Service.GetState();
        Assert.True(state.Supported);
        Assert.Equal("overcurrent", state.Level);
        Assert.Equal(3, state.Worst!.Pin);
        Assert.Equal(13f, state.Worst.Amps);
        Assert.Equal("overcurrent", state.Alert!.Level);
        Assert.Equal(notice.Title, state.Alert.Title);
        Assert.Null(state.Countdown);
        Assert.Equal(AstralPowerDetector.PinWarnAmps, state.PinWarnAmps);
        Assert.Equal(AstralPowerDetector.PinLowAmps, state.PinLowAmps);
    }

    [Fact]
    public void A_dead_pin_alerts_while_the_others_carry_current()
    {
        var rig = new Rig();

        rig.Tick(0f, AstralPowerDetector.WarningReadings);

        var notice = Assert.Single(rig.Notices);
        Assert.Equal("A GPU power pin carries no current", notice.Title);
        Assert.StartsWith("Pin 3 of the ROG Astral RTX 5090 power connector carries no current while the others carry up to 7.0 A.", notice.Text);
        Assert.Equal("warning", rig.Service.GetState().Alert!.Level);
    }

    [Fact]
    public void A_card_carrying_nothing_has_no_dead_pin()
    {
        var rig = new Rig();

        for (var i = 0; i < 10; i++)
        {
            rig.Sensors.Cards.Clear();
            rig.Sensors.Cards.Add(new GpuPinCurrents("/gpu-nvidia/0", "ASUS ROG Astral RTX 5090", new[] { 0f, 0f, 0f, 0f, 0f, 0f },
                rig.Time.GetUtcNow().ToUnixTimeMilliseconds()));
            rig.Service.Tick();
            rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void A_pin_over_its_rating_gets_its_own_wording()
    {
        var rig = new Rig();

        rig.Tick(9.6f, AstralPowerDetector.WarningReadings);

        var notice = Assert.Single(rig.Notices);
        Assert.Equal("A GPU power pin is over its rating", notice.Title);
        Assert.StartsWith("Pin 3 of the ROG Astral RTX 5090 power connector is carrying 9.6 A, over the 9.2 A a pin is rated for.", notice.Text);
        Assert.Equal(AstralPowerDetector.PinCautionAmps, rig.Service.GetState().PinCautionAmps);
    }

    [Fact]
    public void A_reading_no_pin_could_carry_is_ignored()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.AstralPowerDetector.CriticalShutdown = true);

        rig.Tick(65f, 20);

        Assert.Empty(rig.Notices);
        Assert.Empty(rig.Shutdowns);
        Assert.Equal("normal", rig.Service.GetState().Level);
        Assert.Null(rig.Service.GetState().Worst);
    }

    [Fact]
    public void Alerts_follow_their_settings()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.AstralPowerDetector.TrayAlerts = false;
            s.AstralPowerDetector.InAppAlerts = false;
            s.AstralPowerDetector.HideEqualizerReminder = true;
        });

        rig.Tick(13f);

        Assert.Empty(rig.Notices);
        Assert.Null(rig.Service.GetState().Alert);
        Assert.Equal("overcurrent", rig.Service.GetState().Level);
    }

    [Fact]
    public void A_countdown_is_always_shown_and_shuts_down_when_it_runs_out()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.AstralPowerDetector.TrayAlerts = false;
            s.AstralPowerDetector.CriticalShutdown = true;
        });

        rig.Tick(18f, 2);

        var notice = Assert.Single(rig.Notices);
        Assert.Equal("Shutting down in 10 seconds", notice.Title);
        Assert.Equal("Pin 3 of the ROG Astral RTX 5090 power connector reached 18.0 A. Save your work, or cancel the shutdown.", notice.Text);
        Assert.True(notice.CancelsShutdown);
        Assert.Equal("critical", rig.Service.GetState().Countdown!.Reason);
        Assert.NotNull(rig.Service.CurrentCountdownNotice());

        rig.Tick(9f, 9);
        Assert.Empty(rig.Shutdowns);
        rig.Tick(9f);
        var reason = Assert.Single(rig.Shutdowns);
        Assert.Contains("critical overcurrent", reason);
        Assert.True(rig.Service.GetState().ShuttingDown);
    }

    [Fact]
    public void Cancel_stops_the_shutdown()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.AstralPowerDetector.CriticalShutdown = true);
        rig.Tick(18f, 2);

        Assert.True(rig.Service.Cancel());

        rig.Tick(18f, 30);
        Assert.Empty(rig.Shutdowns);
        var state = rig.Service.GetState();
        Assert.Null(state.Countdown);
        Assert.True(state.HeldOff);
        Assert.Null(rig.Service.CurrentCountdownNotice());
        Assert.False(rig.Service.Cancel());
    }

    [Fact]
    public void Shutdown_now_cuts_the_countdown_short()
    {
        var rig = new Rig();
        Assert.False(rig.Service.ShutdownNow());
        rig.Store.Update(s => s.AstralPowerDetector.CriticalShutdown = true);
        rig.Tick(18f, 2);

        Assert.True(rig.Service.ShutdownNow());

        Assert.Single(rig.Shutdowns);
        rig.Tick(18f, 20);
        Assert.Single(rig.Shutdowns);
    }

    [Fact]
    public void A_shutdown_that_fails_ends_the_countdown_and_holds_off()
    {
        var rig = new Rig { ShutdownWorks = false };
        rig.Store.Update(s => s.AstralPowerDetector.CriticalShutdown = true);

        rig.Tick(18f, 12);

        Assert.Single(rig.Shutdowns);
        var state = rig.Service.GetState();
        Assert.Null(state.Countdown);
        Assert.False(state.ShuttingDown);
        Assert.True(state.HeldOff);
    }

    [Fact]
    public void Stale_readings_never_trip()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.AstralPowerDetector.OvercurrentShutdown = true;
            s.AstralPowerDetector.CriticalShutdown = true;
        });
        var old = rig.Time.GetUtcNow().ToUnixTimeMilliseconds() - 10_000;

        rig.Tick(18f, 30, readAtMs: old);

        Assert.Empty(rig.Notices);
        Assert.Empty(rig.Shutdowns);
        Assert.Equal("normal", rig.Service.GetState().Level);
    }

    [Fact]
    public void A_card_is_looked_for_by_reading_the_gpus_now_and_then()
    {
        var rig = new Rig();

        for (var i = 0; i < 31; i++)
        {
            rig.Service.Tick();
            rig.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(2, rig.Sensors.GpuReads);
        Assert.False(rig.Service.GetState().Supported);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        rig.Sensors.FoundOnGpuRead = new List<GpuPinCurrents> { Card(7f, rig.Time.GetUtcNow().ToUnixTimeMilliseconds()) };
        rig.Service.Tick();
        Assert.Equal(3, rig.Sensors.GpuReads);
        Assert.True(rig.Service.GetState().Supported);

        rig.Time.Advance(TimeSpan.FromSeconds(60));
        rig.Service.Tick();
        Assert.Equal(3, rig.Sensors.GpuReads);
    }

    [Fact]
    public void SetConfig_saves_and_keeps_times_within_bounds()
    {
        var rig = new Rig();

        var state = rig.Service.SetConfig(new PowerDetectorConfigBody
        {
            Equalizer = true,
            OvercurrentShutdown = true,
            OvercurrentSeconds = 1,
            CountdownSeconds = 1_000_000,
        });

        var saved = rig.Store.Load().AstralPowerDetector;
        Assert.True(saved.Equalizer);
        Assert.True(saved.OvercurrentShutdown);
        Assert.False(saved.CriticalShutdown);
        Assert.True(saved.TrayAlerts);
        Assert.Equal(AstralPowerDetector.MinOvercurrentSeconds, saved.OvercurrentSeconds);
        Assert.Equal(AstralPowerDetector.MaxSeconds, saved.CountdownSeconds);
        Assert.Equal(AstralPowerDetector.EqualizerOvercurrentAmps, state.OvercurrentAmps);
        Assert.Equal(AstralPowerDetector.EqualizerCriticalAmps, state.CriticalAmps);
        Assert.Equal(AstralPowerDetector.EqualizerPinWarnAmps, state.PinWarnAmps);
        Assert.True(state.Settings.Equalizer);
    }

    [Fact]
    public void Both_shutdowns_start_off()
    {
        var settings = new AstralPowerDetectorSettings();

        Assert.False(settings.OvercurrentShutdown);
        Assert.False(settings.CriticalShutdown);
        Assert.True(settings.TrayAlerts);
        Assert.True(settings.InAppAlerts);
        Assert.Equal(60, settings.OvercurrentSeconds);
        Assert.Equal(60, settings.CountdownSeconds);
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(10, "10 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "90 seconds")]
    [InlineData(300, "5 minutes")]
    public void Durations_read_as_people_say_them(int seconds, string expected)
    {
        Assert.Equal(expected, AstralPowerDetectorService.Duration(TimeSpan.FromSeconds(seconds)));
    }
}
