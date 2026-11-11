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

    // Stop and the render tick can both serialize a frame, so composing is serialized. Each send gets its own copy because section lengths change per frame and the send outlives the lock.
    public void OnFrame(ReadOnlyMemory<byte> frame, DeviceFrame[] devices)
    {
        var connected = _clientCount() > 0;
        // A stopped engine serializes nothing more, so reporting stays off until it runs again.
        _sink.Enabled = connected && !_engineStopped();
        if (!connected)
        {
            return;
        }
        byte[]? copy;
        lock (_lock)
        {
            copy = LiveLedFrameComposer.ComposeCopy(frame.Span, devices, _sink, ref _scratch);
            if ((++_frames & 255) == 0)
            {
                _sink.Prune(devices);
            }
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
