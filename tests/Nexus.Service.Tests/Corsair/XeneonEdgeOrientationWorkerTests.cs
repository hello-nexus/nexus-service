using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Corsair.XeneonEdge;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>Records writes and serves queued reads, mirroring MockStreamDeckHidDevice.</summary>
internal sealed class MockXeneonHidDevice : IHidDevice
{
    public List<byte[]> Writes { get; } = new();
    public Queue<byte[]> PendingReads { get; } = new();
    public bool FailNextRead { get; set; }
    public bool Disposed { get; private set; }

    public int VendorId { get; set; } = XeneonEdgeProtocol.VendorId;
    public int ProductId { get; set; } = XeneonEdgeProtocol.ProductId;
    public string Path { get; set; } = "mock-xeneon-path";
    public string? Serial { get; set; }
    public int UsagePage { get; set; } = XeneonEdgeProtocol.UsagePage;
    public int Usage { get; set; } = XeneonEdgeProtocol.Usage;

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
        if (FailNextRead) return -1;
        if (PendingReads.Count == 0) return 0;
        var next = PendingReads.Dequeue();
        var n = Math.Min(next.Length, buffer.Length);
        next.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeXeneonHidEnumerator : IHidEnumerator
{
    public List<HidDeviceInfo> Infos { get; } = new();
    public Dictionary<string, IHidDevice> DevicesByPath { get; } = new();

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
        Infos.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

    public IReadOnlyList<HidDeviceInfo> FindAll() => Infos;

    public IHidDevice? Open(string path, bool forInput = false) =>
        DevicesByPath.TryGetValue(path, out var dev) ? dev : null;
}

internal sealed class FakeDisplayOrientationProvider : IDisplayOrientationProvider
{
    public List<(string DisplayId, string Orientation, string CoverColorHex)> Calls { get; } = new();
    public bool NextOk { get; set; } = true;
    public string NextError { get; set; } = "";
    /// <summary>When set, SetDisplayOrientation blocks on this until released - lets a test pin the apply queue mid-flight, standing in for the real helper RPC's latency.</summary>
    public ManualResetEventSlim? BlockUntil { get; set; }
    /// <summary>Signaled the instant a call enters SetDisplayOrientation, before it waits on BlockUntil - lets a test know the call has actually started (Task.Run dispatch timing is otherwise unobservable).</summary>
    public ManualResetEventSlim? Entered { get; set; }

    public (bool Ok, string Error) SetY70Orientation(string orientation) => (true, "");

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
    {
        Entered?.Set();
        BlockUntil?.Wait();
        Calls.Add((displayId, orientation, coverColorHex));
        return (NextOk, NextError);
    }
}

/// <summary>
/// Exercises XeneonEdgeOrientationWorker.Tick() (the synchronous per-cycle
/// step the background loop calls repeatedly) against a fake HID layer, the
/// same pattern StreamDeckConnectionWorkerTests uses for its worker.
/// </summary>
public sealed class XeneonEdgeOrientationWorkerTests
{
    private const string DisplayId = "DISP-XENEON-1";

    private sealed record Fixtures(
        FakeXeneonHidEnumerator Hid,
        HardwarePresence Presence,
        PanelDeviceRegistry Registry,
        FakeDisplayOrientationProvider Orientation,
        MultiplexHub Hub);

    private static Fixtures NewFixtures(bool devicePresent)
    {
        var hid = new FakeXeneonHidEnumerator();
        var usb = devicePresent
            ? new[] { new UsbDeviceEntry { VendorId = XeneonEdgeProtocol.VendorId, ProductId = XeneonEdgeProtocol.ProductId } }
            : Array.Empty<UsbDeviceEntry>();
        var presence = new HardwarePresence(new FixedUsbEnumerator(usb));
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        return new Fixtures(hid, presence, registry, new FakeDisplayOrientationProvider(), new MultiplexHub());
    }

    private static XeneonEdgeOrientationWorker NewWorker(Fixtures f) =>
        new(f.Hid, f.Presence, f.Registry, f.Orientation, f.Hub);

    private static void AddDevice(FakeXeneonHidEnumerator hid, MockXeneonHidDevice device)
    {
        hid.Infos.Add(new HidDeviceInfo
        {
            VendorId = XeneonEdgeProtocol.VendorId,
            ProductId = XeneonEdgeProtocol.ProductId,
            Path = device.Path,
            UsagePage = XeneonEdgeProtocol.UsagePage,
            Usage = XeneonEdgeProtocol.Usage,
        });
        hid.DevicesByPath[device.Path] = device;
    }

