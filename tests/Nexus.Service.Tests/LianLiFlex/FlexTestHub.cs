using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Tests.LianLiFlex;

internal sealed class FakeFlexTransport : IFlexReceiverTransport
{
    private readonly Queue<byte[]> _replies = new();
    public List<byte[]> Written { get; } = new();
    public byte[] Info { get; set; }
    public int Version { get; set; } = 22;
    public bool FailWrites { get; set; }

    public FakeFlexTransport(string mac = "6D1007F517E1", int fansType = 0x33, byte rxType = FlexProtocol.UnboundRxType, int rpm = 661, byte pwm = 0)
    {
        Info = InfoFrame(mac, fansType, rxType, rpm, pwm);
    }

    public bool IsOpen => true;

    public bool Write(byte[] frame)
    {
        if (FailWrites)
        {
            return false;
        }
        Written.Add((byte[])frame.Clone());
        if (frame[0] == FlexProtocol.CmdGetInfo)
        {
            _replies.Enqueue(Info);
        }
        else if (frame[0] == FlexProtocol.CmdGetVer)
        {
            var r = (byte[])Info.Clone();
            r[0] = FlexProtocol.CmdGetVer;
            r[1] = (byte)(Version >> 8);
            r[2] = (byte)Version;
            _replies.Enqueue(r);
        }
        return true;
    }

    public byte[]? Read(int timeoutMs) => _replies.Count > 0 ? _replies.Dequeue() : null;

    public void DiscardReplies() => _replies.Clear();

    public void Dispose()
    {
    }

    public IEnumerable<byte[]> Of(byte cmd) => Written.Where(f => f[0] == cmd);

    public static byte[] InfoFrame(string mac, int fansType, byte rxType, int rpm, byte pwm)
    {
        var f = new byte[64];
        f[0] = FlexProtocol.CmdGetInfo;
        Convert.FromHexString(mac).CopyTo(f, 1);
        f[13] = 8;
        f[14] = rxType;
        f[20] = 1;
        f[25] = (byte)fansType;
        f[29] = (byte)(0x20 | ((rpm >> 8) & 0xF));
        f[30] = (byte)rpm;
        f[37] = pwm;
        return f;
    }
}

internal sealed class FakeFlexDiscovery : IFlexDiscovery
{
    public List<FlexUnitPort> Ports { get; } = new();

    public IReadOnlyList<FlexUnitPort> Discover() => Ports.ToArray();

    public static FlexUnitPort Tl(string path = "tl-receiver") => new()
    {
        ReceiverPath = path,
        ReceiverPid = FlexProtocol.TlReceiverProductId,
        ScreenPath = "tl-screen",
        ScreenPid = FlexProtocol.TlScreenProductId,
        ScreenSerial = "5C00C251854D2B79",
    };
}

internal sealed class FlexClock
{
    public long Now { get; set; } = 1_000;

    public Func<long> Func => () => Now;
}
