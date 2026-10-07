using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3MotherboardArgbTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");
    private static readonly string Mac = Convert.ToHexString(FanMac);

    private long _now;
    private readonly Slv3Hub _hub;
    private readonly Slv3TestHub.FakeSlv3Network _net;
    private readonly Slv3TestHub.FakeTxTransport _tx;
    private readonly InMemoryConfigStore _store = new();
    private readonly Slv3LightingDeviceProvider _provider;
    private readonly Slv3LightingFrameWriter _writer;

    public Slv3MotherboardArgbTests()
    {
        (_hub, _net, _tx) = Slv3TestHub.CreateConnected();
        _net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = _net.MasterMac, RxType = 1, FanCount = 3, FansType = 24, ArgbCable = true });
        Assert.True(_hub.DriveTick());
        var identify = new Np50IdentifyTracker();
        _provider = new Slv3LightingDeviceProvider(_hub, _store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(_provider.BuildFrames(0).ToArray());
        _writer = new Slv3LightingFrameWriter(engine, _hub, _store, identify, _provider, () => _now);
    }

    private void SetArgb(bool on) => _store.Update(s =>
        s.Devices.LianLiWireless.Chains = new Dictionary<string, LianLiWirelessChainLighting>
        {
            [Mac] = new() { MotherboardArgb = on },
        });

    private List<byte[]> SwitchFrames() =>
        _tx.SentFrames.FindAll(f => f.Length >= 25 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfArgbSyncSwitch);

    [Fact]
    public void Turning_it_on_switches_the_chain_and_hides_its_cards()
    {
        SetArgb(true);

        _writer.Tick();
        Assert.True(_hub.DriveTick());

        var frame = Assert.Single(SwitchFrames());
        Assert.Equal(1, frame[24]);
        Assert.True(_hub.State.Fans[0].PlayingMotherboardArgb);
        Assert.Empty(_provider.BuildStructures());

        _now += 10_000 * TimeSpan.TicksPerMillisecond;
        _writer.Tick();
        Assert.Single(SwitchFrames());
    }

    [Fact]
    public void Turning_it_off_hands_the_chain_back()
    {
        SetArgb(true);
        _writer.Tick();
        Assert.True(_hub.DriveTick());

        SetArgb(false);
        _writer.Tick();
        Assert.True(_hub.DriveTick());

        Assert.Equal(0, SwitchFrames()[^1][24]);
        Assert.False(_hub.State.Fans[0].PlayingMotherboardArgb);
        Assert.NotEmpty(_provider.BuildStructures());
    }

    [Fact]
    public void A_chain_whose_firmware_ignores_the_switch_is_asked_a_bounded_number_of_times()
    {
        _net.Fans[0].SupportsArgbSwitch = false;
        SetArgb(true);

        for (var i = 0; i < 10; i++)
        {
            _writer.Tick();
            _now += 6_000 * TimeSpan.TicksPerMillisecond;
        }

        // One fresh send per request (the hub's own echo retries need a device-list poll).
        Assert.Equal(3, SwitchFrames().Select(f => f[21]).Distinct().Count());
    }

    [Fact]
    public void A_chain_with_no_saved_choice_keeps_its_own_state()
    {
        _net.Fans[0].PlayingMotherboardArgb = true;
        Assert.True(_hub.DriveTick());

        _writer.Tick();

        Assert.Empty(SwitchFrames());
    }

    [Fact]
    public void A_chain_playing_its_input_with_no_saved_choice_has_no_cards()
    {
        _net.Fans[0].PlayingMotherboardArgb = true;
        Assert.True(_hub.DriveTick());

        Assert.Empty(_provider.BuildStructures());
    }

    [Fact]
    public void A_pending_identify_holds_the_switch_back()
    {
        Assert.True(_hub.Identify(Mac));
        SetArgb(true);

        _writer.Tick();

        Assert.Empty(SwitchFrames());
    }

    [Fact]
    public void Nothing_is_sent_while_the_setting_and_the_chain_agree()
    {
        _writer.Tick();

        Assert.Empty(SwitchFrames());
    }
}
