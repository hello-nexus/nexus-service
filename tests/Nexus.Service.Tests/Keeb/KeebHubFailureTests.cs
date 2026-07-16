using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Failure-path mechanics on the hub's IO surface. An unplugged keeb whose
/// only traffic is READS (firmware animation showing - no stream writes) must
/// still tear the handle down: read timeouts drop it immediately (a late
/// response on the handle would be consumed by the next reader as stale
/// data), and failed feature-arms count toward the same threshold as writes.
/// </summary>
public class KeebHubFailureTests
{
    private sealed class ScriptedHidDevice : IHidDevice
    {
        public bool Disposed { get; private set; }
        public bool FailSetFeature { get; set; }
        public Func<int> ReadResult { get; set; } = () => 0; // 0 = timeout
        public byte[]? ReadPayload { get; set; }

        public int VendorId => KeebProtocol.VendorId;
        public int ProductId => KeebProtocol.ProductId;
        public string Path => "fake-keeb";
        public string? Serial => "TESTKEEB";
        public int UsagePage => KeebProtocol.VendorUsagePage;
        public int Usage => KeebProtocol.VendorUsage;

        public bool SetFeature(ReadOnlySpan<byte> report) => !FailSetFeature;
        public bool GetFeature(Span<byte> buffer) => true;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            var n = ReadResult();
            if (n > 0 && ReadPayload is not null)
            {
                var len = Math.Min(Math.Min(n, ReadPayload.Length), buffer.Length);
                ReadPayload.AsSpan(0, len).CopyTo(buffer);
                return len;
            }
            return n;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SingleDeviceEnumerator : IHidEnumerator
    {
        public ScriptedHidDevice Device { get; } = new();

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
    public void ReadSettings_timeout_drops_the_handle_immediately()
    {
        var hid = new SingleDeviceEnumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());

        hid.Device.ReadResult = () => 0; // settings read times out
        Assert.Null(hub.ReadSettings());
        Assert.False(hub.IsConnected);
        Assert.True(hid.Device.Disposed);
    }

    [Fact]
    public void ReadDeviceInfo_timeout_drops_the_handle_immediately()
    {
        var hid = new SingleDeviceEnumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());

        hid.Device.ReadResult = () => 0;
        Assert.False(hub.ReadDeviceInfo());
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Failed_feature_arms_count_toward_the_drop_threshold()
    {
        var hid = new SingleDeviceEnumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());

        hid.Device.FailSetFeature = true;
        for (var i = 0; i < 4; i++)
        {
            Assert.Null(hub.ReadSettings());
            // Reads that fail at the arm stage accumulate; the handle survives
            // until the threshold (5) so a transient failure doesn't churn.
            Assert.True(hub.IsConnected, $"dropped too early at attempt {i + 1}");
        }
        Assert.Null(hub.ReadSettings());
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Layer_read_timeout_mid_burst_drops_the_handle()
    {
        var hid = new SingleDeviceEnumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());

        var reads = 0;
        hid.Device.ReadPayload = new byte[KeebLayout.PageSize];
        hid.Device.ReadResult = () => ++reads <= 3 ? KeebLayout.PageSize : 0; // dies at page 4
        Assert.Null(hub.ReadLayerRaw(0, 0));
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Successful_read_resets_the_failure_counter()
    {
        var hid = new SingleDeviceEnumerator();
        using var hub = new KeebHub(hid);
        Assert.True(hub.EnsureConnected());

        hid.Device.FailSetFeature = true;
        for (var i = 0; i < 4; i++) Assert.Null(hub.ReadSettings());

        hid.Device.FailSetFeature = false;
        hid.Device.ReadPayload = new byte[KeebLayout.PageSize];
        hid.Device.ReadResult = () => KeebLayout.PageSize;
        Assert.NotNull(hub.ReadSettings());

        // The streak reset: four more failures stay under the threshold.
        hid.Device.FailSetFeature = true;
        for (var i = 0; i < 4; i++) Assert.Null(hub.ReadSettings());
        Assert.True(hub.IsConnected);
    }
}
