using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// A real blocking fake: Read() waits on a BlockingCollection up to timeoutMs,
/// like a real overlapped interrupt-IN read, instead of returning instantly.
/// StreamDeckInputReader keeps a call to this continuously pending on a
/// background thread, so a non-blocking fake would make it busy-spin.
/// </summary>
internal sealed class BlockingFakeHidDevice : IHidDevice
{
    private readonly BlockingCollection<byte[]> _queue = new();

    public bool Disposed { get; private set; }
    public bool FailNextRead { get; set; }

    public int VendorId => 0x0FD9;
    public int ProductId => 0x0063;
    public string Path => "fake-path";
    public string? Serial => "FAKE";
    public int UsagePage => 0x0C;
    public int Usage => 0x01;

    public bool SetFeature(ReadOnlySpan<byte> report) => true;
    public bool GetFeature(Span<byte> buffer) => true;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool Write(ReadOnlySpan<byte> report) => true;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => true;

    public void Enqueue(byte[] report) => _queue.Add(report);

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (FailNextRead)
        {
            FailNextRead = false;
            return -1;
        }
        if (!_queue.TryTake(out var next, timeoutMs))
        {
            return 0;
        }
        var n = Math.Min(next.Length, buffer.Length);
        next.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Counts every Open call so tests can assert a device-gone read triggers a reopen attempt.</summary>
internal sealed class RecordingHidEnumerator : IHidEnumerator
{
    private int _openCount;
    public int OpenCount => _openCount;
    public Func<string, IHidDevice?> OpenFactory { get; set; } = _ => null;

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
    public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

    public IHidDevice? Open(string path, bool forInput = false)
    {
        Interlocked.Increment(ref _openCount);
        return OpenFactory(path);
    }
}

/// <summary>
/// Exercises StreamDeckInputReader in isolation (no StreamDeckConnectionWorker
/// involved): it opens its own handle, keeps a blocking read continuously
/// pending, decodes via StreamDeckProtocol, and reopens after a device-gone
/// read. readTimeoutMs/retryDelayMs are given small values so these tests
/// stay fast without changing the production defaults.
/// </summary>
public class StreamDeckInputReaderTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;
    private static readonly StreamDeckModel Xl = StreamDeckModels.ByProductId(0x006c)!;

    [Fact]
    public async Task QueuedGen1Reports_DecodeAndInvokeCallback_ForBothPressAndRelease()
    {
        var device = new BlockingFakeHidDevice();
        var hid = new RecordingHidEnumerator { OpenFactory = _ => device };
        var reports = new ConcurrentQueue<bool[]>();
        using var reader = new StreamDeckInputReader(
            hid, "path-1", Mini, input => reports.Enqueue(input.Keys), readTimeoutMs: 20, retryDelayMs: 20);

        device.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 }); // key 0 down
        device.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 0 up

        var seen = new List<bool[]>();
        for (var i = 0; i < 100 && seen.Count < 2; i++)
        {
            while (reports.TryDequeue(out var next))
            {
                seen.Add(next);
            }
            if (seen.Count < 2)
            {
                await Task.Delay(10);
            }
        }

        Assert.Equal(2, seen.Count);
        Assert.True(seen[0][0]);
        Assert.False(seen[1][0]);
    }

    [Fact]
    public async Task QueuedGen2Report_DecodesUsingTheGen2HeaderAndKeyOffsets()
    {
        var device = new BlockingFakeHidDevice();
        var hid = new RecordingHidEnumerator { OpenFactory = _ => device };
        var reports = new ConcurrentQueue<bool[]>();
        using var reader = new StreamDeckInputReader(
            hid, "path-xl", Xl, input => reports.Enqueue(input.Keys), readTimeoutMs: 20, retryDelayMs: 20);

        var report = new byte[4 + Xl.KeyCount];
        report[4 + 9] = 1; // key 9 pressed
        device.Enqueue(report);

        bool[]? states = null;
        for (var i = 0; i < 100 && states is null; i++)
        {
            if (!reports.TryDequeue(out states))
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(states);
        Assert.True(states![9]);
    }

    [Fact]
    public async Task DeviceGoneRead_ClosesTheHandleAndRetriesOpen()
    {
        var device = new BlockingFakeHidDevice { FailNextRead = true };
        var hid = new RecordingHidEnumerator { OpenFactory = _ => device };
        using var reader = new StreamDeckInputReader(
            hid, "path-1", Mini, _ => { }, readTimeoutMs: 20, retryDelayMs: 20);

        for (var i = 0; i < 100 && hid.OpenCount < 2; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(hid.OpenCount >= 2);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task OpenFailure_RetriesUntilTheDeviceBecomesAvailable()
    {
        var device = new BlockingFakeHidDevice();
        var attempts = 0;
        var hid = new RecordingHidEnumerator { OpenFactory = _ => Interlocked.Increment(ref attempts) < 3 ? null : device };
        var reports = new ConcurrentQueue<bool[]>();
        using var reader = new StreamDeckInputReader(
            hid, "path-1", Mini, input => reports.Enqueue(input.Keys), readTimeoutMs: 20, retryDelayMs: 20);

        for (var i = 0; i < 200 && attempts < 3; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(attempts >= 3);

        device.Enqueue(new byte[] { 0x01, 0, 0, 1, 0, 0, 0 }); // key 2 down
        bool[]? states = null;
        for (var i = 0; i < 100 && states is null; i++)
        {
            if (!reports.TryDequeue(out states))
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(states);
        Assert.True(states![2]);
    }
}
