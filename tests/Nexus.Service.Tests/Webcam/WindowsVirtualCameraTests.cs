using Nexus.Service.Webcam;
using Nexus.Service.Webcam.Windows;

namespace Nexus.Service.Tests.Webcam;

/// <summary>
/// Lifecycle of the Windows camera through the control/ring/decoder seams -
/// runs anywhere, no Windows APIs touched.
/// </summary>
public class WindowsVirtualCameraTests
{
    private static readonly WebcamFormat Format = new(1920, 1080, WebcamCodec.Mjpeg);

    private static WindowsVirtualCamera Make(FakeControl control, FakeDecoderFactory? decoders = null) =>
        new(control, new FakeRingFactory(), decoders ?? new FakeDecoderFactory(), () => false);

    [Fact]
    public async Task Start_CreateFailure_SurfacesActionableError()
    {
        var control = new FakeControl { CreateResult = unchecked((int)0x80004005) };
        var camera = Make(control);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => camera.StartAsync(Format, CancellationToken.None));

        Assert.Contains("privacy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(control.Destroyed);
    }

    [Fact]
    public async Task WriteBeforeStart_IsIgnored()
    {
        var decoders = new FakeDecoderFactory();
        var camera = Make(new FakeControl(), decoders);

        await camera.WriteFrameAsync(new byte[16], new WebcamFrameInfo(1920, 1080, true, WebcamCodec.Mjpeg, 0));

        Assert.Equal(0, decoders.Created);
    }

    [Fact]
    public async Task Stop_DestroysOnlyOwnHandle_AndIsIdempotent()
    {
        var control = new FakeControl();
        var camera = Make(control);
        await camera.StartAsync(Format, CancellationToken.None);

        await camera.StopAsync();
        await camera.StopAsync();

        Assert.Equal(new[] { control.LastHandle }, control.Destroyed);
    }

    [Fact]
    public async Task Restart_ReplacesTheLiveCamera()
    {
        var control = new FakeControl();
        var camera = Make(control);

        await camera.StartAsync(Format, CancellationToken.None);
        var first = control.LastHandle;
        await camera.StartAsync(Format, CancellationToken.None);

        Assert.Equal(new[] { first }, control.Destroyed);
        Assert.Equal(2, control.Created);
    }

    [Fact]
    public async Task DecodedFrame_LandsInRing_AndSignals()
    {
        var control = new FakeControl();
        var decoders = new FakeDecoderFactory();
        var camera = Make(control, decoders);
        await camera.StartAsync(Format, CancellationToken.None);

        await camera.WriteFrameAsync(new byte[64], new WebcamFrameInfo(1920, 1080, true, WebcamCodec.Mjpeg, 33));

        Assert.Equal(1, FakeRingFactory.LastRing!.Signals);
    }

    private sealed class FakeControl : IVCamControl
    {
        private nint _next = 0x100;
        public int CreateResult { get; set; }
        public int Created { get; private set; }
        public nint LastHandle { get; private set; }
        public List<nint> Destroyed { get; } = new();

        public void EnsureAvailable() { }

        public int Create(string friendlyName, bool allUsers, out nint handle)
        {
            if (CreateResult < 0)
            {
                handle = 0;
                return CreateResult;
            }
            Created++;
            handle = _next++;
            LastHandle = handle;
            return 0;
        }

        public int Destroy(nint handle)
        {
            Destroyed.Add(handle);
            return 0;
        }
    }

    private sealed class FakeRing : IVCamFrameRing
    {
        private readonly byte[] _buffer;
        public int Signals { get; private set; }

        public FakeRing(long bytes) => _buffer = new byte[bytes];

        public Memory<byte> Mapping => _buffer;

        public void SignalFrameReady() => Signals++;

        public void Dispose() { }
    }

    private sealed class FakeRingFactory : IVCamFrameRingFactory
    {
        public static FakeRing? LastRing { get; private set; }

        public IVCamFrameRing Create(int width, int height, int slotCount)
        {
            var slotBytes = width * height * 3 / 2;
            LastRing = new FakeRing(VCamProtocol.MappingBytes(slotBytes, slotCount));
            return LastRing;
        }
    }

    private sealed class FakeDecoder : IWindowsVideoDecoder
    {
        private readonly byte[] _nv12 = new byte[1920 * 1080 * 3 / 2];

        public bool TryDecode(ReadOnlySpan<byte> payload, uint timestampMs, out DecodedNv12Frame frame)
        {
            frame = new DecodedNv12Frame(_nv12, 1920, 1080);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class FakeDecoderFactory : IWindowsVideoDecoderFactory
    {
        public int Created { get; private set; }

        public IWindowsVideoDecoder Create(WebcamCodec codec, int width, int height)
        {
            Created++;
            return new FakeDecoder();
        }
    }
}
