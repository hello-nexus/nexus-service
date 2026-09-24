using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Shared fake TX/RX harness for tests that need a connected <see cref="Slv3Hub"/>
/// with bound fan chains (lighting provider/writer tests), factored out of the
/// bind/unbind state-machine harness in <see cref="Slv3HubTests"/>.
/// </summary>
internal static class Slv3TestHub
{
    public static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx) CreateConnected()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        if (!hub.EnsureConnected())
        {
            throw new InvalidOperationException("test harness failed to connect");
        }
        return (hub, net, tx);
    }

    public sealed class FakeDiscovery : ISlv3Discovery
    {
        public IReadOnlyList<Slv3PortInfo> Discover() => new[]
        {
            new Slv3PortInfo { PortName = "fake-tx", Role = Slv3DongleRole.Tx },
            new Slv3PortInfo { PortName = "fake-rx", Role = Slv3DongleRole.Rx },
        };
    }

    public sealed class SimulatedFan
    {
        public required byte[] Mac { get; init; }
        public byte[] MasterMac { get; set; } = new byte[6];
        public byte Channel { get; set; } = Slv3Protocol.DefaultChannel;
        public byte RxType { get; set; }
        public byte DevType { get; set; }
        public byte FanCount { get; set; } = 1;
        /// <summary>fans_type byte reported for every port; 0 = unclassified.</summary>
        public byte FansType { get; set; }
        public byte[] EffectIndex { get; set; } = new byte[4];
    }

    public sealed class FakeSlv3Network
    {
        public byte[] MasterMac { get; } = Convert.FromHexString("AABBCCDDEEFF");
        public List<SimulatedFan> Fans { get; } = new();
    }

    public sealed class FakeTxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;

        public FakeTxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Tx;
        public string PortName => "fake-tx";
        public List<byte[]> SentFrames { get; } = new();

        /// <summary>Every send is recorded, then reported as failed.</summary>
        public bool FailSends { get; set; }

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (FailSends)
            {
                return false;
            }
            // The chunkSeq-0 USB frame carries RF payload bytes [0..59], which
            // includes the RF_RgbSync header's effect_index at RF-offset
            // [14..18) - USB-frame offset [18..22). Mirrors real firmware
            // caching the streamed effect_index and echoing it in the next
            // device-list poll.
            if (copy.Length >= 22 && copy[0] == Slv3Protocol.UsbSendRf && copy[1] == 0
                && copy[4] == Slv3Protocol.RfFrameType && copy[5] == Slv3Protocol.RfRgbSync)
            {
                var fanMac = copy.AsSpan(6, 6).ToArray();
                foreach (var fan in _net.Fans)
                {
                    if (Slv3Protocol.MacEquals(fan.Mac, fanMac))
                    {
                        fan.EffectIndex = copy.AsSpan(18, 4).ToArray();
                        break;
                    }
                }
            }
            return true;
        }

        public byte[] RfRead(int expectedLen)
        {
            var reply = new byte[64];
            reply[0] = Slv3Protocol.UsbGetMac;
            _net.MasterMac.CopyTo(reply, 1);
            return reply;
        }

        public void Dispose()
        {
        }
    }

    public sealed class FakeRxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;

        public FakeRxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";

        public bool RfSend(ReadOnlySpan<byte> frame) => true;

        public byte[] RfRead(int expectedLen)
        {
            var buf = new byte[Slv3Protocol.RecordHeaderLength + _net.Fans.Count * Slv3Protocol.RecordLength];
            buf[0] = Slv3Protocol.UsbSendRf;
            buf[1] = (byte)_net.Fans.Count;
            for (var i = 0; i < _net.Fans.Count; i++)
            {
                var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
                var rec = buf.AsSpan(offset);
                var fan = _net.Fans[i];
                fan.Mac.CopyTo(rec);
                fan.MasterMac.CopyTo(rec.Slice(6));
                rec[12] = fan.Channel;
                rec[13] = fan.RxType;
                rec[18] = fan.DevType;
                rec[19] = fan.FanCount;
                for (var p = 0; p < Slv3Protocol.PortsPerRecord; p++) rec[24 + p] = fan.FansType;
                fan.EffectIndex.CopyTo(rec.Slice(20, 4));
                rec[41] = Slv3Protocol.RecordValidator;
            }
            // Firmware sends only the requested pages: the header still reports the
            // true device count, but records past PageLength * pageCount bytes are
            // truncated. A one-page poll of >10 fans therefore drops the overflow.
            return buf.Length <= expectedLen ? buf : buf[..expectedLen];
        }

        public void Dispose()
        {
        }
    }
}
