using Nexus.Service.Cooling;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Tests.Cooling;

public class CommanderCoreCoolingProviderTests
{
    private const int CommanderCore = 0x0C1C;
    private const int CommanderCoreXt = 0x0C2A;

    private sealed class FakeSource : CommanderCoreCoolingProvider.ISource
    {
        public List<CommanderCoreCoolingProvider.Endpoint> Listed { get; } = new();

        public IReadOnlyList<CommanderCoreCoolingProvider.Endpoint> Endpoints() => Listed.ToArray();
    }

    private sealed class FakeTransport : INexusCoolingTransport
    {
        public NexusCoolingOutcome StatusOutcome { get; set; } = NexusCoolingOutcome.Ok;
        public NexusCoolingOutcome SetOutcome { get; set; } = NexusCoolingOutcome.Ok;
        public CommanderCoreStatus? Status { get; set; }
        public List<(int Address, List<(int Channel, int Duty)> Table)> Writes { get; } = new();

        public Task<NexusCoolingStatusResult> GetCoolingStatusAsync(int address, CancellationToken ct = default) =>
            Task.FromResult(new NexusCoolingStatusResult(StatusOutcome, StatusOutcome == NexusCoolingOutcome.Ok ? Status : null));

        public Task<NexusCoolingOutcome> SetCoolingDutiesAsync(int address, IReadOnlyList<(int Channel, int Duty)> duties, CancellationToken ct = default)
        {
            Writes.Add((address, duties.ToList()));
            return Task.FromResult(SetOutcome);
        }
    }

    private sealed class Rig
    {
        public FakeSource Source { get; } = new();
        public FakeTransport Transport { get; } = new();
        public DateTime Now { get; set; } = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        public CommanderCoreCoolingProvider Provider { get; }

        public Rig(int address = 3)
        {
            Source.Listed.Add(new CommanderCoreCoolingProvider.Endpoint("openrgb-l-HID__a", "Corsair Commander Core", address));
            Provider = new CommanderCoreCoolingProvider(Source, Transport, config: null, now: () => Now);
        }

        public Task Poll() => Provider.PollOnceAsync(CancellationToken.None);
    }

    // Pump and three fans on a Commander Core; the vendor capture's connected set.
    private static CommanderCoreStatus Aio(int pid = CommanderCore, params int?[] applied)
    {
        var channels = new List<CommanderCoreChannel>
        {
            new(true, 2357, applied.Length > 0 ? applied[0] : null),
            new(true, 918, applied.Length > 1 ? applied[1] : null),
            new(true, 903, applied.Length > 2 ? applied[2] : null),
            new(true, 501, applied.Length > 3 ? applied[3] : null),
            new(false, 0, null),
            new(false, 0, null),
            new(false, 0, null),
        };
        return new CommanderCoreStatus(true, pid, 100, channels, new[]
        {
            new CommanderCoreTemperature(true, 27.5f),
            new CommanderCoreTemperature(false, 0f),
        });
    }

    [Fact]
    public async Task Lists_the_connected_pump_and_fans_with_the_daemons_readings()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        var channels = rig.Provider.GetFanChannels();

