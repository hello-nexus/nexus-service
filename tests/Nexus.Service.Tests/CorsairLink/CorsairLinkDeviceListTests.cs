using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkDeviceListTests
{
    private const byte Devices = 0x36;
    private const int ListCapacity = CorsairLinkProtocol.ReportLength - 4;

    public enum Second { Report, Timeout, ErrorStatus, ErrorStatusWithGarbage }

    /// <summary>Serves a device-list stream split across reads: the first read carries what fits, the second the rest, or the failure in <see cref="SecondRead"/>.</summary>
    private sealed class ListHub : IHidDevice
    {
        private readonly byte[] _stream;
        private readonly Dictionary<byte, byte> _handles = new();
        private readonly Queue<byte[]> _responses = new();
        private int _listReads;
        public int ListReads => _listReads;
        public Second SecondRead = Second.Report;

        public ListHub(byte[] stream)
        {
            _stream = stream;
        }

        public int VendorId => CorsairLinkProtocol.VendorId;
        public int ProductId => CorsairLinkProtocol.ProductId;
        public string Path => "fake";
        public string? Serial => null;
        public int UsagePage => CorsairLinkProtocol.VendorUsagePage;
        public int Usage => CorsairLinkProtocol.VendorUsage;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var cmd = report.Slice(CorsairLinkProtocol.HeaderSize).ToArray();
            var status = 0;
            var echo = cmd[0];
            var payload = Array.Empty<byte>();
            switch (cmd[0])
            {
                case 0x02 when cmd[1] == 0x13:
                    payload = new byte[] { 4, 1, 0x90, 0x02 };
                    break;
                case 0x02 when cmd[1] == 0x03:
                    payload = new[] { CorsairLinkProtocol.ModeSoftware };
                    break;
                case 0x09:
                    status = _handles.ContainsKey(cmd[1]) ? 0 : 3;
                    break;
                case 0x0D:
                    if (_handles.ContainsKey(cmd[1])) status = 3;
                    else
                    {
                        _handles[cmd[1]] = cmd[2];
                        if (cmd[2] == Devices) _listReads = 0;
                    }
                    break;
                case 0x05:
                    _handles.Remove(cmd[2]);
                    break;
                case 0x08:
                    if (!_handles.TryGetValue(cmd[1], out var res)) status = 3;
                    else if (res == Devices && _listReads == 1 && SecondRead != Second.Report)
                    {
                        _listReads++;
                        switch (SecondRead)
                        {
                            case Second.Timeout:
                                return true;
                            case Second.ErrorStatus:
                                status = 3;
                                break;
                            default:
                                status = 3;
                                payload = Enumerable.Repeat((byte)0xFF, ListCapacity).ToArray();
                                break;
                        }
                    }
                    else payload = ReadResource(res);
                    break;
            }
            var resp = new byte[CorsairLinkProtocol.WriteBufferLength];
            resp[1 + CorsairLinkProtocol.ResponseCommandOffset] = echo;
            resp[1 + CorsairLinkProtocol.ResponseStatusOffset] = (byte)status;
            payload.CopyTo(resp, 1 + 4);
            _responses.Enqueue(resp);
            return true;
        }

        private byte[] ReadResource(byte resource)
        {
            switch (resource)
            {
                case Devices:
                    var index = _listReads++;
                    if (index == 0) return _stream.Take(ListCapacity).ToArray();
                    return index == 1 ? _stream.Skip(ListCapacity).Take(ListCapacity).ToArray() : Array.Empty<byte>();
                case 0x17:
                    return new byte[] { 0x25, 0x00, 0 };
                case 0x21:
                    return new byte[] { 0x10, 0x00, 0 };
                default:
                    return Array.Empty<byte>();
            }
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_responses.Count == 0) return 0;
            _responses.Dequeue().CopyTo(buffer);
            return CorsairLinkProtocol.WriteBufferLength;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private static byte[] Record(int type, int model, int serialLength)
    {
        var rec = new byte[8 + serialLength];
        rec[2] = (byte)type;
        rec[3] = (byte)model;
        rec[7] = (byte)serialLength;
        Encoding.ASCII.GetBytes(new string('A', serialLength)).CopyTo(rec, 8);
        return rec;
    }

    // Data-type, channel count, then one record per slot: empty slots are the bare 8-byte header.
    private static byte[] Stream(int channels, Func<int, byte[]> slot)
    {
        var bytes = new List<byte> { 0x21, 0x00, (byte)channels };
        for (var ch = 1; ch <= channels; ch++) bytes.AddRange(slot(ch));
        return bytes.ToArray();
    }

    // Long serials with four empty slots (ch9-12) and an RX at the last channel push the records past one report.
    private static byte[] LongList(int channels = 20) => Stream(channels, ch => ch switch
    {
        >= 9 and <= 12 => new byte[8],
        _ when ch == channels => Record(15, 0, 26),
        _ => Record(2, 0, 26),
    });

    private static CorsairLinkHub Connect(ListHub device)
    {
        var hub = new CorsairLinkHub(modeCycleWaitMs: 0);
        hub.Attach(device);
        Assert.True(hub.Initialize(), hub.LastInitTrace);
        return hub;
    }

    [Fact]
    public void A_device_list_longer_than_one_report_parses_through_the_continuation_read()
    {
        using var hub = Connect(new ListHub(LongList()));

        var channels = hub.State.Devices.Select(d => d.Channel).ToArray();
        Assert.Equal(16, channels.Length);
        Assert.Equal(new[] { 19, 20 }, channels.TakeLast(2));
        Assert.Equal(15 * 18 + 8, hub.State.Devices.Sum(d => d.LedCount));
        Assert.Equal(0, hub.TruncationWarnings);
    }

    [Theory]
    [InlineData(Second.Timeout, 21)]
    [InlineData(Second.ErrorStatus, 22)]
    [InlineData(Second.ErrorStatusWithGarbage, 23)]
    public void A_continuation_that_is_not_accepted_leaves_the_list_truncated_and_warns(Second second, int channels)
    {
        var device = new ListHub(LongList(channels)) { SecondRead = second };
        using var hub = Connect(device);

        Assert.DoesNotContain(hub.State.Devices, d => d.Channel >= 19);
        Assert.Equal(1, hub.TruncationWarnings);
    }

    [Fact]
    public void A_continuation_that_fails_on_one_poll_keeps_the_previous_topology_and_warns_once()
    {
        var device = new ListHub(LongList(24));
        using var hub = Connect(device);
        var before = hub.State.Devices.Select(d => d.Channel).ToArray();

        device.SecondRead = Second.Timeout;
        Assert.True(hub.Poll());
        Assert.Equal(before, hub.State.Devices.Select(d => d.Channel));

        device.SecondRead = Second.Report;
        Assert.True(hub.Poll());
        device.SecondRead = Second.Timeout;
        Assert.True(hub.Poll());

        Assert.Equal(before, hub.State.Devices.Select(d => d.Channel));
        Assert.Equal(1, hub.TruncationWarnings);
    }

    [Fact]
    public void A_single_report_list_is_unchanged()
    {
        var stream = Stream(3, _ => Record(2, 0, 26));
        var device = new ListHub(stream);
        using var hub = Connect(device);

        Assert.Equal(new[] { 1, 2, 3 }, hub.State.Devices.Select(d => d.Channel));
        Assert.Equal(1, device.ListReads);
    }

    [Fact]
    public void ParseDevices_reports_where_a_cut_off_list_stopped()
    {
        var stream = LongList().Take(ListCapacity).ToArray();
        var resp = new byte[4].Concat(stream).ToArray();

        var devices = CorsairLinkProtocol.ParseDevices(resp, out var info);

        Assert.True(info.Truncated);
        Assert.Equal(20, info.Channels);
        Assert.Equal(info.LastChannel, devices.Count == 0 ? 0 : devices[^1].Channel);
        Assert.True(info.LastChannel < 20);
    }
}
