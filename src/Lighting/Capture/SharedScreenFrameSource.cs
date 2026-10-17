using System;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// One screen capture shared by every consumer: the global Mirror effect and the
/// mirror-pin effect each hold a handle, the first started handle opens the
/// underlying source and the last stopped one closes it. Without this, one
/// consumer's Stop (a mode switch away from Mirror) would end the capture the
/// other still reads.
/// </summary>
public sealed class SharedScreenFrameSource
{
    private readonly IScreenFrameSource _inner;
    private readonly object _gate = new();
    private int _refs;
    private string _monitor = "";
    private int _w, _h;

    public SharedScreenFrameSource(IScreenFrameSource inner) => _inner = inner;

    /// <summary>Number of started handles; zero means the capture is closed.</summary>
    public int ActiveHandles { get { lock (_gate) return _refs; } }

    public IScreenFrameSource CreateHandle() => new Handle(this);

    /// <summary>Whether the underlying source can deliver frames at all.</summary>
    public bool Available => _inner.Available;

    /// <summary>
    /// A read-only consumer for previews. It starts the capture only while no other
    /// consumer holds it, and never restarts or retargets a capture a device effect
    /// holds, so a preview can not change what the devices show.
    /// </summary>
    public IScreenFrameSource CreateObserver() => new Observer(this);

    private void StartShared(string monitorId, int width, int height)
    {
        lock (_gate)
        {
            if (_refs == 0)
            {
                _inner.Start(monitorId, width, height);
            }
            else if (!string.Equals(monitorId, _monitor, StringComparison.Ordinal) || width != _w || height != _h)
            {
                // The newest consumer asked for a different screen: follow it.
                _inner.Stop();
                _inner.Start(monitorId, width, height);
            }
            _refs++;
            _monitor = monitorId;
            _w = width;
            _h = height;
        }
    }

    private void StopShared()
    {
        lock (_gate)
        {
            if (_refs == 0) return;
            if (--_refs == 0)
            {
                _inner.Stop();
            }
        }
    }

    private sealed class Handle : IScreenFrameSource
    {
        private readonly SharedScreenFrameSource _owner;
        private bool _started;
        private bool _closed;

        public Handle(SharedScreenFrameSource owner) => _owner = owner;

        public void Start(string monitorId, int width, int height)
        {
            // Under the owner's gate, and a no-op once stopped: a render that races the
            // effect's disposal must not take a reference nothing will release.
            lock (_owner._gate)
            {
                if (_started || _closed) return;
                _owner.StartShared(monitorId, width, height);
                _started = true;
            }
        }

        public void Stop()
        {
            lock (_owner._gate)
            {
                _closed = true;
                if (!_started) return;
                _started = false;
                _owner.StopShared();
            }
        }

        public byte[]? TryAcquireFrame(out int width, out int height) => _owner._inner.TryAcquireFrame(out width, out height);

        public void Reselect() => _owner._inner.Reselect();
    }

    private sealed class Observer : IScreenFrameSource
    {
        private readonly SharedScreenFrameSource _owner;
        private bool _holding;
        private bool _closed;

        public Observer(SharedScreenFrameSource owner) => _owner = owner;

        public bool Available => _owner._inner.Available;

        /// <summary>Idempotent: call every tick. Follows the monitor only while it is the sole consumer.</summary>
        public void Start(string monitorId, int width, int height)
        {
            lock (_owner._gate)
            {
                if (_closed) return;
                if (!_holding)
                {
                    if (_owner._refs != 0) return;
                    _owner.StartShared(monitorId, width, height);
                    _holding = true;
                }
                else if (_owner._refs == 1
                    && (!string.Equals(monitorId, _owner._monitor, StringComparison.Ordinal) || width != _owner._w || height != _owner._h))
                {
                    _owner._inner.Stop();
                    _owner._inner.Start(monitorId, width, height);
                    _owner._monitor = monitorId;
                    _owner._w = width;
                    _owner._h = height;
                }
            }
        }

        public void Stop()
        {
            lock (_owner._gate)
            {
                _closed = true;
                if (!_holding) return;
                _holding = false;
                _owner.StopShared();
            }
        }

        public byte[]? TryAcquireFrame(out int width, out int height) => _owner._inner.TryAcquireFrame(out width, out height);
    }
}
