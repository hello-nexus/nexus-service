using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Power;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralSupplementFeedTests
{
    private const string Hw = "/gpu-nvidia/0";

    private sealed class Store : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class NoPower : ISystemPowerProvider
    {
        public bool Lock() => true;
        public bool Sleep() => true;
        public bool Shutdown() => true;
        public bool Restart() => true;
        public bool Logout() => true;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public DateTimeOffset Now
        {
            get => _wall;
            set { _ticks += (value - _wall).Ticks; _wall = value; }
        }
        public int TimersCreated { get; private set; }
        public Timer? Last { get; private set; }
        public void JumpWall(TimeSpan by) => _wall += by;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _wall;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            return Last = new Timer { Period = period };
        }

        public sealed class Timer : ITimer
        {
            public bool Disposed { get; private set; }
            public TimeSpan Period { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Period = period; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class Client : IAstralNvApiClient
    {
        public uint SubSystemId = AstralAibVendors.AsusVendorId;
        public bool ReadSucceeds = true;
        public bool ReadThrows;
        public float Pin1Amps = 8f;
        public int Reads;

        public bool TryGetPciSubsystemId(int adapterIndex, out uint subSystemId)
        {
            subSystemId = SubSystemId;
            return true;
        }

        public bool TryReadAstralBlock(int adapterIndex, out byte[] block)
        {
            Reads++;
            if (ReadThrows)
            {
                throw new InvalidOperationException("driver fault");
            }
            block = ReadSucceeds ? Build() : Array.Empty<byte>();
            return ReadSucceeds;
        }

        public bool TryReadGSensorBlock(int adapterIndex, out byte[] block)
        {
            block = Array.Empty<byte>();
            return false;
        }

        // Words run in reverse pin order, current before voltage within a pin when read in order.
        private byte[] Build()
        {
            var b = new byte[AstralTelemetryParser.BlockSize];
            for (var pin = 0; pin < AstralTelemetryParser.PinCount; pin++)
            {
                var amps = pin == 0 ? Pin1Amps : 8f;
                Put(b, 11 - pin * 2, (ushort)(amps * 1000));
                Put(b, 11 - (pin * 2 + 1), 12000);
            }
            return b;
        }

        private static void Put(byte[] b, int word, ushort v)
        {
            b[word * 2] = (byte)(v >> 8);
            b[word * 2 + 1] = (byte)v;
        }
    }

    private sealed class Rig
    {
        public Client Client { get; } = new();
        public FakeTime Time { get; } = new();
        public AstralMonitor Monitor { get; }
        public AstralGpuSupplement Supplement { get; }
        public int Shutdowns { get; private set; }

        public Rig()
        {
            var store = new Store();
            store.Update(s => s.Astral.CriticalShutdown = true);
            Monitor = new AstralMonitor(store, new NoPower(), Time);
            Monitor.ShutdownSink = _ => { Shutdowns++; return true; };
            Supplement = new AstralGpuSupplement(Client, Time, TimeSpan.FromSeconds(1), Monitor);
        }

        // The GPU list asks for sensors once per second while something is watching.
        public void ListTick(int seconds = 1)
        {
            for (var i = 0; i < seconds; i++)
            {
                Supplement.AppendSensors(Hw, Hw, "NVIDIA GeForce RTX 5090", new List<HardwareSensor>());
                Time.Now = Time.Now.AddSeconds(1);
            }
        }

        public void Idle(int seconds) => Time.Now = Time.Now.AddSeconds(seconds);
    }

    [Fact]
    public void One_critical_sample_followed_by_failed_reads_never_arms_a_shutdown()
    {
        var rig = new Rig();
        rig.Client.Pin1Amps = 18f;
        rig.ListTick();
        rig.Client.ReadSucceeds = false;
        rig.ListTick(30);

        Assert.Null(rig.Monitor.GetState().Countdown);
        Assert.Equal(0, rig.Shutdowns);
        Assert.Null(rig.Monitor.CardName());
    }

    [Fact]
    public void Two_successive_critical_samples_do_arm_it()
    {
        var rig = new Rig();
        rig.Client.Pin1Amps = 18f;
        rig.ListTick(2);
        Assert.Equal("critical", rig.Monitor.GetState().Countdown!.Reason);
    }

    [Fact]
    public void No_fallback_timer_exists_without_an_astral_card()
    {
        var rig = new Rig();
        rig.Client.SubSystemId = 0x10DE;
        rig.ListTick(3);
        var other = new Rig();
        other.Client.ReadSucceeds = false;
        other.ListTick(3);
        Assert.Equal(0, rig.Time.TimersCreated);
        Assert.Equal(0, other.Time.TimersCreated);
    }

    [Fact]
    public void The_fallback_reads_only_while_the_gpu_list_is_not_driving_it()
    {
        var rig = new Rig();
        rig.ListTick();
        Assert.Equal(1, rig.Time.TimersCreated);

        // The list keeps probing: the fallback adds no reads.
        rig.Idle(0);
        var reads = rig.Client.Reads;
        rig.Supplement.FallbackTick();
        Assert.Equal(reads, rig.Client.Reads);

        // The list stops (monitoring off): the fallback takes over once per interval.
        rig.Idle(5);
        rig.Client.Pin1Amps = 18f;
        rig.Supplement.FallbackTick();
        Assert.Equal(reads + 1, rig.Client.Reads);
        rig.Supplement.FallbackTick();
        Assert.Equal(reads + 1, rig.Client.Reads);
        rig.Idle(1);
        rig.Supplement.FallbackTick();
        Assert.Equal("critical", rig.Monitor.GetState().Countdown!.Reason);

        // The list is back: the fallback goes quiet again.
        rig.Idle(1);
        rig.ListTick();
        var after = rig.Client.Reads;
        rig.Idle(1);
        rig.Supplement.FallbackTick();
        Assert.Equal(after, rig.Client.Reads);
    }

    [Fact]
    public void The_fallback_slows_to_a_retry_when_the_card_stops_answering_and_speeds_up_when_it_returns()
    {
        var rig = new Rig();
        rig.ListTick();
        var timer = rig.Time.Last!;
        Assert.Equal(TimeSpan.FromSeconds(1), timer.Period);
        rig.Client.ReadSucceeds = false;
        for (var i = 0; i < 70; i++)
        {
            rig.Idle(1);
            rig.Supplement.FallbackTick();
        }
        Assert.False(timer.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Period);

        rig.Client.ReadSucceeds = true;
        rig.Idle(10);
        rig.Supplement.FallbackTick();
        Assert.Equal(TimeSpan.FromSeconds(1), timer.Period);
        Assert.Equal(1, rig.Time.TimersCreated);
        Assert.NotNull(rig.Monitor.CardName());
    }

    [Theory]
    [InlineData(2.99)]
    [InlineData(3.05)]
    public void The_hand_off_from_the_list_to_the_fallback_keeps_the_critical_streak(double idleSeconds)
    {
        var rig = new Rig();
        rig.Client.Pin1Amps = 18f;
        rig.ListTick();
        // The list read at t0 and the clock now sits one interval later; the first fallback read lands idleSeconds after that.
        rig.Time.Now = rig.Time.Now.AddSeconds(idleSeconds);
        rig.Supplement.FallbackTick();
        Assert.Equal("critical", rig.Monitor.GetState().Countdown!.Reason);
    }

    [Fact]
    public void A_backward_wall_clock_step_does_not_stop_reads()
    {
        var rig = new Rig();
        rig.ListTick();
        rig.Time.JumpWall(TimeSpan.FromHours(-1));
        var reads = rig.Client.Reads;
        rig.ListTick();
        Assert.Equal(reads + 1, rig.Client.Reads);

        rig.Idle(5);
        rig.Time.JumpWall(TimeSpan.FromHours(-1));
        rig.Supplement.FallbackTick();
        Assert.Equal(reads + 2, rig.Client.Reads);
    }

    [Fact]
    public void A_read_that_throws_every_tick_still_slows_the_fallback()
    {
        var rig = new Rig();
        rig.ListTick();
        var timer = rig.Time.Last!;
        rig.Client.ReadThrows = true;
        for (var i = 0; i < 70; i++)
        {
            rig.Idle(1);
            rig.Supplement.FallbackTick();
        }
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Period);
    }

    [Fact]
    public void A_fallback_tick_that_throws_does_not_escape_the_timer_thread()
    {
        var rig = new Rig();
        rig.ListTick();
        rig.Idle(5);
        rig.Client.ReadThrows = true;
        rig.Supplement.FallbackTick();
        Assert.Equal(1, rig.Time.TimersCreated);
    }

    [Fact]
    public void A_fallback_tick_that_fires_slightly_early_still_reads()
    {
        var rig = new Rig();
        rig.ListTick();
        rig.Idle(5);
        rig.Supplement.FallbackTick();
        var reads = rig.Client.Reads;
        rig.Time.Now = rig.Time.Now.AddSeconds(0.7);
        rig.Supplement.FallbackTick();
        Assert.Equal(reads + 1, rig.Client.Reads);
    }

    [Fact]
    public void Disposing_the_supplement_stops_the_fallback_for_good()
    {
        var rig = new Rig();
        rig.ListTick();
        var timer = rig.Time.Last!;
        rig.Supplement.Dispose();
        Assert.True(timer.Disposed);
        rig.ListTick(2);
        Assert.Equal(1, rig.Time.TimersCreated);
    }
}
