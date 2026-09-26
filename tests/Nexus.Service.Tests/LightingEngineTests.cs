using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

public class LightingEngineTests
{
    private static DeviceFrame[] MakeDevices(int count = 2, int ledsPerDevice = 4)
    {
        var frames = new DeviceFrame[count];
        for (int i = 0; i < count; i++)
            frames[i] = new DeviceFrame(i, $"test-{i}", ledsPerDevice, x: i * 100, y: 100);
        return frames;
    }

    [Fact]
    public void SetEffect_ChangesCurrentEffectName()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.SetEffect(new TestEffect("test-fx"));
        Assert.Equal("test-fx", engine.CurrentEffectName);
    }

    [Fact]
    public void CanRenderAhead_is_false_without_a_shader_effect()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        Assert.False(engine.CanRenderAhead(new[] { "test-0" }));
        engine.SetEffect(new TestEffect("not-a-shader"));
        Assert.False(engine.CanRenderAhead(new[] { "test-0" }));
    }

    [Fact]
    public void RequestAhead_completes_without_frames_when_the_effect_is_not_a_shader()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.FrameIntervalMs = 10;
        engine.SetEffect(new TestEffect("not-a-shader"));

        var req = engine.RequestAhead(new[] { "test-0" }, new long[] { 1, 2, 3 });

        Assert.True(req.Done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(req.Frames);
    }

    [Fact]
    public void SetEffect_DisposesOldEffect()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        var old = new TestEffect("old");
        engine.SetEffect(old);
        engine.SetEffect(new TestEffect("new"));
        Assert.True(old.Disposed);
    }

    [Fact]
    public async Task SetEffect_EmitsV3Frames()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.FrameIntervalMs = 10;

        var frameReceived = new TaskCompletionSource<byte[]>();
        engine.OnFrame += frame => frameReceived.TrySetResult(frame.ToArray());
        engine.SetEffect(new TestEffect("emitter"));

        var finished = await Task.WhenAny(frameReceived.Task, Task.Delay(2000));
        Assert.Same(frameReceived.Task, finished);
        var frame = await frameReceived.Task;
        // v3: [0x03][canvasW:2][canvasH:2][canvasPixels:W*H*3][deviceCount][...]
        Assert.Equal(0x03, frame[0]);
        var cw = frame[1] | (frame[2] << 8);
        var ch = frame[3] | (frame[4] << 8);
        Assert.True(cw > 0 && ch > 0, $"canvas dims positive ({cw}x{ch})");
        var canvasBytes = cw * ch * 3;
        Assert.True(frame.Length >= 5 + canvasBytes + 1, "frame contains canvas + device header");
        // Device count byte comes immediately after canvas pixels.
        var deviceCount = frame[5 + canvasBytes];
        Assert.Equal(2, deviceCount); // MakeDevices defaults to 2
    }

    [Fact]
    public async Task Paused_StopsRenderingButKeepsBroadcasting()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.FrameIntervalMs = 10;

        var effect = new CountingEffect("counter");
        engine.SetEffect(effect);

        var firstFrame = new TaskCompletionSource<bool>();
        engine.OnFrame += _ => firstFrame.TrySetResult(true);
        await Task.WhenAny(firstFrame.Task, Task.Delay(2000));
        Assert.True(firstFrame.Task.IsCompletedSuccessfully, "effect renders before pause");

        engine.SetPaused(true);
        Assert.True(engine.Paused);
        var renderCountAtPause = effect.RenderCount;

        var broadcastCount = 0;
        var gotBroadcastWhilePaused = new TaskCompletionSource<bool>();
        engine.OnFrame += _ =>
        {
            if (System.Threading.Interlocked.Increment(ref broadcastCount) >= 3)
            {
                gotBroadcastWhilePaused.TrySetResult(true);
            }
        };
        await Task.WhenAny(gotBroadcastWhilePaused.Task, Task.Delay(2000));
        Assert.True(gotBroadcastWhilePaused.Task.IsCompletedSuccessfully, "frames keep broadcasting while paused");
        Assert.Equal(renderCountAtPause, effect.RenderCount);

        engine.SetPaused(false);
        Assert.False(engine.Paused);
    }

    [Fact]
    public void SetEffect_ResetsPaused()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.SetEffect(new TestEffect("first"));
        engine.SetPaused(true);
        Assert.True(engine.Paused);
        engine.SetEffect(new TestEffect("second"));
        Assert.False(engine.Paused);
    }

    [Fact]
    public void Stop_ResetsPaused()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        engine.SetEffect(new TestEffect("stopping"));
        engine.SetPaused(true);
        Assert.True(engine.Paused);
        engine.Stop();
        Assert.False(engine.Paused);
    }

    [Fact]
    public void Stop_ClearsEffect()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices());
        var effect = new TestEffect("stopping");
        engine.SetEffect(effect);
        engine.Stop();
        Assert.Equal("none", engine.CurrentEffectName);
        Assert.True(effect.Disposed);
    }

    [Fact]
    public void Stop_EmitsBlankFrame()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeDevices(1, 4));

        byte[]? lastFrame = null;
        engine.OnFrame += frame => lastFrame = frame.ToArray();
        engine.SetEffect(new TestEffect("blank"));
        engine.Stop();

        Assert.NotNull(lastFrame);
        // v3: [0x03][canvasW:2][canvasH:2][canvasPixels][devCount][idx:1][ledCount:2][leds:N*3]
        Assert.Equal(0x03, lastFrame![0]);
        var cw = lastFrame[1] | (lastFrame[2] << 8);
        var ch = lastFrame[3] | (lastFrame[4] << 8);
        var canvasEnd = 5 + cw * ch * 3;
        // Canvas should be cleared (all zeros) after Stop.
        for (int i = 5; i < canvasEnd; i++)
        {
            Assert.Equal(0, lastFrame[i]);
        }
        // Device LED bytes come after the device header (1 + 2 bytes).
        var ledStart = canvasEnd + 1 + 1 + 2;
        for (int i = ledStart; i < lastFrame.Length; i++)
        {
            Assert.Equal(0, lastFrame[i]);
        }
    }

    private sealed class TestEffect : IEffect
    {
        public string Name { get; }
        public bool Disposed { get; private set; }
        public TestEffect(string name) { Name = name; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
        }
        public void Dispose() { Disposed = true; }
    }

    private sealed class CountingEffect : IEffect
    {
        public string Name { get; }
        public int RenderCount;
        public CountingEffect(string name) { Name = name; }
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            System.Threading.Interlocked.Increment(ref RenderCount);
        }
        public void Dispose() { }
    }
}
