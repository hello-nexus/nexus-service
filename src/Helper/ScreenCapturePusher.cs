#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side screen-capture worker. The service (Session 0 / LocalSystem)
/// cannot use <see cref="IDXGIOutputDuplication"/>; the helper runs in the
/// interactive user session where DXGI sees the real desktop outputs. The
/// service sends <c>screenMirror.start</c> with the chosen monitor + canvas
/// resolution, this class spins up a DXGI duplication thread, downsamples
/// each captured frame to canvas-resolution RGB24 (~43 KB at 160x90), and
/// emits <c>screenMirror.frame</c> envelopes via the shared
/// <see cref="HelperOutbound"/>. Stops on <c>screenMirror.stop</c> or when
/// disposed (helper teardown).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapturePusher : IDisposable
{
    private const int MinRetryMs = 250;
    private const int MaxRetryMs = 4000;
    private readonly HelperOutbound _outbound;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private DxgiScreenCapture? _dxgi;
    private bool _disposed;

    public ScreenCapturePusher(HelperOutbound outbound)
    {
        _outbound = outbound;
    }

    public void Start(string monitorId, int width, int height)
    {
        if (_disposed) return;
        if (width <= 0 || height <= 0) return;
        Stop();
        var cts = new CancellationTokenSource();
        lock (_lock) { _cts = cts; }
        _worker = Task.Run(() => RunLoop(monitorId, width, height, cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_lock) { cts = _cts; worker = _worker; _cts = null; _worker = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { worker?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { cts.Dispose(); } catch { }
        // _dxgi is owned by the worker; disposal happens at the end of RunLoop.
    }

    private async Task RunLoop(string monitorId, int width, int height, CancellationToken ct)
    {
        var outputIdx = uint.TryParse(monitorId, out var idx) ? idx : 0u;
        var buffer = new byte[width * height * 3];
        DxgiScreenCapture? dxgi = null;
        var retryMs = MinRetryMs;
        var recovering = false;
        try
        {
            // ~30 fps push rate. The service's render loop runs at the engine
            // tick (16ms = 60 fps), so half of those ticks reuse the cached
            // latest frame.
            var period = TimeSpan.FromMilliseconds(33);
            while (!ct.IsCancellationRequested)
            {
                var backoff = false;
                if (dxgi is not null && dxgi.IsLost)
                {
                    lock (_lock) { _dxgi = null; }
                    dxgi.Dispose();
                    dxgi = null;
                    if (!recovering) HelperLog.Warn("[screen-capture-pusher] duplication lost; re-creating");
                    // Lost again before delivering a frame: back off instead of rebuilding every tick.
                    backoff = recovering;
                    recovering = true;
                }
                if (dxgi is null && !backoff)
                {
                    var next = new DxgiScreenCapture();
                    if (next.Initialize(outputIdx))
                    {
                        dxgi = next;
                        lock (_lock) { _dxgi = dxgi; }
                    }
                    else
                    {
                        next.Dispose();
                        if (retryMs == MinRetryMs) HelperLog.Warn("[screen-capture-pusher] DXGI init failed; retrying");
                        recovering = true;
                        backoff = true;
                    }
                }
                if (backoff || dxgi is null)
                {
                    // DXGI raises no event when duplication becomes possible again, so poll with backoff.
                    try { await Task.Delay(retryMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    retryMs = Math.Min(retryMs * 2, MaxRetryMs);
                    continue;
                }
                var deadline = DateTime.UtcNow + period;
                if (dxgi.AcquireFrame(timeoutMs: 16))
                {
                    if (recovering)
                    {
                        HelperLog.Write("[screen-capture-pusher] capture resumed");
                        recovering = false;
                        retryMs = MinRetryMs;
                    }
                    if (dxgi.BlitToBuffer(buffer, width, height))
                    {
                        var copy = new byte[buffer.Length];
                        Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length);
                        try
                        {
                            await _outbound.SendAsync(
                                type: "screenMirror.frame",
                                payload: new ScreenMirrorFramePayload { Width = width, Height = height, Bytes = copy },
                                payloadType: AppJsonContext.Default.ScreenMirrorFramePayload,
                                ct: ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { Console.Error.WriteLine($"[screen-capture-pusher] send failed: {ex.Message}"); }
                    }
                    dxgi.ReleaseFrame();
                }
                var wait = deadline - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-capture-pusher] loop crashed: {ex.Message}"); }
        finally
        {
            lock (_lock) { _dxgi = null; }
            try { dxgi?.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
#endif
