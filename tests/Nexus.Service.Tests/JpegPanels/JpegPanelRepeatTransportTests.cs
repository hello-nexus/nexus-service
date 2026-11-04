using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

public sealed class JpegPanelRepeatTransportTests
{
    private const int RepeatMs = 1000;

    private sealed class Fixture : IDisposable
    {
        public readonly JpegPanelHub Hub = new(JpegPanelModel.CorsairCapellix);
        public readonly Device Hid = new();
        public readonly JpegPanelStreamTransport Transport;
        public long Now;

        public Fixture()
        {
            Hub.Attach(Hid);
            Transport = new JpegPanelStreamTransport(Hub, "serial") { Clock = () => Now };
            Hid.Writes.Clear();
            var frame = new byte[JpegPanelModel.CorsairCapellix.Width * JpegPanelModel.CorsairCapellix.Height * 4];
            Transport.Write(frame);
            Hid.Writes.Clear();
        }

        public void Dispose()
        {
            Transport.Dispose();
            Hub.Dispose();
        }
    }

    [Fact]
    public void A_due_repeat_resends_the_last_frame()
    {
        using var f = new Fixture();
        f.Now = RepeatMs;

        f.Transport.RepeatLast(RepeatMs);

        Assert.NotEmpty(f.Hid.Writes);
    }

    [Fact]
    public void No_repeat_before_the_idle_window()
    {
        using var f = new Fixture();
        f.Now = RepeatMs - 1;

        f.Transport.RepeatLast(RepeatMs);

        Assert.Empty(f.Hid.Writes);
    }

    [Fact]
    public void A_closed_gate_blocks_the_repeat()
    {
        using var f = new Fixture();
        var allowed = false;
        f.Transport.BindRepeatGate(() => allowed);
        f.Now = RepeatMs;

        f.Transport.RepeatLast(RepeatMs);
        Assert.Empty(f.Hid.Writes);

        allowed = true;
        f.Transport.RepeatLast(RepeatMs);
        Assert.NotEmpty(f.Hid.Writes);
    }

    [Fact]
    public void A_repeat_during_a_live_write_never_lands_after_the_new_frame()
    {
        var control = LiveWrites(repeatMidWrite: false);
        var raced = LiveWrites(repeatMidWrite: true);

        Assert.NotEmpty(control);
        Assert.Equal(control.Count, raced.Count);
        for (var i = 0; i < control.Count; i++)
        {
            Assert.Equal(control[i], raced[i]);
        }
    }

    // Sends a black frame, then a white one; with repeatMidWrite the other thread tries a due repeat while the white frame is being written.
    private static List<byte[]> LiveWrites(bool repeatMidWrite)
    {
        using var f = new Fixture();
        var model = JpegPanelModel.CorsairCapellix;
        var white = new byte[model.Width * model.Height * 4];
        Array.Fill(white, (byte)255);
        f.Now = RepeatMs;
        if (repeatMidWrite)
        {
            // A dedicated thread: Task.Wait may inline the work on this thread, where the lock is reentrant.
            f.Hid.OnFirstWrite = () =>
            {
                var other = new Thread(() => f.Transport.RepeatLast(RepeatMs));
                other.Start();
                Assert.True(other.Join(TimeSpan.FromSeconds(5)), "repeat blocked on the hub lock");
            };
        }

        f.Transport.Write(white);

        return f.Hid.Writes;
    }

    [Fact]
    public void No_repeat_after_dispose()
    {
        using var f = new Fixture();
        f.Now = RepeatMs;
        f.Transport.Dispose();

        f.Transport.RepeatLast(RepeatMs);

        Assert.Empty(f.Hid.Writes);
    }

    private sealed class Device : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x1B1C;
        public int ProductId => 0x0C39;
        public string Path => "/dev/fake-capellix";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public Action? OnFirstWrite { get; set; }

        public bool Write(ReadOnlySpan<byte> report)
        {
            var first = OnFirstWrite;
            OnFirstWrite = null;
            first?.Invoke();
            Writes.Add(report.ToArray());
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose()
        {
        }
    }
}
