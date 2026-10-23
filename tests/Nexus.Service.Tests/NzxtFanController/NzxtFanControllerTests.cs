using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.NzxtFanController;

public class NzxtFanControllerTests
{
    private sealed class FakeHid : IHidDevice
    {
        public readonly List<byte[]> Writes = new();
        public readonly Queue<byte[]> Incoming = new();
        public bool Disposed;

        public int VendorId => NzxtFanControllerProtocol.VendorId;
        public int ProductId => NzxtFanControllerProtocol.ProductIds[0];
        public string Path => "fake-nzxt-fan-controller";
        public string? Serial => null;
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool SetFeature(ReadOnlySpan<byte> report) => false;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Incoming.Count == 0)
            {
                System.Threading.Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }
            var next = Incoming.Dequeue();
            next.CopyTo(buffer);
            return next.Length;
        }

        public void Dispose() => Disposed = true;
    }

    private static byte[] Status((int Mode, int Rpm, int Duty)[] fans)
    {
        var report = new byte[NzxtFanControllerProtocol.ReportLength];
        report[0] = 0x67;
        report[1] = 0x02;
        for (int i = 0; i < fans.Length; i++)
        {
            report[16 + i] = (byte)fans[i].Mode;
            report[24 + 2 * i] = (byte)(fans[i].Rpm & 0xFF);
            report[25 + 2 * i] = (byte)(fans[i].Rpm >> 8);
            report[40 + i] = (byte)fans[i].Duty;
        }
        return report;
    }

    private static readonly (int, int, int)[] ThreeFans = { (2, 1200, 50), (1, 300, 20), (0, 0, 0) };

    /// <summary>Duties written to one channel through fixed-duty reports, in order.</summary>
    private static List<int> DutyWrites(FakeHid hid, int channel) =>
        hid.Writes
            .Where(w => w[0] == 0x62 && (w[2] & (1 << channel)) != 0)
            .Select(w => (int)w[3 + channel])
            .ToList();

    private static (NzxtFanControllerHub Hub, FakeHid Hid) Connected()
    {
        var hid = new FakeHid();
        hid.Incoming.Enqueue(Status(ThreeFans));
        var hub = new NzxtFanControllerHub();
        hub.Attach(hid);
        Assert.True(hub.Connect());
        return (hub, hid);
    }

    [Fact]
    public void Status_DecodesModeRpmAndDutyPerChannel()
    {
        Assert.True(NzxtFanControllerProtocol.TryDecodeStatus(Status(ThreeFans), out var fans));

        Assert.Equal(NzxtFanControllerProtocol.FanChannelCount, fans.Length);
        Assert.Equal(new NzxtFanReading(2, 1200, 50), fans[0]);
        Assert.Equal(new NzxtFanReading(1, 300, 20), fans[1]);
        Assert.Equal(new NzxtFanReading(0, 0, 0), fans[2]);
    }

    [Fact]
    public void Status_RpmIsLittleEndianAcrossTwoBytes()
    {
        Assert.True(NzxtFanControllerProtocol.TryDecodeStatus(Status(new[] { (2, 0x0A1B, 0) }), out var fans));

        Assert.Equal(0x0A1B, fans[0].Rpm);
    }

    [Theory]
    [InlineData(0x67, 0x01)]
    [InlineData(0x11, 0x01)]
    [InlineData(0x21, 0x03)]
    public void NonStatusReports_AreNotDecoded(int id, int sub)
    {
        var report = Status(ThreeFans);
        report[0] = (byte)id;
        report[1] = (byte)sub;

        Assert.False(NzxtFanControllerProtocol.TryDecodeStatus(report, out var fans));
        Assert.Empty(fans);
    }

    [Fact]
    public void ShortReport_IsNotDecoded()
    {
        Assert.False(NzxtFanControllerProtocol.TryDecodeStatus(new byte[] { 0x67, 0x02, 0, 0 }, out _));
    }

    [Theory]
    [InlineData(0, 0x01, 75, 0, 0)]
    [InlineData(1, 0x02, 0, 75, 0)]
    [InlineData(2, 0x04, 0, 0, 75)]
    public void FixedDuty_SetsTheChannelMaskAndItsOwnDutyByte(int channel, int mask, int d0, int d1, int d2)
    {
        var report = NzxtFanControllerProtocol.EncodeFixedDuty(channel, 75);

        Assert.Equal(NzxtFanControllerProtocol.ReportLength, report.Length);
        Assert.Equal(new byte[] { 0x62, 0x01, (byte)mask, (byte)d0, (byte)d1, (byte)d2 }, report.Take(6).ToArray());
        Assert.All(report.Skip(6), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(150, 100)]
    public void FixedDuty_ClampsToPercentRange(int asked, int sent)
    {
        Assert.Equal((byte)sent, NzxtFanControllerProtocol.EncodeFixedDuty(0, asked)[3]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void FixedDuty_RejectsAChannelPastTheThreeFanHeaders(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NzxtFanControllerProtocol.EncodeFixedDuty(channel, 50));
    }

    [Fact]
    public void Connect_SendsOnlyTheTwoFanReportingCommands()
    {
        var (_, hid) = Connected();

        Assert.Equal(2, hid.Writes.Count);
        Assert.Equal(
            new byte[] { 0x60, 0x02, 0x01, 0xE8, 0x01, 0x01, 0xE8, 0x01 },
            hid.Writes[0].Take(8).ToArray());
        Assert.Equal(new byte[] { 0x60, 0x03 }, hid.Writes[1].Take(2).ToArray());
        Assert.All(hid.Writes, w => Assert.Equal(0x60, w[0]));
    }

    [Fact]
    public void Connect_FailsWhenNoStatusReportArrives()
    {
        var hid = new FakeHid();
        var hub = new NzxtFanControllerHub();
        hub.Attach(hid);

        Assert.False(hub.Connect());
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Poll_SkipsOtherReportsAndTakesTheNextStatus()
    {
        var (hub, hid) = Connected();
        var other = new byte[NzxtFanControllerProtocol.ReportLength];
        other[0] = 0x11;
        other[1] = 0x01;
        hid.Incoming.Enqueue(other);
        hid.Incoming.Enqueue(Status(new[] { (2, 900, 40) }));

        Assert.True(hub.Poll());

        Assert.Equal(900, hub.Fans[0].Rpm);
    }

    private static int StartReportingWrites(FakeHid hid) =>
        hid.Writes.Count(w => w[0] == 0x60 && w[1] == 0x03);

    [Fact]
    public void Poll_MissResendsStartReporting()
    {
        var (hub, hid) = Connected();
        hub.StatusTimeoutMs = 20;
        int before = StartReportingWrites(hid);
        int intervalBefore = hid.Writes.Count(w => w[0] == 0x60 && w[1] == 0x02);
        hid.Writes.Clear();

        Assert.False(hub.Poll());

        Assert.Equal(2, hid.Writes.Count);
        Assert.Equal(new byte[] { 0x60, 0x02 }, hid.Writes[0].Take(2).ToArray());
        Assert.Equal(new byte[] { 0x60, 0x03 }, hid.Writes[1].Take(2).ToArray());
        Assert.Equal(1, before);
        Assert.Equal(1, intervalBefore);
    }

    [Fact]
    public void Poll_ShortSilenceStaysConnectedAndLongSilenceIsLost()
    {
        var (hub, hid) = Connected();
        hub.StatusTimeoutMs = 20;

        for (int i = 0; i < NzxtFanControllerHub.MaxMissedPolls - 1; i++)
        {
            Assert.False(hub.Poll());
        }
        Assert.False(hub.Lost);
        hid.Incoming.Enqueue(Status(ThreeFans));
        Assert.True(hub.Poll());
        for (int i = 0; i < NzxtFanControllerHub.MaxMissedPolls - 1; i++)
        {
            Assert.False(hub.Poll());
        }
        Assert.False(hub.Lost);

        Assert.False(hub.Poll());

        Assert.True(hub.Lost);
    }

    [Fact]
    public void ConnectAndDetachBroadcastCooling_FailedConnectDoesNot()
    {
        var ws = new MultiplexHub();
        var topics = new List<string>();
        ws.OnBroadcastForTest += (topic, _) => topics.Add(topic);
        using var sub = ws.AddTestSubscription(PanelTopics.Cooling);
        var hid = new FakeHid();
        var hub = new NzxtFanControllerHub(ws) { StatusTimeoutMs = 20 };
        hub.Attach(hid);

        Assert.False(hub.Connect());
        hub.Detach();
        Assert.Empty(topics);

        var hid2 = new FakeHid();
        hid2.Incoming.Enqueue(Status(ThreeFans));
        hub.Attach(hid2);
        Assert.True(hub.Connect());
        Assert.Single(topics);
        hub.Detach();

        Assert.Equal(2, topics.Count);
        Assert.All(topics, t => Assert.Equal(PanelTopics.Cooling, t));
    }

    [Fact]
    public void Detach_ClearsStateAndDisposesTheHandle()
    {
        var (hub, hid) = Connected();

        hub.Detach();

        Assert.False(hub.IsConnected);
        Assert.Empty(hub.Fans);
        Assert.True(hid.Disposed);
        Assert.False(hub.SetFanDuty(0, 50));
    }

    [Fact]
    public void Provider_ListsEveryHeaderUnderOneDevice()
    {
        var (hub, _) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);

        var channels = provider.GetFanChannels();

        Assert.Equal(new[] { "nzxt-fan-controller:fan0", "nzxt-fan-controller:fan1", "nzxt-fan-controller:fan2" },
            channels.Select(c => c.Id).ToArray());
        Assert.All(channels, c =>
        {
            Assert.Equal(NzxtFanControllerHub.DeviceId, c.DeviceId);
            Assert.Equal(NzxtFanControllerHub.ProductName, c.DeviceName);
            Assert.Equal(FanKinds.Fan, c.Kind);
            Assert.Equal(FanModes.Auto, c.Mode);
        });
        Assert.Equal(1200, channels[0].Rpm);
        Assert.Equal(50, channels[0].DutyPercent);
        var component = Assert.Single(provider.GetAll());
        Assert.Equal(NzxtFanControllerProtocol.FanChannelCount, component.Devices.Count);
    }

    [Fact]
    public void Provider_IsEmptyWhileDisconnected()
    {
        var provider = new NzxtFanControllerCoolingProvider(new NzxtFanControllerHub());

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetAll());
        Assert.Empty(provider.GetTemperatureSources());
    }

    [Fact]
    public void SetFanSpeed_WritesTheDutyAndMarksTheChannelManual()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        hid.Writes.Clear();

        var applied = provider.SetFanSpeed("nzxt-fan-controller:fan1", 130);

        Assert.Equal(100, applied);
        var write = Assert.Single(hid.Writes);
        Assert.Equal(new byte[] { 0x62, 0x01, 0x02, 0, 100, 0 }, write.Take(6).ToArray());
        Assert.Equal(new[] { 100 }, DutyWrites(hid, 1));
        var channels = provider.GetFanChannels();
        Assert.Equal(FanModes.Manual, channels[1].Mode);
        Assert.Equal(FanModes.Auto, channels[0].Mode);
    }

    [Theory]
    [InlineData("nzxt-fan-controller:fan3")]
    [InlineData("nzxt-fan-controller:fan")]
    [InlineData("nzxt-fan-controller:fan-1")]
    [InlineData("nzxt-kraken:fan")]
    [InlineData("")]
    public void SetFanSpeed_IgnoresAnIdThatIsNotOneOfTheThreeChannels(string id)
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        hid.Writes.Clear();

        provider.DriveFanSpeed(id, 50);

        for (int ch = 0; ch < NzxtFanControllerProtocol.FanChannelCount; ch++)
        {
            Assert.Empty(DutyWrites(hid, ch));
        }
    }

    [Fact]
    public void Release_ReturnsTheChannelToAutoWithoutWriting()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 60);
        hid.Writes.Clear();

        provider.ReleaseFan("nzxt-fan-controller:fan0");

        Assert.Empty(DutyWrites(hid, 0));
        Assert.Equal(FanModes.Auto, provider.GetFanChannels()[0].Mode);
    }

    private static void Reassert(NzxtFanControllerCoolingProvider provider, int times)
    {
        for (int i = 0; i < times; i++)
        {
            provider.ReassertControl();
        }
    }

    [Fact]
    public void Reassert_WritesAgainOnlyAfterSeveralReportsOffTheCommandedDuty()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 80);
        hid.Writes.Clear();

        Reassert(provider, 2);
        Assert.Empty(DutyWrites(hid, 0));

        Reassert(provider, 1);
        Assert.Equal(new[] { 80 }, DutyWrites(hid, 0));
    }

    [Fact]
    public void Reassert_StopsAfterAFewResendsThatNeverConverge()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 80);
        hid.Writes.Clear();

        Reassert(provider, 60);

        Assert.Equal(new[] { 80, 80, 80 }, DutyWrites(hid, 0));
    }

    [Fact]
    public void CapWarning_FiresOnceAfterTheLagAndRearmsOnANewDuty()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 80);

        // Every allowed resend takes one lag; the cap is only reported after one more lag.
        Reassert(provider, 9);
        Assert.Equal(0, provider.CapWarnings);
        Reassert(provider, 2);
        Assert.Equal(0, provider.CapWarnings);
        Reassert(provider, 1);
        Assert.Equal(1, provider.CapWarnings);
        Reassert(provider, 30);
        Assert.Equal(1, provider.CapWarnings);

        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 70);
        Reassert(provider, 12);
        Assert.Equal(2, provider.CapWarnings);
        Assert.Equal(new[] { 70, 70, 70, 70 }, DutyWrites(hid, 0).Skip(4).ToList());
    }

    [Fact]
    public void CapWarning_RearmsAfterTheChannelConverges()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 80);
        Reassert(provider, 12);
        Assert.Equal(1, provider.CapWarnings);

        hid.Incoming.Enqueue(Status(new[] { (2, 1200, 80), (1, 300, 20), (0, 0, 0) }));
        Assert.True(hub.Poll());
        Reassert(provider, 1);
        hid.Incoming.Enqueue(Status(ThreeFans));
        Assert.True(hub.Poll());
        Reassert(provider, 12);

        Assert.Equal(2, provider.CapWarnings);
    }

    [Fact]
    public void Reassert_StartsOverAfterAReconnect()
    {
        var (hub, _) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 80);
        Reassert(provider, 60);
        Assert.Equal(1, provider.CapWarnings);
        hub.Detach();
        var again = new FakeHid();
        again.Incoming.Enqueue(Status(ThreeFans));
        hub.Attach(again);
        Assert.True(hub.Connect());
        again.Writes.Clear();

        Reassert(provider, 3);
        Assert.Equal(new[] { 80 }, DutyWrites(again, 0));

        Reassert(provider, 9);
        Assert.Equal(2, provider.CapWarnings);
    }

    [Fact]
    public void Reassert_StillWritesAChannelWhoseModeByteReadsNone()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan2", 60);
        hid.Writes.Clear();

        Reassert(provider, 3);

        Assert.Equal(new[] { 60 }, DutyWrites(hid, 2));
    }

    [Fact]
    public void Reassert_LeavesAChannelAloneOnceItReportsTheCommandedDuty()
    {
        var (hub, hid) = Connected();
        var provider = new NzxtFanControllerCoolingProvider(hub);
        provider.DriveFanSpeed("nzxt-fan-controller:fan0", 50);
        hid.Writes.Clear();

        Reassert(provider, 6);

        Assert.Empty(DutyWrites(hid, 0));
    }

    [Fact]
    public void Handler_MatchesOnlySiblingProducts()
    {
        var handler = new NzxtFanControllerHandler(new NzxtFanControllerHub());

        Assert.Equal(
            new[] { new UsbId(0x1E71, 0x2011), new UsbId(0x1E71, 0x2019), new UsbId(0x1E71, 0x201F) },
            handler.Identifiers);
        Assert.False(handler.IsConnected(new[] { Entry(0x1E71, 0x2020), Entry(0x1E71, 0x2006), Entry(0x1E71, 0x3012), Entry(0x3402, 0x2011) }));
        Assert.False(handler.HasPage);
    }

    [Theory]
    [InlineData(0x2011)]
    [InlineData(0x2019)]
    [InlineData(0x201F)]
    public void Handler_ConnectedForEachSiblingProduct(int pid)
    {
        var handler = new NzxtFanControllerHandler(new NzxtFanControllerHub());

        Assert.True(handler.IsConnected(new[] { Entry(0x1E71, pid) }));
    }

    [Fact]
    public void Handler_IsOffByDefaultBecauseCamStartsWhitelisted()
    {
        Assert.False(DeviceControlPolicy.DefaultOn(NzxtFanControllerHub.DeviceId));
        Assert.Equal("nzxt-cam", DeviceControlPolicy.ConflictAppFor(NzxtFanControllerHub.DeviceId));
    }

    private static UsbDeviceEntry Entry(int vid, int pid) => new() { VendorId = vid, ProductId = pid };
}