        Assert.Equal(new[] { "Pump", "Fan 1", "Fan 2", "Fan 3" }, channels.Select(c => c.Name));
        Assert.Equal(new[] { 2357, 918, 903, 501 }, channels.Select(c => c.Rpm));
        Assert.Equal(FanKinds.Pump, channels[0].Kind);
        Assert.All(channels.Skip(1), c => Assert.Equal(FanKinds.Fan, c.Kind));
        Assert.All(channels, c => Assert.Equal("Corsair Commander Core", c.DeviceName));
        Assert.All(channels, c => Assert.Equal(FanModes.Auto, c.Mode));
        Assert.Single(channels.Select(c => c.DeviceId).Distinct());
        Assert.StartsWith("cmdcore:", channels[0].Id);
        Assert.True(CommanderCoreCoolingProvider.IsCommanderCoreId(channels[0].Id));
    }

    [Fact]
    public async Task The_core_xt_has_no_pump_and_counts_its_fans_from_one()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio(CommanderCoreXt);
        await rig.Poll();

        var channels = rig.Provider.GetFanChannels();

        Assert.Equal(new[] { "Fan 1", "Fan 2", "Fan 3", "Fan 4" }, channels.Select(c => c.Name));
        Assert.All(channels, c => Assert.Equal(FanKinds.Fan, c.Kind));
        Assert.Equal("Temperature probe 1", rig.Provider.GetTemperatureSources().Single().Name);
    }

    [Fact]
    public async Task The_channel_ids_are_stable_for_a_device_across_polls_and_address_changes()
    {
        var rig = new Rig(address: 3);
        rig.Transport.Status = Aio();
        await rig.Poll();
        var before = rig.Provider.GetFanChannels().Select(c => c.Id).ToArray();

        rig.Source.Listed[0] = rig.Source.Listed[0] with { Address = 9 };
        await rig.Poll();

        Assert.Equal(before, rig.Provider.GetFanChannels().Select(c => c.Id));
    }

    [Fact]
    public async Task Surfaces_connected_temperature_probes_and_reads_them_by_id()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        var source = Assert.Single(rig.Provider.GetTemperatureSources());

        Assert.Equal("Coolant temperature", source.Name);
        Assert.Equal(27.5f, source.Value);
        Assert.Equal(27.5f, rig.Provider.ReadTemperature(source.Id));
        Assert.Null(rig.Provider.ReadTemperature(source.Id.Replace(":t0", ":t1")));
        Assert.Null(rig.Provider.ReadTemperature("corsair:ch1:temp"));
    }

    [Fact]
    public async Task Nothing_is_written_until_a_duty_is_commanded()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();

        await rig.Poll();
        await rig.Poll();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_commanded_duty_sends_the_whole_table_with_safe_defaults_for_the_rest()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan2 = rig.Provider.GetFanChannels()[2];

        rig.Provider.DriveFanSpeed(fan2.Id, 80);
        await rig.Poll();

        var write = Assert.Single(rig.Transport.Writes);
        Assert.Equal(3, write.Address);
        Assert.Equal(new[] { (0, 70), (1, 50), (2, 80), (3, 50) }, write.Table);
        Assert.Equal(FanModes.Manual, rig.Provider.GetFanChannels()[2].Mode);
    }

    [Fact]
    public async Task A_pump_is_never_driven_below_its_floor()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var pump = rig.Provider.GetFanChannels()[0];

        var applied = rig.Provider.SetFanSpeed(pump.Id, 10);
        rig.Provider.DriveFanSpeed(pump.Id, 20);
        await rig.Poll();

        Assert.Equal(50, applied);
        Assert.Equal((0, 50), Assert.Single(rig.Transport.Writes).Table[0]);
    }

    [Fact]
    public async Task Repeating_a_duty_the_daemon_has_applied_writes_nothing()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 60);
        await rig.Poll();
        // The daemon now reports the table it applied.
        rig.Transport.Status = Aio(CommanderCore, 70, 60, 50, 50);

        rig.Provider.DriveFanSpeed(fan1.Id, 60);
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_changed_duty_is_written_again()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 60);
        await rig.Poll();
        rig.Transport.Status = Aio(CommanderCore, 70, 60, 50, 50);

        rig.Provider.DriveFanSpeed(fan1.Id, 65);
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Equal((1, 65), rig.Transport.Writes[1].Table[1]);
    }

    [Fact]
    public async Task A_daemon_that_forgot_the_table_gets_it_again_but_not_every_poll()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        rig.Provider.DriveFanSpeed(rig.Provider.GetFanChannels()[1].Id, 60);
        await rig.Poll();
        Assert.Single(rig.Transport.Writes);

        // Applied duties still unknown: the daemon lost the device or restarted.
        rig.Now += TimeSpan.FromSeconds(1);
        await rig.Poll();
        Assert.Single(rig.Transport.Writes);
        rig.Now += TimeSpan.FromSeconds(5);
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
    }

    [Fact]
    public async Task A_renumbered_controller_gets_the_commanded_table_at_once()
    {
        var rig = new Rig(address: 3);
        rig.Transport.Status = Aio();
        await rig.Poll();
        rig.Provider.DriveFanSpeed(rig.Provider.GetFanChannels()[1].Id, 60);
        await rig.Poll();
        rig.Transport.Status = Aio(CommanderCore, 70, 60, 50, 50);
        await rig.Poll();
        Assert.Single(rig.Transport.Writes);

        rig.Source.Listed[0] = rig.Source.Listed[0] with { Address = 0 };
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Equal(0, rig.Transport.Writes[1].Address);
    }

    [Fact]
    public async Task A_refused_write_is_retried_on_the_next_poll()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        rig.Provider.DriveFanSpeed(rig.Provider.GetFanChannels()[1].Id, 60);
        rig.Transport.SetOutcome = NexusCoolingOutcome.Failed;
        await rig.Poll();

        rig.Transport.SetOutcome = NexusCoolingOutcome.Ok;
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
    }

    [Fact]
    public async Task A_command_for_a_channel_that_is_not_connected_is_dropped()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var id = rig.Provider.GetFanChannels()[0].Id.Replace(":ch0", ":ch5");

        rig.Provider.DriveFanSpeed(id, 80);
        await rig.Poll();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public async Task Releasing_a_fan_returns_it_to_the_default_in_the_next_table()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var channels = rig.Provider.GetFanChannels();
        rig.Provider.DriveFanSpeed(channels[1].Id, 90);
        rig.Provider.DriveFanSpeed(channels[2].Id, 90);
        await rig.Poll();

        rig.Provider.ReleaseFan(channels[1].Id);
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Equal(new[] { (0, 70), (1, 50), (2, 90), (3, 50) }, rig.Transport.Writes[1].Table);
        Assert.Equal(FanModes.Auto, rig.Provider.GetFanChannels()[1].Mode);
    }

    [Fact]
    public async Task Releasing_every_channel_hands_the_device_back_with_an_empty_table_once()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 90);
        await rig.Poll();
        rig.Transport.Status = Aio(CommanderCore, 70, 90, 50, 50);
        await rig.Poll();

        rig.Provider.ReleaseFan(fan1.Id);
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Empty(rig.Transport.Writes[1].Table);
        Assert.Equal(FanModes.Auto, rig.Provider.GetFanChannels()[1].Mode);
    }

    [Fact]
    public async Task Release_all_hands_back_only_devices_that_were_commanded()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        rig.Provider.ReleaseAll();
        await rig.Poll();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_channel_nobody_commanded_reports_the_default_duty_not_zero()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        var channels = rig.Provider.GetFanChannels();

        Assert.Equal(70, channels[0].DutyPercent);
        Assert.Equal(50, channels[1].DutyPercent);
    }

    [Fact]
    public async Task A_release_during_a_bridge_blip_still_hands_the_device_back()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 0);
        await rig.Poll();
        var listed = rig.Source.Listed[0];

        rig.Source.Listed.Clear();
        await rig.Poll();
        rig.Provider.ReleaseFan(fan1.Id);
        rig.Source.Listed.Add(listed);
        rig.Transport.Status = Aio(CommanderCore, 70, 0, 50, 50);
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Empty(rig.Transport.Writes[1].Table);
    }

    [Fact]
    public async Task A_release_on_a_controller_at_address_zero_hands_back_too()
    {
        var rig = new Rig(address: 0);
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 40);
        await rig.Poll();
        rig.Transport.Status = Aio(CommanderCore, 70, 40, 50, 50);
        await rig.Poll();

        rig.Provider.ReleaseFan(fan1.Id);
        await rig.Poll();

        Assert.Equal(2, rig.Transport.Writes.Count);
        Assert.Empty(rig.Transport.Writes[1].Table);
    }

    [Fact]
    public async Task A_duty_the_daemon_holds_that_nobody_commands_is_handed_back_once_and_not_every_poll()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio(CommanderCore, 70, 0, 50, 50);

        await rig.Poll();
        await rig.Poll();

        var write = Assert.Single(rig.Transport.Writes);
        Assert.Empty(write.Table);
    }

    [Fact]
    public async Task The_hand_back_duties_are_not_mistaken_for_a_command_to_undo()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio(CommanderCore, 70, 50, 50, 50);

        await rig.Poll();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_daemon_that_restarted_and_forgot_the_table_is_not_handed_back_to()
    {
        var rig = new Rig(address: 3);
        rig.Transport.Status = Aio();
        await rig.Poll();
        var fan1 = rig.Provider.GetFanChannels()[1];
        rig.Provider.DriveFanSpeed(fan1.Id, 90);
        await rig.Poll();
        rig.Provider.ReleaseFan(fan1.Id);
        rig.Source.Listed[0] = rig.Source.Listed[0] with { Address = 0 };
        rig.Transport.Status = Aio();

        await rig.Poll();
        rig.Now += TimeSpan.FromSeconds(4);
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_command_replayed_before_the_first_contact_is_sent_when_the_device_shows_up()
    {
        var first = new Rig();
        first.Transport.Status = Aio();
        await first.Poll();
        var channels = first.Provider.GetFanChannels();
        var pump = channels[0].Id;
        var fan2 = channels[2].Id;

        var restarted = new Rig();
        restarted.Transport.Status = Aio();
        restarted.Provider.DriveFanSpeed(fan2, 65);
        restarted.Provider.DriveFanSpeed(pump, 10);
        await restarted.Poll();

        var write = Assert.Single(restarted.Transport.Writes);
        Assert.Equal(new[] { (0, 50), (1, 50), (2, 65), (3, 50) }, write.Table);
    }

    [Fact]
    public async Task A_replayed_command_for_an_id_that_is_not_a_channel_id_is_ignored()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();

        rig.Provider.DriveFanSpeed("cmdcore:zz:ch1", 65);
        rig.Provider.DriveFanSpeed("cmdcore:0123abcd:ch9", 65);
        await rig.Poll();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public async Task A_daemon_without_cooling_support_surfaces_nothing_and_is_asked_again_later()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        rig.Transport.StatusOutcome = NexusCoolingOutcome.Unsupported;
        await rig.Poll();
        Assert.Empty(rig.Provider.GetFanChannels());

        rig.Transport.StatusOutcome = NexusCoolingOutcome.Ok;
        await rig.Poll();
        Assert.Empty(rig.Provider.GetFanChannels());
        rig.Now += TimeSpan.FromMinutes(2);
        await rig.Poll();

        Assert.NotEmpty(rig.Provider.GetFanChannels());
    }

    [Fact]
    public async Task Readings_older_than_the_stale_window_disappear_instead_of_freezing()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();
        Assert.NotEmpty(rig.Provider.GetFanChannels());

        rig.Transport.StatusOutcome = NexusCoolingOutcome.Failed;
        rig.Now += TimeSpan.FromSeconds(3);
        await rig.Poll();
        Assert.NotEmpty(rig.Provider.GetFanChannels());
        rig.Now += TimeSpan.FromSeconds(3);
        await rig.Poll();

        Assert.Empty(rig.Provider.GetFanChannels());
        Assert.Empty(rig.Provider.GetTemperatureSources());
        Assert.Empty(rig.Provider.GetAll());
    }

    [Fact]
    public async Task A_status_without_fresh_data_is_no_data()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio() with { Valid = false };

        await rig.Poll();

        Assert.Empty(rig.Provider.GetFanChannels());
    }

    [Fact]
    public async Task A_device_the_bridge_stops_listing_is_forgotten()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        rig.Source.Listed.Clear();
        await rig.Poll();

        Assert.Empty(rig.Provider.GetFanChannels());
        Assert.False(rig.Provider.HasDevices);
    }

    [Fact]
    public async Task The_cooling_component_lists_each_connected_port()
    {
        var rig = new Rig();
        rig.Transport.Status = Aio();
        await rig.Poll();

        var component = Assert.Single(rig.Provider.GetAll());

        Assert.Equal("Corsair Commander Core", component.Name);
        Assert.Equal(new[] { "Pump", "Fan", "Fan", "Fan" }, component.Devices.Select(d => d.Type));
        Assert.Equal(2357, component.Devices[0].Rpm);
    }

    [Theory]
    [InlineData("Corsair Commander Core", true)]
    [InlineData("Corsair Commander Core XT", true)]
    [InlineData("Corsair iCUE LINK System Hub", false)]
    [InlineData("Corsair Lighting Node Pro", false)]
    public void Matches_the_controllers_the_fork_driver_names(string name, bool expected)
    {
        Assert.Equal(expected, CommanderCoreCoolingProvider.IsCommanderCoreName(name));
    }
}
