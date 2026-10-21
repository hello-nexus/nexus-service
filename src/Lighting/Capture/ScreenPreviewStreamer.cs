using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Streams the selected monitor's capture to preview clients while at least one is
/// connected, in the same frame layout as the lighting output stream (zero devices),
/// so the page paints it exactly like a global Mirror frame. The capture is held
/// through a read-only observer handle: it starts the shared capture only when no
/// device effect holds it and never retargets one that does. Each loop owns its own
/// handle and releases only that. Flip and the color post-process apply; the
/// reactive glow does not.
/// </summary>
public sealed class ScreenPreviewStreamer : IDisposable
{
    public const int FrameIntervalMs = 100;

    private readonly WebSocketHub _hub;
    private readonly Func<IScreenFrameSource?> _openHandle;
    private readonly Func<string> _monitor;
    private readonly PostProcessState _postProcess;
    private readonly Func<int> _clientCount;
    private readonly object _gate = new();
    private readonly CanvasBuffer _canvas = new(CanvasBuffer.DefaultWidth, CanvasBuffer.DefaultHeight);
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ScreenPreviewStreamer(WebSocketHub hub, Func<IScreenFrameSource?> openHandle, Func<string> monitor, PostProcessState postProcess, Func<int>? clientCount = null)
    {
        _hub = hub;
        _openHandle = openHandle;
        _monitor = monitor;
        _postProcess = postProcess;
        _clientCount = clientCount ?? (() => hub.ClientCount);
        _hub.OnFirstClient += Reconcile;
        _hub.OnAllClientsGone += Reconcile;
    }

    /// <summary>True while a streaming loop is running.</summary>
    public bool Running { get { lock (_gate) return _cts is not null; } }

    /// <summary>
    /// Makes the loop match the client count, under one lock. Both hub events call this and read the
    /// live count, so a reconnect that interleaves a leave and a join can not strand a client.
    /// </summary>
    internal void Reconcile()
    {
        lock (_gate)
        {
            var want = !_disposed && _clientCount() > 0;
            if (!want)
            {
                _cts?.Cancel();
                _cts = null;
                return;
            }
            if (_cts is not null) return;
            var handle = _openHandle();
            if (handle is null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(handle, token));
        }
    }

    private async Task RunAsync(IScreenFrameSource handle, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameIntervalMs));
        var warned = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Tick(handle); }
                catch (Exception ex)
                {
                    if (!warned)
                    {
                        warned = true;
                        ServiceLog.Warn($"[screen-preview] frame failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { handle.Stop(); } catch { /* released best-effort */ }
        }
    }

    private void Tick(IScreenFrameSource handle)
    {
        handle.Start(_monitor(), CanvasBuffer.DefaultWidth, CanvasBuffer.DefaultHeight);
        var src = handle.TryAcquireFrame(out var w, out var h);
        if (src is null || w <= 0 || h <= 0) return;
        ScreenMirrorEffect.BlitToCanvas(_canvas, src, w, h);
        _canvas.ApplyFlip(_postProcess.FlipX, _postProcess.FlipY);
        _canvas.ApplyPostProcess(_postProcess);
        // A fresh array per broadcast: a slow client's pending send must not see the next frame written into it.
        var frame = new byte[1 + 4 + _canvas.ByteCount + 1];
        frame[0] = 0x03;
        frame[1] = (byte)(CanvasBuffer.DefaultWidth & 0xFF);
        frame[2] = (byte)(CanvasBuffer.DefaultWidth >> 8);
        frame[3] = (byte)(CanvasBuffer.DefaultHeight & 0xFF);
        frame[4] = (byte)(CanvasBuffer.DefaultHeight >> 8);
        _canvas.Pixels.CopyTo(frame.AsSpan(5));
        _ = _hub.BroadcastBinaryAsync(frame);
    }

    public void Dispose()
    {
        _hub.OnFirstClient -= Reconcile;
        _hub.OnAllClientsGone -= Reconcile;
        lock (_gate)
        {
            _disposed = true;
            _cts?.Cancel();
            _cts = null;
        }
    }
}
