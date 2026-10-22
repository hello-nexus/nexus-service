using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.DeepCool;

public class MystiqueWorkerTests
{
    [Fact]
    public async Task Pushes_live_sensors_and_applies_a_saved_change_at_once()
    {
        var store = new InMemoryConfigStore();
        var pipes = new FakeFactory();
        var worker = Build(store, pipes, present: true, gateOn: true);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => pipes.Pipe is { } p && p.Commands().Contains(MystiqueProtocol.CmdSensors));
            Assert.True(worker.IsConnected);
            Assert.DoesNotContain(MystiqueProtocol.CmdSystem, pipes.Pipe!.Commands());

            store.Update(s =>
            {
                s.Devices.DeepCoolMystique.Brightness = 30;
                s.Devices.DeepCoolMystique.DeviceEdited = true;
            });
            worker.RequestApply();

            await WaitFor(() => pipes.Pipe!.Commands().Contains(MystiqueProtocol.CmdSystem));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_upload_streams_between_ticks_with_no_live_traffic_inside_it()
    {
        var store = new InMemoryConfigStore();
        var pipes = new FakeFactory();
        var worker = Build(store, pipes, present: true, gateOn: true);
        var package = MystiqueMedia.BuildPicture(new byte[5000], "2101b6e6277841daa821e8d79a5c528b");

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => worker.IsConnected);
            Assert.True(await worker.UploadAsync(package, () => DeepCoolMystiqueSettings.SourceImages, CancellationToken.None));

            var writes = pipes.Pipe!.Writes();
            int start = writes.FindIndex(w => w.Length == MystiqueProtocol.FrameLength && w[2] == MystiqueProtocol.CmdUploadStart);
            int finish = writes.FindIndex(w => w.Length == MystiqueMedia.FinishLength);
            Assert.InRange(start, 0, finish - 1);
            Assert.Equal(package.Frames[0].Length, writes.Skip(start + 1).Take(finish - start - 1).Sum(w => w.Length));
            Assert.Equal(MystiqueProtocol.CmdMediaSource, writes[finish + 1][2]);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_caller_giving_up_after_the_stream_started_still_gets_the_real_result()
    {
        var store = new InMemoryConfigStore();
        var pipes = new FakeFactory();
        var worker = Build(store, pipes, present: true, gateOn: true);
        var package = MystiqueMedia.BuildPicture(new byte[5000], "2101b6e6277841daa821e8d79a5c528b");

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => worker.IsConnected);
            pipes.Pipe!.HoldStream = true;
            using var cts = new CancellationTokenSource();
            var upload = worker.UploadAsync(package, () => DeepCoolMystiqueSettings.SourceImages, cts.Token);
            Assert.True(pipes.Pipe.Streaming.Wait(TimeSpan.FromSeconds(10)));
            cts.Cancel();
            pipes.Pipe.Release.Set();

            Assert.True(await upload);
            Assert.Contains(pipes.Pipe.Writes(), w => w.Length == MystiqueMedia.FinishLength);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Nothing_reaches_the_screen_after_nexus_control_is_switched_off()
    {
        var store = new InMemoryConfigStore();
        var pipes = new FakeFactory();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled(MystiqueWorker.HandlerId, true);
        var worker = new MystiqueWorker(pipes, gate, new HardwarePresence(new FakeUsb(true)), store, new FakeSensors(), connectPollMs: 60_000, tickMs: 20);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => worker.IsConnected);
            gate.SetEnabled(MystiqueWorker.HandlerId, false);
            await WaitFor(() => !worker.IsConnected);
            int written = pipes.Pipe!.Commands().Length;

            worker.RequestApply();
            worker.RequestApply();

