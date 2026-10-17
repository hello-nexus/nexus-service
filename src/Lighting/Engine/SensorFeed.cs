using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Latest sensor readings for lighting shaders. A shader asks for a reading through a
/// <see cref="Channel"/> on every frame; the first ask starts one background poller, and the poller
/// stops by itself once nothing has asked for <see cref="IdleMs"/>, so a session with no sensor
/// effect never walks the hardware. Polling never runs on the GL thread; a frame only reads a
/// volatile float.
/// </summary>
public sealed class SensorFeed : IDisposable
{
    /// <summary>Gap between hardware reads.</summary>
    internal const int PollMs = 1000;

    /// <summary>No frame asked for this long and the poller exits.</summary>
    internal const int IdleMs = 5000;

    /// <summary>Published for a sensor the poll could not find; unlike NaN it is a current answer.</summary>
    public const float Missing = float.NegativeInfinity;

    /// <summary>Shortest gap between two logged poll failures.</summary>
    private const int LogGapMs = 60_000;

    /// <summary>One reading, shared by every shader bound to the same category and sensor.</summary>
    public sealed class Channel
    {
        private readonly SensorFeed _feed;
        private long _lastAskMs;
        private float _value = float.NaN;

        internal Channel(SensorFeed feed)
        {
            _feed = feed;
        }

        internal long LastAskMs => Volatile.Read(ref _lastAskMs);

        internal void Publish(float value) => Volatile.Write(ref _value, value);

        /// <summary>
        /// The latest reading in the sensor's units. NaN while no current reading has arrived (never polled, or the
        /// channel sat unread long enough to go stale); <see cref="Missing"/> when it was polled and the sensor is
        /// not there. Marks the channel as wanted.
        /// </summary>
        public float Read()
        {
            var now = _feed._clockMs();
            var previous = Interlocked.Exchange(ref _lastAskMs, now);
            if (previous == 0 || now - previous >= IdleMs)
            {
                // Nobody was polling it, so what it holds is old; wait for a fresh reading.
                Volatile.Write(ref _value, float.NaN);
            }
            _feed.Wake();
            return Volatile.Read(ref _value);
        }
    }

    private readonly Func<string, string, float>? _resolve;
    private readonly ISensorProvider? _sensors;
    private readonly Func<long> _clockMs;
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _pollMs;
    // Tests drive Tick by hand and keep the poller thread out of the count.
    private readonly bool _background;
    private int _running;
    private int _disposed;

    public SensorFeed(ISensorProvider sensors)
        : this(sensors, null, null)
    {
    }

    internal SensorFeed(ISensorProvider? sensors, Func<string, string, float>? resolve, Func<long>? clockMs, int pollMs = PollMs, bool background = true)
    {
        _pollMs = pollMs;
        _background = background;
        _sensors = sensors;
        _resolve = resolve;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The channel for a binding's category and sensor; bindings that differ only in range share it.</summary>
    public Channel Acquire(SensorBinding binding) => _channels.GetOrAdd(binding.FeedKey, _ => new Channel(this));

    /// <summary>True while the poller thread is alive.</summary>
    internal bool IsPolling => Volatile.Read(ref _running) != 0;

    private void Wake()
    {
        if (!_background || Volatile.Read(ref _running) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool wanted;
                try
                {
                    wanted = Tick(_clockMs());
                }
                catch (Exception ex)
                {
                    // A failure does not end the loop (a restart would retry with no gap), but an idle feed still stops.
                    LogFailure(ex);
                    wanted = AnyWanted(_clockMs());
                }
                if (!wanted)
                {
                    break;
                }
                await Task.Delay(_pollMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
        // A frame that asked between the last Tick and the flag clearing found the poller still marked running.
        if (!_cts.IsCancellationRequested && AnyWanted(_clockMs()))
        {
            Wake();
        }
    }

    private long _lastLogMs = long.MinValue;

    /// <summary>Failures that reached the log rather than being rate-limited away.</summary>
    internal int LoggedFailures;

    private void LogFailure(Exception ex)
    {
        var now = _clockMs();
        if (_lastLogMs != long.MinValue && now - _lastLogMs < LogGapMs)
        {
            return;
        }
        _lastLogMs = now;
        Interlocked.Increment(ref LoggedFailures);
        ServiceLog.Error($"[sensor-feed] poll failed: {ex.GetType().Name}: {ex.Message}");
    }

    private bool AnyWanted(long nowMs)
    {
        foreach (var ch in _channels.Values)
        {
            if (nowMs - ch.LastAskMs < IdleMs && ch.LastAskMs != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One poll of every wanted channel. False when none is wanted, which ends the poller.</summary>
    internal bool Tick(long nowMs)
    {
        List<KeyValuePair<string, Channel>>? wanted = null;
        foreach (var kv in _channels)
        {
            var ask = kv.Value.LastAskMs;
            if (ask != 0 && nowMs - ask < IdleMs)
            {
                (wanted ??= new List<KeyValuePair<string, Channel>>()).Add(kv);
            }
        }
        if (wanted is null)
        {
            return false;
        }
        SensorSnapshotSources sources = default;
        var extrasFailed = false;
        if (_resolve is null && _sensors is not null)
        {
            foreach (var kv in wanted)
            {
                var category = kv.Key[..kv.Key.IndexOf('\n')];
                if (SensorSnapshotResolver.CategoryUsesExtras(category))
                {
                    // One walk for every extras-backed binding. A walk that throws leaves those readings missing.
                    try
                    {
                        sources = new SensorSnapshotSources(Extras: _sensors.GetSensorExtras());
                    }
                    catch (Exception ex)
                    {
                        LogFailure(ex);
                        extrasFailed = true;
                    }
                    break;
                }
            }
        }
        foreach (var kv in wanted)
        {
            var split = kv.Key.IndexOf('\n');
            var category = kv.Key[..split];
            var sensor = kv.Key[(split + 1)..];
            float value;
            try
            {
                value = extrasFailed && SensorSnapshotResolver.CategoryUsesExtras(category)
                    ? float.NaN
                    : _resolve is not null
                        ? _resolve(category, sensor)
                        : SensorSnapshotResolver.Resolve(_sensors!, category, sensor, sources)?.Value ?? float.NaN;
            }
            catch (Exception)
            {
                value = float.NaN;
            }
            kv.Value.Publish(float.IsNaN(value) ? Missing : value);
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Cancel();
        }
    }
}
