using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Plugins;
using Nexus.Service.Tests.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

// Cooling mode reaching real Lian Li wireless ports through the composite: the watchdog, the guard
// escalation and shutdown must never leave a Nexus-driven port on the follow-motherboard byte.
public class WatchdogCoolingModeTests
{
    private const string Mac = "112233445566";
    private static string Port(int n) => $"lianli-wireless:{Mac}:port{n}";

    private sealed class Rig
    {
        public required Slv3Hub Hub;
        public required CompositeFanControlProvider Fans;
        public required BoardFans Board;
        public required InMemoryConfigStore Store;
        public required ThermalGuardController Guard;
        public required CurveEngine Engine;
        public required long[] Mono;
    }

    private static Rig Build()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 3, Pwm = new[] { 40, 40, (int)Slv3Protocol.PwmFollowMotherboard, 0 } },
        };
        var board = new BoardFans();
        var noPorts = new NoPorts();
        var fans = new CompositeFanControlProvider(
            board,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry(),
            new CompositeFanControlProvider.FanSource(Slv3CoolingProvider.IsSlv3Id, new Slv3CoolingProvider(hub)));
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "c1",
                Name = "c1",
                Type = "Flat",
                Input = new CurveInputDocument { Id = "cpu", Type = "Temperature" },
                Outputs = { new CurveOutputDocument { Id = "mb:fan0", Type = "Fan" }, new CurveOutputDocument { Id = Port(0), Type = "Fan" } },
                Flat = new FlatCurveData { Speed = 20 },
            });
            s.Cooling.ManualSpeeds[Port(1)] = 30;
            // Port 2 is the user's: it stays on the motherboard header.
            s.Cooling.UncontrolledFanChannels.Add(Port(2));
        });
        var mono = new long[] { 1_000 };
        var guard = new ThermalGuardController(fans, store) { MonotonicMs = () => mono[0] };
        var engine = new CurveEngine(fans, store, new MultiplexHub(), null, guard) { Clock = () => mono[0] };
        return new Rig { Hub = hub, Fans = fans, Board = board, Store = store, Guard = guard, Engine = engine, Mono = mono };
    }

    private static void ArmAtEightyFiveC(Rig rig)
    {
        rig.Board.CpuTemp = 85f;
        rig.Mono[0] += 1000;
        rig.Engine.Tick();
        rig.Guard.TickCompleted();
    }

    [Fact]
    public void WatchdogFire_WritesFullDutyToTheBoardAndToWirelessPorts_NeverFollowMotherboard()
    {
        var rig = Build();
        ArmAtEightyFiveC(rig);

        rig.Guard.WatchdogCheck(rig.Mono[0] + 11_000);

        Assert.Equal(100, rig.Board.Duties["mb:fan0"]);
        Assert.Equal(100, rig.Hub.GetPortDuty(Mac, 0));
        Assert.Equal(100, rig.Hub.GetPortDuty(Mac, 1));
        // The user's own board-controlled port is left alone.
        Assert.Null(rig.Hub.GetPortDuty(Mac, 2));
        Assert.Equal(0, rig.Board.ReleaseAlls);
    }

    [Fact]
    public void Shutdown_AndGuardEscalationRelease_KeepDrivenWirelessPortsOnTheirDuty()
    {
        var rig = Build();
        ArmAtEightyFiveC(rig);
        rig.Guard.WatchdogCheck(rig.Mono[0] + 11_000);

        rig.Fans.ReleaseAllKeepingDrivenPorts();

        Assert.Equal(1, rig.Board.ReleaseAlls);
        Assert.Equal(100, rig.Hub.GetPortDuty(Mac, 0));
        Assert.Equal(100, rig.Hub.GetPortDuty(Mac, 1));
    }

    [Fact]
    public void PlainReleaseAll_StillHandsWirelessPortsBackToTheBoard_ForTheUsersOwnChoice()
    {
        var rig = Build();
        ArmAtEightyFiveC(rig);
        rig.Guard.WatchdogCheck(rig.Mono[0] + 11_000);

        rig.Fans.ReleaseAll();

        Assert.Null(rig.Hub.GetPortDuty(Mac, 0));
        Assert.Null(rig.Hub.GetPortDuty(Mac, 1));
    }

    [Fact]
    public void KeepingPortsRelease_StillReleasesThePumpOfAnAio()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 },
        };
        var provider = new Slv3CoolingProvider(hub);
        ((IFanControlProvider)provider).DriveFansToFull(new[] { Port(0) });

        provider.ReleaseAllKeepingDrivenPorts();
        Assert.Equal(100, hub.GetPortDuty(Mac, 0));

        provider.ReleaseAll();
        Assert.Null(hub.GetPortDuty(Mac, 0));
    }

    [Fact]
    public void ADrivenPortWithAReleasedPeer_OnlyTheDrivenOneKeepsItsDuty()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var provider = new Slv3CoolingProvider(hub);
        provider.DriveFanSpeed(Port(0), 55);

        provider.ReleaseAllKeepingDrivenPorts();

        Assert.Equal(55, hub.GetPortDuty(Mac, 0));
        Assert.Null(hub.GetPortDuty(Mac, 1));
    }

    // ── composite stall isolation ──

    [Fact]
    public void ACoolingModeWriteToAHungProvider_DoesNotBlockTheOthers_AndIsReplayedOnRecovery()
    {
        var hung = new HangingDriveFans();
        var board = new BoardFans();
        var noPorts = new NoPorts();
        var composite = new CompositeFanControlProvider(
            board,
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry(),
            new CompositeFanControlProvider.FanSource(id => id.StartsWith("ext:", StringComparison.Ordinal), hung))
        { CallBudget = TimeSpan.FromMilliseconds(100) };
        hung.Hang();

        var started = Environment.TickCount64;
        composite.DriveFansToFull(new[] { "mb:fan0", "ext:fan0" });

        Assert.True(Environment.TickCount64 - started < 4000);
        Assert.Equal(100, board.Duties["mb:fan0"]);
        Assert.Empty(hung.Driven);

        hung.Release();
        Assert.True(SpinWait.SpinUntil(() => { lock (hung.Driven) return hung.Driven.Contains(("ext:fan0", 100)); }, 5000));
    }

    [Fact]
    public void ASkippedCoolingModeWrite_IsDroppedWhenALaterCurveWriteSupersedesIt()
    {
        var hung = new HangingDriveFans();
        var noPorts = new NoPorts();
        var composite = new CompositeFanControlProvider(
            new BoardFans(),
            new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!)),
            new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!)),
            new PluginProviderRegistry(),
            new CompositeFanControlProvider.FanSource(id => id.StartsWith("ext:", StringComparison.Ordinal), hung))
        { CallBudget = TimeSpan.FromMilliseconds(100) };
        hung.Hang();
        composite.DriveFansToFull(new[] { "ext:fan0" });
        composite.DriveFanSpeed("ext:fan0", 40);

        hung.Release();
        // A write lands only once the source has recovered and finished its replays.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            composite.DriveFanSpeed("ext:fan0", 41);
            lock (hung.Driven) return hung.Driven.Contains(("ext:fan0", 41));
        }, 5000));

        // The one 100 is the original hung call; a replay would add a second.
        lock (hung.Driven) Assert.Single(hung.Driven, d => d == ("ext:fan0", 100));
    }

    private sealed class BoardFans : IFanControlProvider
    {
        public float CpuTemp = 40f;
        public int ReleaseAlls;
        public readonly Dictionary<string, int> Duties = new();
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { new FanChannel { Id = "mb:fan0", Name = "Fan" } };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            new[] { new TemperatureSource { Id = "cpu", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = CpuTemp } };
        public float? ReadTemperature(string sensorId) => CpuTemp;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) => Duties[channelId] = dutyPercent;
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() => ReleaseAlls++;
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    /// <summary>An extra source whose duty writes block while hung, like a wedged USB transfer.</summary>
    private sealed class HangingDriveFans : IFanControlProvider
    {
        private readonly ManualResetEventSlim _open = new(true);
        public readonly List<(string Id, int Duty)> Driven = new();
        public void Hang() => _open.Reset();
        public void Release() => _open.Set();
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent)
        {
            _open.Wait();
            lock (Driven) Driven.Add((channelId, dutyPercent));
        }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }
}
