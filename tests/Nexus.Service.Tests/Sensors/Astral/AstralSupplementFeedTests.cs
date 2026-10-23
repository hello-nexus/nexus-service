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
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int TimersCreated { get; private set; }
        public Timer? Last { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            return Last = new Timer();
        }

        public sealed class Timer : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class Client : IAstralNvApiClient
    {
        public uint SubSystemId = AstralAibVendors.AsusVendorId;
        public bool ReadSucceeds = true;
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
    public void The_fallback_goes_away_once_the_card_stops_answering()
    {
        var rig = new Rig();
        rig.ListTick();
        var timer = rig.Time.Last!;
        rig.Client.ReadSucceeds = false;
        for (var i = 0; i < 70 && !timer.Disposed; i++)
        {
            rig.Idle(1);
            rig.Supplement.FallbackTick();
        }
        Assert.True(timer.Disposed);
    }
}