    private static PanelDeviceRecord PromoteXeneonEdge(PanelDeviceRegistry registry, bool? autoOrient = null)
    {
        var caps = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor, Family = KnownPanelDisplays.XeneonEdgeFamily };
        var (record, _) = registry.AllocateForDisplay(DisplayId, "Xeneon Edge", caps);
        if (autoOrient.HasValue)
        {
            registry.Patch(record.Id, new PanelDevicePatch { AutoOrient = autoOrient.Value });
        }
        return record;
    }

    private static byte[] OrientationReport(byte code)
    {
        var buf = new byte[64];
        buf[0] = 0x01;
        buf[1] = 0x11;
        buf[5] = 0x02;
        buf[7] = code;
        return buf;
    }

    [Fact]
    public void Tick_NoUsbPresence_SkipsHidEntirely()
    {
        var f = NewFixtures(devicePresent: false);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);

        var active = worker.Tick();

        Assert.False(active);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void Tick_DevicePresent_OpensAndArmsWithTheOrientationQuery()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);

        var active = worker.Tick();

        Assert.True(active);
        Assert.Single(device.Writes);
        Assert.Equal(XeneonEdgeProtocol.BuildOrientationQuery(), device.Writes[0]);
    }

    [Fact]
    public async Task Tick_OrientationChange_AppliesTheMappedOrientationAndPersistsIt()
    {
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        device.PendingReads.Enqueue(OrientationReport(0)); // -> Landscape
        worker.Tick();

        // The apply runs off the reader thread (a background queue), so it
        // may not have landed the instant Tick() returns.
        await WaitForOrientationCalls(f.Orientation, 1);

        var call = Assert.Single(f.Orientation.Calls);
        Assert.Equal(DisplayId, call.DisplayId);
        Assert.Equal(DisplayOrientations.Landscape, call.Orientation);
        Assert.Equal(DisplayOrientations.Landscape, f.Registry.FindByDisplayId(DisplayId)!.Capabilities!.Orientation);
    }

    [Fact]
    public async Task Tick_OrientationChange_ThreadsThePanelsBackgroundColourAsTheCoverColour()
    {
        var f = NewFixtures(devicePresent: true);
        var record = PromoteXeneonEdge(f.Registry);
        f.Registry.Patch(record.Id, new PanelDevicePatch { BackgroundColor = "#082617" });
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.PendingReads.Enqueue(OrientationReport(0));
        worker.Tick();

        // The apply runs off the reader thread (a background queue), so it
        // may not have landed the instant Tick() returns.
        await WaitForOrientationCalls(f.Orientation, 1);

        var call = Assert.Single(f.Orientation.Calls);
        Assert.Equal("#082617", call.CoverColorHex);
    }

    [Fact]
    public async Task Tick_OrientationChange_NoBackgroundColourSet_ThreadsAnEmptyCoverColour()
    {
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.PendingReads.Enqueue(OrientationReport(0));
        worker.Tick();

        // The apply runs off the reader thread (a background queue), so it
        // may not have landed the instant Tick() returns.
        await WaitForOrientationCalls(f.Orientation, 1);

        var call = Assert.Single(f.Orientation.Calls);
        Assert.Equal("", call.CoverColorHex);
    }

    [Fact]
    public void Tick_AutoOrientFalse_IgnoresTheSensorChange()
    {
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry, autoOrient: false);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.PendingReads.Enqueue(OrientationReport(1));
        worker.Tick();

        Assert.Empty(f.Orientation.Calls);
    }

    [Fact]
    public void Tick_NoMatchingPanelRecord_DoesNotApply()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.PendingReads.Enqueue(OrientationReport(1));
        worker.Tick();

        Assert.Empty(f.Orientation.Calls);
    }

    [Fact]
    public void Tick_DeviceGone_ClosesTheHandleAndBacksOff()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.FailNextRead = true;
        var active = worker.Tick();

        Assert.False(active);
        Assert.True(device.Disposed);
    }

    [Fact]
    public void Tick_IdleRead_ReturnsActiveWithoutApplying()
    {
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        var active = worker.Tick(); // no pending reads: idle

        Assert.True(active);
        Assert.Empty(f.Orientation.Calls);
    }

    private static byte[] SettingsBlockReport(int brightness, int backlight, int contrast, int red, int green, int blue)
    {
        var buf = new byte[64];
        buf[0] = 0x01;
        buf[1] = 0x0e;
        buf[5] = 0x1d;
        buf[6] = (byte)brightness;
        buf[7] = (byte)backlight;
        buf[8] = (byte)contrast;
        buf[9] = (byte)red;
        buf[10] = (byte)green;
        buf[11] = (byte)blue;
        return buf;
    }

    private static byte[] SetAckReport(byte group, byte value)
    {
        var buf = new byte[64];
        buf[0] = 0x01;
        buf[1] = 0x0f;
        buf[5] = 0x02;
        buf[6] = group;
        buf[7] = 0x01;
        buf[8] = value;
        return buf;
    }

    [Fact]
    public async Task ReadSettingsAsync_NoDeviceOpen_ReturnsNullImmediately()
    {
        var f = NewFixtures(devicePresent: true);
        var worker = NewWorker(f);
        // No Tick() yet: the worker never opened a reader.

        var result = await worker.ReadSettingsAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadSettingsAsync_HappyPath_ReturnsBlockParsedFromTheDevicesReply()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        var task = worker.ReadSettingsAsync(CancellationToken.None);
        device.PendingReads.Enqueue(SettingsBlockReport(brightness: 50, backlight: 100, contrast: 50, red: 151, green: 127, blue: 139));
        worker.Tick(); // delivers the reply to the pending request

        var result = await task;

        Assert.NotNull(result);
        Assert.Equal(50, result.Value.Brightness);
        Assert.Equal(100, result.Value.Backlight);
        Assert.Equal(50, result.Value.Contrast);
        Assert.Equal(151, result.Value.Red);
        Assert.Equal(127, result.Value.Green);
        Assert.Equal(139, result.Value.Blue);
        // The settings query is the second write - the first is the
        // orientation arm every OpenAndArm issues on open.
        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(XeneonEdgeProtocol.BuildSettingsQuery(), device.Writes[1]);
    }

    [Fact]
    public async Task SetControlAsync_HappyPath_ClampsAndReturnsTheAckedValue()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        var task = worker.SetControlAsync(XeneonEdgeControl.Brightness, 500, CancellationToken.None); // out of range, clamps to 100
        device.PendingReads.Enqueue(SetAckReport(group: 0x02, value: 100));
        worker.Tick();

        var result = await task;

        Assert.Equal(100, result);
        var sent = device.Writes[1];
        Assert.Equal(0x02, sent[6]); // brightness group
        Assert.Equal(0x02, sent[7]); // brightness item
        Assert.Equal(100, sent[8]); // clamped value
    }

    [Fact]
    public async Task SetControlAsync_AckForADifferentGroup_ReturnsNull()
    {
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        var task = worker.SetControlAsync(XeneonEdgeControl.Brightness, 50, CancellationToken.None);
        device.PendingReads.Enqueue(SetAckReport(group: 0x03, value: 50)); // wrong group
        worker.Tick();

        Assert.Null(await task);
    }


    /// <summary>
    /// Waits until the worker has issued <paramref name="expected"/> writes.
    /// Polls rather than sleeps a fixed span: the write lands on a pool
    /// continuation, so there is no signal to await from the test side.
    /// </summary>
    private static async Task WaitForWriteCount(MockXeneonHidDevice device, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (device.Writes.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"expected {expected} writes, saw {device.Writes.Count}");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Waits until the orientation provider has recorded <paramref name="expected"/>
    /// calls. Orientation applies run on a background queue (off the reader
    /// thread), so Tick() returning is not proof the apply has landed yet.
    /// </summary>
    private static async Task WaitForOrientationCalls(FakeDisplayOrientationProvider orientation, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (orientation.Calls.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"expected {expected} orientation call(s), saw {orientation.Calls.Count}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task RestoreDefaultsAsync_WritesEveryControlAtItsFactoryValue()
    {
        // The panel's own 0xff command only restores RGB, so a full restore
        // writes all six controls individually.
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        var task = worker.RestoreDefaultsAsync(CancellationToken.None);
        for (var i = 0; i < XeneonEdgeDefaults.All.Length; i++)
        {
            var (control, value) = XeneonEdgeDefaults.All[i];
            var coords = XeneonEdgeControls.Coords[control];
            // Each control is awaited in turn and the continuation that issues
            // the next write runs on the pool, so wait for that write to land
            // before acking it: acking early leaves no pending request and the
            // reply is dropped.
            await WaitForWriteCount(device, i + 2);
            device.PendingReads.Enqueue(SetAckReport(group: coords.Group, value: (byte)value));
            worker.Tick();
        }

        // The restore verifies itself against a settings read: an ack only says
        // the panel received the write, so serve a block showing every control
        // landed on its factory value.
        await WaitForWriteCount(device, XeneonEdgeDefaults.All.Length + 2);
        device.PendingReads.Enqueue(SettingsBlockReport(
            brightness: XeneonEdgeDefaults.Brightness, backlight: XeneonEdgeDefaults.Backlight,
            contrast: XeneonEdgeDefaults.Contrast, red: XeneonEdgeDefaults.Red,
            green: XeneonEdgeDefaults.Green, blue: XeneonEdgeDefaults.Blue));
        worker.Tick();

        Assert.True(await task);
        // Writes[0] is the arm; the six restores follow in XeneonEdgeDefaults.All order.
        var sent = device.Writes.Skip(1).Take(XeneonEdgeDefaults.All.Length).ToList();
        Assert.Equal(XeneonEdgeDefaults.All.Length, sent.Count);
        for (var i = 0; i < XeneonEdgeDefaults.All.Length; i++)
        {
            var (control, value) = XeneonEdgeDefaults.All[i];
            var coords = XeneonEdgeControls.Coords[control];
            Assert.Equal(coords.Group, sent[i][6]);
            Assert.Equal(coords.Item, sent[i][7]);
            Assert.Equal((byte)value, sent[i][8]);
        }
    }

    [Fact]
    public async Task Tick_OrientationReport_StillAppliesWhileNoSettingsRequestIsPending()
    {
        // Guards against the settings reply-demux swallowing unrelated
        // reports: an orientation push must still apply normally.
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick();

        device.PendingReads.Enqueue(OrientationReport(0));
        worker.Tick();

        await WaitForOrientationCalls(f.Orientation, 1);
        Assert.Single(f.Orientation.Calls);
    }

    [Fact]
    public async Task SetControlAsync_StaleReplyAfterTimeout_DoesNotSatisfyTheNextRequest()
    {
        // Brightness/Contrast share group 0x02 and the ack does not echo
        // item, so a stale brightness ack landing on a subsequent contrast
        // request is otherwise indistinguishable from a real contrast ack.
        var f = NewFixtures(devicePresent: true);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        var first = worker.SetControlAsync(XeneonEdgeControl.Brightness, 10, CancellationToken.None);
        await WaitForWriteCount(device, 2); // arm + brightness write
        Assert.Null(await first); // no reply is ever queued for it: it times out

        // The next request cannot arm until the timed-out one's drain
        // window clears, so this call blocks behind it.
        var second = worker.SetControlAsync(XeneonEdgeControl.Contrast, 77, CancellationToken.None);

        // Brightness's device reply finally shows up, late - it must land
        // on the abandoned brightness request, not the pending contrast one.
        device.PendingReads.Enqueue(SetAckReport(group: 0x02, value: 10));
        worker.Tick();

        await WaitForWriteCount(device, 3); // the drain clears and contrast's write goes out
        device.PendingReads.Enqueue(SetAckReport(group: 0x02, value: 77));
        worker.Tick();

        Assert.Equal(77, await second);
    }

    [Fact]
    public async Task Tick_OrientationReportsWhileApplyInFlight_CoalescesToTheLatestCodeOnly()
    {
        // Applies must not run concurrently, and only the newest code
        // survives when reports queue up behind an in-flight apply (the
        // panel reports absolute state, not deltas).
        var f = NewFixtures(devicePresent: true);
        PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        f.Orientation.BlockUntil = gate;
        f.Orientation.Entered = entered;
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        device.PendingReads.Enqueue(OrientationReport(0)); // -> Landscape
        worker.Tick();
        // Task.Run's dispatch timing is otherwise unobservable: without this,
        // the two reports below could both coalesce before the first apply
        // ever reaches the provider, applying only the last of the three.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // Two more reports land while the first apply is still in flight;
        // only the last (PortraitFlipped) should ever reach the provider.
        device.PendingReads.Enqueue(OrientationReport(1)); // -> Portrait
        worker.Tick();
        device.PendingReads.Enqueue(OrientationReport(3)); // -> PortraitFlipped
        worker.Tick();

        gate.Set();

        await WaitForOrientationCalls(f.Orientation, 2);
        Assert.Equal(2, f.Orientation.Calls.Count);
        Assert.Equal(DisplayOrientations.Landscape, f.Orientation.Calls[0].Orientation);
        Assert.Equal(DisplayOrientations.PortraitFlipped, f.Orientation.Calls[1].Orientation);
    }

    [Fact]
    public async Task DisplayRecordReady_ReplaysAnUnappliedOrientationOnceTheRecordIsReady()
    {
        // Mirrors a hot-plug where the panel record only appears after
        // auto-promotion, with the helper already connected - its Connected
        // event never fires again to trigger a replay, so PanelDeviceRegistry
        // must be the one that does.
        var f = NewFixtures(devicePresent: true);
        var record = PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        f.Orientation.NextOk = false;
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        device.PendingReads.Enqueue(OrientationReport(1)); // -> Portrait
        worker.Tick();
        await WaitForOrientationCalls(f.Orientation, 1); // first attempt fails

        f.Orientation.NextOk = true;
        // Re-enabling the record fires PanelDeviceRegistry.DisplayRecordReady,
        // the same signal a fresh auto-promotion raises.
        f.Registry.DisablePanelForDisplay(DisplayId);
        f.Registry.AllocateForDisplay(DisplayId, "Xeneon Edge", record.Capabilities);

        await WaitForOrientationCalls(f.Orientation, 2);
        Assert.Equal(DisplayOrientations.Portrait, f.Orientation.Calls[1].Orientation);
        Assert.Equal(DisplayOrientations.Portrait, f.Registry.FindByDisplayId(DisplayId)!.Capabilities!.Orientation);
    }

    [Fact]
    public async Task ReplayWhenHelperServes_DoesNotReapplyAStaleCodeOverARealRotationThatSupersededIt()
    {
        // The replay loop must re-read the unapplied code on every attempt
        // instead of closing over the one captured when it started: a real
        // rotation that lands (and applies successfully) during the retry
        // gap must win, not get overwritten by a stale replay of the code
        // it just superseded.
        var f = NewFixtures(devicePresent: true);
        var record = PromoteXeneonEdge(f.Registry);
        var device = new MockXeneonHidDevice();
        AddDevice(f.Hid, device);
        f.Orientation.NextOk = false; // every apply fails for now
        var worker = NewWorker(f);
        worker.Tick(); // opens + arms

        // First attempt: fails, _unapplied = Landscape (code 0).
        device.PendingReads.Enqueue(OrientationReport(0));
        worker.Tick();
        await WaitForOrientationCalls(f.Orientation, 1);

        // Trigger the replay loop via a record-ready signal (re-enable, the
        // same mechanism a hot-plug's auto-promotion fires).
        f.Registry.DisablePanelForDisplay(DisplayId);
        f.Registry.AllocateForDisplay(DisplayId, "Xeneon Edge", record.Capabilities);

        // Replay's own first attempt also fails (still _unapplied = 0),
        // then it sleeps HelperReplayGapMs before its second attempt.
        await WaitForOrientationCalls(f.Orientation, 2);

        // A real rotation lands and succeeds during that gap - it must win.
        f.Orientation.NextOk = true;
        device.PendingReads.Enqueue(OrientationReport(1)); // -> Portrait
        worker.Tick();
        await WaitForOrientationCalls(f.Orientation, 3);
        Assert.Equal(DisplayOrientations.Portrait, f.Orientation.Calls[2].Orientation);

        // Let the replay's gap (HelperReplayGapMs) fully elapse so its
        // second attempt (or lack of one) settles before asserting.
        await Task.Delay(600);

        // The replay must have re-read _unapplied (now cleared) and
        // stopped, not reapplied its stale captured Landscape over Portrait.
        Assert.Equal(3, f.Orientation.Calls.Count);
        Assert.Equal(DisplayOrientations.Portrait, f.Registry.FindByDisplayId(DisplayId)!.Capabilities!.Orientation);
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
