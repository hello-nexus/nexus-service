using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Rate-limits the backlight log line: a burst of changes (slider drag, lock fade) logs its
/// first level at once and its settled level once the burst ends. Used under a transport's
/// brightness lock.
/// </summary>
internal sealed class BacklightLog
{
    internal const long IntervalMs = 1000;

    private readonly Action<string> _write;
    private long _lastMs = long.MinValue / 2;
    private int _pending = -1;

    public BacklightLog(Action<string>? write = null) => _write = write ?? ServiceLog.Info;

    /// <summary>A level reached the panel.</summary>
    public void Applied(string handlerId, int percent, long nowMs)
    {
        if (nowMs - _lastMs >= IntervalMs)
        {
            _lastMs = nowMs;
            _pending = -1;
            _write($"[{handlerId}] backlight {percent}%");
        }
        else
        {
            _pending = percent;
        }
    }

    /// <summary>The level did not change this pass; emits the held level once the interval has passed.</summary>
    public void Settled(string handlerId, long nowMs)
    {
        if (_pending >= 0 && nowMs - _lastMs >= IntervalMs)
        {
            var percent = _pending;
            _lastMs = nowMs;
            _pending = -1;
            _write($"[{handlerId}] backlight {percent}%");
        }
    }
}
