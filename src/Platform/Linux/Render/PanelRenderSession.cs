using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel.Streams;
using SkiaSharp;

namespace Nexus.Service.Platform.Linux.Render;

public sealed class PanelRenderSession : IDisposable
{
    public const int MaxFps = 60;

    private readonly CdpPipeClient _cdp;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private string? _latestFrame;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const int LoadWaitMs = 10_000;
    private string? _targetId;
    private string? _cdpSession;
    private Task? _pump;
    private int _disposed;

    public PanelRenderSession(CdpPipeClient cdp, string sessionId, string url,
        int cssWidth, int cssHeight, double dpr, int fps)
    {
        _cdp = cdp;
        SessionId = sessionId;
        Url = url;
        CssWidth = Math.Max(1, cssWidth);
        CssHeight = Math.Max(1, cssHeight);
        Dpr = dpr > 0 ? dpr : 1.0;
        Fps = Math.Clamp(fps, 1, MaxFps);

        PixelWidth = StreamedPanelProfile.FramePixels(CssWidth, Dpr);
        PixelHeight = StreamedPanelProfile.FramePixels(CssHeight, Dpr);
    }

    public string SessionId { get; }
    public string Url { get; }
    public int CssWidth { get; }
    public int CssHeight { get; }
    public double Dpr { get; }
    public int Fps { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public Action<byte[]>? FrameSink { get; set; }

    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);
    private long _framesDelivered;

    public bool Faulted { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        _cdp.EventReceived += OnEvent;
        var created = await _cdp.SendAsync("Target.createTarget", w =>
        {
            w.WriteString("url", "about:blank");
            w.WriteNumber("width", CssWidth);
            w.WriteNumber("height", CssHeight);
            w.WriteBoolean("newWindow", true);
        }, ct: ct).ConfigureAwait(false);
        _targetId = created.GetProperty("targetId").GetString();

        var attached = await _cdp.SendAsync("Target.attachToTarget", w =>
        {
            w.WriteString("targetId", _targetId);
            w.WriteBoolean("flatten", true);
        }, ct: ct).ConfigureAwait(false);
        _cdpSession = attached.GetProperty("sessionId").GetString();

        await Send("Page.enable", null, ct).ConfigureAwait(false);
        await Send("Emulation.setDeviceMetricsOverride", w =>
        {
            w.WriteNumber("width", CssWidth);
            w.WriteNumber("height", CssHeight);
            w.WriteNumber("deviceScaleFactor", Dpr);
            w.WriteBoolean("mobile", false);
        }, ct).ConfigureAwait(false);

        await Send("Emulation.setFocusEmulationEnabled", w => w.WriteBoolean("enabled", true), ct).ConfigureAwait(false);
        await Send("Page.navigate", w => w.WriteString("url", Url), ct).ConfigureAwait(false);

        try
        {
            await _loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(LoadWaitMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        await Send("Page.startScreencast", w =>
        {
            w.WriteString("format", "png");
            w.WriteNumber("maxWidth", PixelWidth);
            w.WriteNumber("maxHeight", PixelHeight);
            w.WriteNumber("everyNthFrame", 1);
        }, ct).ConfigureAwait(false);

        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private Task<JsonElement> Send(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct) =>
        _cdp.SendAsync(method, writeParams, _cdpSession, ct: ct);

    private void OnEvent(string method, JsonElement parameters, string? session)
    {
        if (_cdpSession is null || !string.Equals(session, _cdpSession, StringComparison.Ordinal))
        {
            if (method == "Target.detachedFromTarget"
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("sessionId", out var detached)
                && string.Equals(detached.GetString(), _cdpSession, StringComparison.Ordinal))
            {
                Faulted = true;
            }
            return;
        }
        switch (method)
        {
            case "Page.screencastFrame":
                if (parameters.TryGetProperty("sessionId", out var frameId) && frameId.TryGetInt32(out var ack))
                {
                    _cdp.Post("Page.screencastFrameAck", w => w.WriteNumber("sessionId", ack), _cdpSession);
                }
                if (parameters.TryGetProperty("data", out var data) && data.GetString() is { } base64)
                {
                    Volatile.Write(ref _latestFrame, base64);
                    if (_frameReady.CurrentCount == 0)
                    {
                        try
                        {
                            _frameReady.Release();
                        }
                        catch (SemaphoreFullException)
                        {
                        }
                    }
                }
                break;
            case "Page.loadEventFired":
                _loaded.TrySetResult();
                break;
            case "Inspector.targetCrashed":
                Faulted = true;
                break;
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var periodMs = 1000 / Fps;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _frameReady.WaitAsync(ct).ConfigureAwait(false);
                var started = Environment.TickCount64;
                var base64 = Interlocked.Exchange(ref _latestFrame, null);
                if (base64 is not null)
                {
                    var frame = DecodeToBgra(base64, PixelWidth, PixelHeight);
                    if (frame is not null)
                    {
                        FrameSink?.Invoke(frame);
                        Interlocked.Increment(ref _framesDelivered);
                    }
                }
                var spent = Environment.TickCount64 - started;
                if (spent < periodMs)
                {
                    await Task.Delay((int)(periodMs - spent), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static byte[]? DecodeToBgra(string base64, int width, int height)
    {
        byte[] encoded;
        try
        {
            encoded = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        using var data = SKData.CreateCopy(encoded);
        if (data is null)
        {
            return null;
        }
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > 64L * 1024 * 1024)
        {
            return null;
        }
        var srcW = codec.Info.Width;
        var srcH = codec.Info.Height;
        var decoded = new byte[srcW * srcH * 4];
        var info = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);
        SKCodecResult result;
        unsafe
        {
            fixed (byte* p = decoded)
            {
                result = codec.GetPixels(info, (IntPtr)p);
            }
        }
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }
        return srcW == width && srcH == height ? decoded : Resample(decoded, srcW, srcH, width, height);
    }

    internal static byte[] Resample(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            var sy = Math.Min(srcH - 1, (int)((long)y * srcH / dstH));
            for (var x = 0; x < dstW; x++)
            {
                var sx = Math.Min(srcW - 1, (int)((long)x * srcW / dstW));
                Buffer.BlockCopy(src, (sy * srcW + sx) * 4, dst, (y * dstW + x) * 4, 4);
            }
        }
        return dst;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cdp.EventReceived -= OnEvent;
        _cts.Cancel();
        if (_targetId is not null && !_cdp.IsClosed)
        {
            var target = _targetId;
            _cdp.Post("Target.closeTarget", w => w.WriteString("targetId", target));
        }
        try
        {
            _pump?.Wait(2000);
        }
        catch
        {
        }
        _cts.Dispose();
        _frameReady.Dispose();
    }
}