            Assert.Equal(written, pipes.Pipe.Commands().Length);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Does_nothing_while_nexus_control_is_off()
    {
        var pipes = new FakeFactory();
        var store = new CountingStore();
        var worker = Build(store, pipes, present: true, gateOn: false);
        int baseline = store.Loads;

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => store.Loads >= baseline + 5);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, pipes.Opens);
        Assert.False(worker.IsConnected);
    }

    [Fact]
    public async Task Does_nothing_while_the_screen_is_absent()
    {
        var pipes = new FakeFactory();
        var usb = new FakeUsb(false);
        var worker = Build(new InMemoryConfigStore(), pipes, usb);

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => usb.Scans >= 5);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, pipes.Opens);
    }

    [Fact]
    public async Task Turning_nexus_control_on_connects_without_waiting_out_the_poll()
    {
        var store = new InMemoryConfigStore();
        var pipes = new FakeFactory();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled(MystiqueWorker.HandlerId, false);
        var worker = new MystiqueWorker(pipes, gate, new HardwarePresence(new FakeUsb(true)), store, new FakeSensors(), connectPollMs: 60_000, tickMs: 20);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            gate.SetEnabled(MystiqueWorker.HandlerId, true);
            await WaitFor(() => worker.IsConnected);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_unanswered_format_reopens_and_goes_once_more_on_the_fresh_session()
    {
        var store = new InMemoryConfigStore();
        int silenced = 0;
        var pipes = new FakeFactory { Silence = cmd => cmd == MystiqueProtocol.CmdFormat && Interlocked.Increment(ref silenced) == 1 };
        var worker = Build(store, pipes, present: true, gateOn: true);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor(() => worker.IsConnected);

            Assert.True(await worker.FormatAsync(CancellationToken.None));

            Assert.Equal(2, Volatile.Read(ref pipes.Formats));
            Assert.Equal(2, Volatile.Read(ref pipes.Opens));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static MystiqueWorker Build(IConfigStore store, FakeFactory pipes, bool present, bool gateOn) =>
        Build(store, pipes, new FakeUsb(present), gateOn);

    private static MystiqueWorker Build(IConfigStore store, FakeFactory pipes, FakeUsb usb, bool gateOn = true)
    {
        var gate = new DeviceControlGate(store);
        gate.SetEnabled(MystiqueWorker.HandlerId, gateOn);
        return new MystiqueWorker(pipes, gate, new HardwarePresence(usb), store, new FakeSensors(), connectPollMs: 20, tickMs: 20);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    private sealed class FakeUsb : IUsbEnumerator
    {
        private readonly bool _present;

        public FakeUsb(bool present)
        {
            _present = present;
        }

        private int _scans;

        public int Scans => Volatile.Read(ref _scans);

        public List<UsbDeviceEntry> Enumerate() => Interlocked.Increment(ref _scans) > 0 && _present
            ? new List<UsbDeviceEntry> { new() { VendorId = MystiqueProtocol.VendorId, ProductId = MystiqueProtocol.ProductId } }
            : new List<UsbDeviceEntry>();
    }

    private sealed class CountingStore : IConfigStore
    {
        private readonly InMemoryConfigStore _inner = new();
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);
        public string SettingsPath => _inner.SettingsPath;
        public NexusSettings Load()
        {
            Interlocked.Increment(ref _loads);
            return _inner.Load();
        }
        public void Update(Action<NexusSettings> mutator) => _inner.Update(mutator);
        public void FlushNow() => _inner.FlushNow();
        public void Reload() => _inner.Reload();
        public event Action? OnChanged
        {
            add => _inner.OnChanged += value;
            remove => _inner.OnChanged -= value;
        }
    }

    private sealed class FakeFactory : IBulkUsbPipeFactory
    {
        public int Opens;
        public int Formats;
        public RecordingPipe? Pipe;

        /// <summary>Commands whose reply is withheld.</summary>
        public Func<byte, bool>? Silence;

        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
        {
            Interlocked.Increment(ref Opens);
            Pipe = new RecordingPipe(this);
            return Pipe;
        }
    }

    private sealed class RecordingPipe(FakeFactory factory) : IBulkUsbPipe
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _writes = new();
        private byte[]? _pending;

        public byte[] Commands()
        {
            lock (_lock)
            {
                return _writes.Select(w => w[2]).ToArray();
            }
        }

        public List<byte[]> Writes()
        {
            lock (_lock)
            {
                return _writes.ToList();
            }
        }

        public bool Write(ReadOnlySpan<byte> data) => Write(MystiqueProtocol.LiveWritePipe, data);

        /// <summary>When set, the first stream packet signals <see cref="Streaming"/> and waits for <see cref="Release"/>.</summary>
        public bool HoldStream { get; set; }
        public ManualResetEventSlim Streaming { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public bool Write(byte pipeId, ReadOnlySpan<byte> data)
        {
            if (HoldStream && data.Length == MystiqueMedia.PacketLength && !Streaming.IsSet)
            {
                Streaming.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            bool command = data.Length == MystiqueProtocol.FrameLength;
            if (command && data[2] == MystiqueProtocol.CmdFormat)
            {
                Interlocked.Increment(ref factory.Formats);
            }
            bool silent = command && factory.Silence?.Invoke(data[2]) == true;
            lock (_lock)
            {
                _writes.Add(data.ToArray());
                _pending = silent ? null : MystiqueProtocol.BuildFrame(data[2], ReadOnlySpan<byte>.Empty, MystiqueProtocol.DeviceMagic);
            }
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs) => Read(MystiqueProtocol.LiveReadPipe, buffer, timeoutMs);

        public int Read(byte pipeId, Span<byte> buffer, int timeoutMs)
        {
            lock (_lock)
            {
                if (_pending is null)
                {
                    return 0;
                }
                _pending.CopyTo(buffer);
                int n = _pending.Length;
                _pending = null;
                return n;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSensors : ISensorProvider
    {
        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => new[]
        {
            new HardwareSensor { Id = "c/t", Name = "CPU Package", Type = "Temperature", Value = 44 },
            new HardwareSensor { Id = "c/l", Name = "CPU Total", Type = "Load", Value = 25 },
            new HardwareSensor { Id = "c/clk", Name = "Core Average", Type = "Clock", Value = 3600 },
        };
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
