#if WINDOWS
using System;
using System.Diagnostics;
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
    private const int DiagWindowSeconds = 60;
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
        HelperLog.Write($"[mirror-diag] Start monitor={monitorId} canvas={width}x{height}");
        Stop();
        var cts = new CancellationTokenSource();
        lock (_lock) { _cts = cts; }
        _worker = Task.Run(() => RunLoop(monitorId, width, height, cts.Token))
            .ContinueWith(t => HelperLog.Write($"[mirror-diag] worker faulted {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_lock) { cts = _cts; worker = _worker; _cts = null; _worker = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (Exception ex) { HelperLog.Write($"[mirror-diag] Stop cancel failed {ex.GetType().Name}: {ex.Message}"); }
        try { worker?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception ex) { HelperLog.Write($"[mirror-diag] Stop wait failed {ex.GetType().Name}: {ex.Message}"); }
        try { cts.Dispose(); } catch (Exception ex) { HelperLog.Write($"[mirror-diag] Stop dispose failed {ex.GetType().Name}: {ex.Message}"); }
        // _dxgi is owned by the worker; disposal happens at the end of RunLoop.
    }

    private async Task RunLoop(string monitorId, int width, int height, CancellationToken ct)
    {
        var outputIdx = uint.TryParse(monitorId, out var idx) ? idx : 0u;
        var buffer = new byte[width * height * 3];
        DxgiScreenCapture? dxgi = null;
        var retryMs = MinRetryMs;
        var recovering = false;
        var initFailLogged = false;
        var stats = new MirrorDiagWindow();
        var windowStart = Stopwatch.GetTimestamp();
        HelperLog.Write($"[mirror-diag] capture start output={outputIdx} canvas={width}x{height}");
        try
        {
            // ~30 fps push rate. The service's render loop runs at the engine
            // tick (16ms = 60 fps), so half of those ticks reuse the cached
            // latest frame.
            var period = TimeSpan.FromMilliseconds(33);
            while (!ct.IsCancellationRequested)
            {
                var nowTs = Stopwatch.GetTimestamp();
                if (nowTs - windowStart >= DiagWindowSeconds * Stopwatch.Frequency)
                {
                    HelperLog.Write(stats.FormatAndReset((nowTs - windowStart) / (double)Stopwatch.Frequency));
                    windowStart = nowTs;
                }
                var backoff = false;
                if (dxgi is not null && dxgi.IsLost)
                {
                    stats.Lost++;
                    var wasRecovering = recovering;
                    var waited = false;
                    if (!wasRecovering) HelperLog.Warn("[screen-capture-pusher] duplication lost; re-creating");
                    var recreated = false;
                    if (!dxgi.NeedsFullRebuild)
                    {
                        if (wasRecovering)
                        {
                            // Lost again before delivering a frame: back off instead of recreating every tick.
                            try { await Task.Delay(retryMs, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                            retryMs = Math.Min(retryMs * 2, MaxRetryMs);
                            waited = true;
                        }
                        recreated = dxgi.TryRecreateDuplication();
                        if (recreated) stats.Recreated++;
                    }
                    recovering = true;
                    if (!recreated)
                    {
                        lock (_lock) { _dxgi = null; }
                        dxgi.Dispose();
                        dxgi = null;
                        backoff = wasRecovering && !waited;
                    }
                }
                if (dxgi is null && !backoff)
                {
                    var next = new DxgiScreenCapture { Diag = HelperLog.Write, SkipPointerOnlyFrames = true, LogInitFailure = !initFailLogged };
                    if (next.Initialize(outputIdx))
                    {
                        initFailLogged = false;
                        next.ConfigureDownsample(width, height);
                        dxgi = next;
                        lock (_lock) { _dxgi = dxgi; }
                    }
                    else
                    {
                        next.Dispose();
                        initFailLogged = true;
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
                var gotImage = dxgi.AcquireFrame(timeoutMs: 16);
                switch (dxgi.LastOutcome)
                {
                    case AcquireOutcome.Timeout:
                        stats.AcquireCalls++; stats.Timeouts++; stats.RecordAcquire(dxgi.LastAcquireMs);
                        break;
                    case AcquireOutcome.Lost:
                        stats.AcquireCalls++; stats.RecordAcquire(dxgi.LastAcquireMs);
                        break;
                    case AcquireOutcome.Frame:
                        stats.AcquireCalls++; stats.RecordAcquire(dxgi.LastAcquireMs);
                        stats.Frames++; stats.AccumulatedSum += dxgi.LastAccumulatedFrames;
                        break;
                    case AcquireOutcome.PointerOnly:
                        stats.AcquireCalls++; stats.RecordAcquire(dxgi.LastAcquireMs);
                        stats.PointerOnly++;
                        break;
                }
                if (gotImage)
                {
                    stats.Copies++;
                    if (recovering)
                    {
                        HelperLog.Write("[screen-capture-pusher] capture resumed");
                        recovering = false;
                        retryMs = MinRetryMs;
                    }
                    if (dxgi.BlitToBuffer(buffer, width, height))
                    {
                        stats.Path = dxgi.UsingGpu ? "gpu" : "cpu";
                        stats.RecordReadback(dxgi.LastMapMs);
                        stats.RecordCopyMap(dxgi.LastCopyMs + dxgi.LastMapMs);
                        stats.RecordBlit(dxgi.LastSampleMs);
                        var copy = new byte[buffer.Length];
                        Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length);
                        try
                        {
                            await _outbound.SendAsync(
                                type: "screenMirror.frame",
                                payload: new ScreenMirrorFramePayload { Width = width, Height = height, Bytes = copy },
                                payloadType: AppJsonContext.Default.ScreenMirrorFramePayload,
                                ct: ct).ConfigureAwait(false);
                            stats.Pushed++;
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { HelperLog.Write($"[mirror-diag] send failed {ex.GetType().Name}: {ex.Message}"); }
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
        catch (Exception ex) { HelperLog.Write($"[mirror-diag] loop crashed {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            lock (_lock) { _dxgi = null; }
            try { dxgi?.Dispose(); } catch (Exception ex) { HelperLog.Write($"[mirror-diag] dispose failed {ex.GetType().Name}: {ex.Message}"); }
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
