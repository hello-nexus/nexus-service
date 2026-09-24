using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Pins the stream's bucket rotation. Ping-pong between two buckets rewrites the bucket
/// that just left the screen while the panel is still reading it, which shows as a band
/// of decode garbage across the bottom rows (bench-measured on the Elite V2). Three
/// buckets give every bucket a full frame off before it is reused.
/// </summary>
public class KrakenStreamBucketTests
{
    // No lighting channels, so Connect skips the accessory table and the fake only has to
    // ack what it is asked.
    private static readonly KrakenModel EliteNoRgb = KrakenModel.Find(0x300C)!;

    [Fact]
    public void Stream_rotates_three_buckets_and_never_reuses_the_one_just_displayed()
    {
        var hid = new AckingHidDevice();
        var lcd = new RecordingLcdTransport();
        var hub = new KrakenHub(new FixedLcdFactory(lcd));
        hub.Attach(hid, EliteNoRgb, KrakenProtocol.ReportLength);
        Assert.True(hub.Connect());
        Assert.True(hub.HasLcd);

        var frame = new byte[EliteNoRgb.LcdFrameBytes];
        for (int i = 0; i < 7; i++)
        {
            Assert.True(hub.PushStreamFrame(frame), $"push {i}");
        }

        var setups = hid.Writes.Where(w => w[0] == 0x32 && w[1] == 0x01).Select(w => (Index: w[2], StartPage: w[4] | (w[5] << 8))).ToList();
        var pages = KrakenProtocol.PagesFor(EliteNoRgb.LcdFrameBytes);
        Assert.Equal(new[] { (0, 0), (1, pages), (2, 2 * pages) }, setups.Select(s => ((int)s.Index, s.StartPage)));

        var starts = hid.Writes.Where(w => w[0] == 0x36 && w[1] == 0x01).Select(w => (int)w[2]).ToList();
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2, 0 }, starts);

        var activations = hid.Writes
            .Where(w => w[0] == 0x38 && w[1] == 0x01 && w[2] == (byte)KrakenDisplayMode.Bucket)
            .Select(w => (int)w[3]).ToList();
        Assert.Equal(starts, activations);

        // The bucket written by push k is not the one push k-2 put on screen, which is
        // exactly what ping-pong would do.
        for (int k = 2; k < starts.Count; k++)
        {
            Assert.NotEqual(activations[k - 2], starts[k]);
        }
        Assert.Equal(7, lcd.Frames);
    }

    /// <summary>Answers every command with its ack report: id+1, same sub-command, [14]=1.</summary>
    private sealed class AckingHidDevice : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1E71;
        public int ProductId => 0x300C;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }

    private sealed class RecordingLcdTransport : IKrakenLcdTransport
    {
        // A header write starts a frame; the pixel chunks that follow belong to it.
        public int Frames { get; private set; }
        public bool Write(ReadOnlySpan<byte> data)
        {
            if (data.Length == 20) Frames++;
            return true;
        }
        public void Dispose() { }
    }

    private sealed class FixedLcdFactory : IKrakenLcdTransportFactory
    {
        private readonly IKrakenLcdTransport _lcd;
        public FixedLcdFactory(IKrakenLcdTransport lcd) { _lcd = lcd; }
        public IKrakenLcdTransport? Open(string? serial) => _lcd;
    }
}
