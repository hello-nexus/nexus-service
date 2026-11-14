using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting;

/// <summary>Turns each serialized engine frame into the dashboard frame and hands it to the lighting output stream.</summary>
internal sealed class LiveLedDashboardFeed
{
    private readonly LiveLedSink _sink;
    private readonly Func<int> _clientCount;
    private readonly Func<bool> _engineStopped;
    private readonly Action<byte[]> _sendComposed;
    private readonly Action<ReadOnlyMemory<byte>> _sendRaw;
    private readonly object _lock = new();
    private byte[] _scratch = Array.Empty<byte>();
    private int _frames;

    public LiveLedDashboardFeed(LiveLedSink sink, Func<int> clientCount, Func<bool> engineStopped, Action<byte[]> sendComposed, Action<ReadOnlyMemory<byte>> sendRaw)
    {
        _sink = sink;
        _clientCount = clientCount;
        _engineStopped = engineStopped;
        _sendComposed = sendComposed;
        _sendRaw = sendRaw;
    }

    // Stop and the render tick can both serialize a frame, so deciding, composing and handing off are serialized. Each send gets its own copy because section lengths change per frame and the send outlives the lock. Once the engine is stopped only its final frame goes out, so frames arriving after it are dropped; a render tick still racing Stop on the engine's shared frame buffer can leak lit colors into the final frame itself.
    public void OnFrame(ReadOnlyMemory<byte> frame, DeviceFrame[] devices, bool final)
    {
        var connected = _clientCount() > 0;
        lock (_lock)
        {
            var stopped = _engineStopped();
            if (stopped && !final)
            {
                return;
            }
            _sink.Enabled = connected;
            if (!connected)
            {
                return;
            }
            var copy = LiveLedFrameComposer.ComposeCopy(frame.Span, devices, _sink, ref _scratch);
            if ((++_frames & 255) == 0)
            {
                _sink.Prune(devices);
            }
            if (copy is not null)
            {
                _sendComposed(copy);
            }
            else
            {
                _sendRaw(frame);
            }
        }
    }
}
