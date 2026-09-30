using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Every handle on the keeb's vendor collection receives every input report,
/// so the knob poll's settings replies queue on the hub's handle. A table read
/// that took them as layer pages captured a garbage "pristine" table, and
/// writing it back wiped the keyboard's assignments.
/// </summary>
public class KeebHubStaleInputTests
{
    // Answers each read feature by queueing the reply pages, like the device.
    private sealed class QueueingDevice : IHidDevice
    {
        public readonly Queue<byte[]> Input = new();
        public int SetFeatureCalls;

        public int VendorId => KeebProtocol.VendorId;
        public int ProductId => KeebProtocol.ProductId;
        public string Path => "fake-keeb";
        public string? Serial => "TESTKEEB";
        public int UsagePage => KeebProtocol.VendorUsagePage;
        public int Usage => KeebProtocol.VendorUsage;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Interlocked.Increment(ref SetFeatureCalls);
            var r = report.ToArray();
            if (r.SequenceEqual(KeebProtocol.LayerFeature(KeebProtocol.Read, 0, 0)))
                for (var p = 0; p < KeebLayerCodec.PageCount; p++) Input.Enqueue(Page((byte)(0xA0 + p)));
            else if (r.SequenceEqual(KeebProtocol.SettingsReadFeature))
                Input.Enqueue(Page(0x5E));
            return true;
        }

        public bool GetFeature(Span<byte> buffer) => true;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Input.Count == 0) return 0;
            var page = Input.Dequeue();
            page.CopyTo(buffer);
            return page.Length;
        }

        public void Dispose() { }

        public static byte[] Page(byte fill)
        {
            var b = new byte[KeebLayout.PageSize];
            Array.Fill(b, fill, 1, KeebLayout.PageDataSize);
            return b;
        }
    }

    private sealed class Enumerator : IHidEnumerator
    {
        public QueueingDevice Device { get; } = new();

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => new[]
        {
            new HidDeviceInfo
            {
                Path = "fake-keeb",
                VendorId = vendorId,
                ProductId = productId,
                Serial = "TESTKEEB",
                UsagePage = KeebProtocol.VendorUsagePage,
                Usage = KeebProtocol.VendorUsage,
                FeatureReportByteLength = KeebProtocol.FeatureReportSize,
                OutputReportByteLength = KeebLayout.PageSize,
            },
        };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Find(KeebProtocol.VendorId, KeebProtocol.ProductId);

        public IHidDevice? Open(string path, bool forInput = false) => Device;
    }

    [Fact]
    public void Layer_read_discards_queued_settings_replies()
    {
        var hid = new Enumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());
        for (var i = 0; i < 10; i++) hid.Device.Input.Enqueue(QueueingDevice.Page(0x5E));

        var raw = hub.ReadLayerRaw(0, 0);

        Assert.NotNull(raw);
        for (var p = 0; p < KeebLayerCodec.PageCount; p++)
            Assert.Equal((byte)(0xA0 + p), raw![p * KeebLayout.PageSize + 1]);
    }

    [Fact]
    public void Settings_read_discards_queued_table_pages()
    {
        var hid = new Enumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());
        hid.Device.Input.Enqueue(QueueingDevice.Page(0xA3));

        var raw = hub.ReadSettings();

        Assert.NotNull(raw);
        Assert.Equal(0x5E, raw![1]);
    }

    [Fact]
    public void Knob_poll_skips_while_an_onboard_transaction_holds_the_lock()
    {
        var hid = new Enumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());
        var store = new Nexus.Service.Tests.InMemoryConfigStore();
        var applier = new KeebSettingsApplier(hub, store, new Nexus.Service.Sockets.MultiplexHub(), hid);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (hub.OnboardLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        try
        {
            applier.PollKnobToGlobal(readByte: true);
            Assert.Equal(0, hid.Device.SetFeatureCalls);
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        applier.PollKnobToGlobal(readByte: true);
        Assert.Equal(1, hid.Device.SetFeatureCalls);
    }
}
