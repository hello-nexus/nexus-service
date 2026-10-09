using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Audio;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Deck;

/// <summary>Dial action type ids (DeckDialAction.Type) and the groups the dispatcher branches on.</summary>
public static class DeckDialTypes
{
    public const string Volume = "volume";
    public const string MicVolume = "micVolume";
    public const string AppVolume = "appVolume";
    public const string DisplayBrightness = "displayBrightness";
    public const string DeckBrightness = "deckBrightness";
    public const string LightingBrightness = "lightingBrightness";
    public const string Y70Brightness = "y70Brightness";
    public const string Page = "page";
    public const string Monitoring = "monitoring";
    public const string Custom = "custom";

    /// <summary>Types whose turn moves a 0-100 value and whose press mutes.</summary>
    public static bool IsMuteType(string type) => type is Volume or MicVolume or AppVolume;

    /// <summary>Types whose press toggles between zero and the last non-zero value.</summary>
    public static bool IsZeroToggleType(string type) => type is DisplayBrightness or LightingBrightness or Y70Brightness;

    /// <summary>Types that read and write a 0-100 value through <see cref="IDeckDialValues"/> (deckBrightness is the worker's own).</summary>
    public static bool IsServiceValueType(string type) => IsMuteType(type) || IsZeroToggleType(type);

    /// <summary>Targets whose read is a slow bus transaction (DDC/CI, serial).</summary>
    public static bool IsSlowType(string type) => type is DisplayBrightness or Y70Brightness;

    public static bool IsValueType(string type) => IsServiceValueType(type) || type == DeckBrightness;
}

/// <summary>One dial target's current level, 0-100. Pending means the first read has not finished; Supported false renders "--".</summary>
public readonly record struct DialReading(bool Supported, bool Pending, double Percent, bool Muted)
{
    public static DialReading Unsupported => new(false, false, 0, false);
    public static DialReading PendingRead => new(true, true, 0, false);
}

/// <summary>Reads and writes the value a dial controls without ever blocking the input thread.</summary>
public interface IDeckDialValues
{
    /// <summary>Last known level, optimistic for 1.5 s after a Write; refreshes in the background.</summary>
    DialReading Read(DeckDialAction action);

    /// <summary>Sets the level (0-100). Returns at once; a slow target keeps only the latest request.</summary>
    void Write(DeckDialAction action, double percent);

    /// <summary>Mutes or unmutes a volume-type target.</summary>
    void SetMuted(DeckDialAction action, bool muted);
}

/// <summary>Runs <paramref name="write"/> for the newest submitted value, never queueing one call per submit.</summary>
internal sealed class LatestValueWriter
{
    private readonly Func<double, Task> _write;
    private readonly object _gate = new();
    private double? _pending;
    private bool _running;

    public LatestValueWriter(Func<double, Task> write) => _write = write;

    public void Submit(double value)
    {
        lock (_gate)
        {
            _pending = value;
            if (_running)
            {
                return;
            }
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            double value;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _running = false;
                    return;
                }
                value = next;
                _pending = null;
            }
            try
            {
                await _write(value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[streamdeck] dial write failed: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Dial value access over the same providers the key actions use. Reads are
/// cached and refreshed off the caller's thread (DDC and CoreAudio reads can
/// stall), writes are optimistic and coalesced per target. micVolume works on
/// Windows only: the macOS volume provider reads the output scope and the
/// Linux one ignores a device id, so neither can address a capture device.
/// </summary>
public sealed class DeckDialValueService : IDeckDialValues
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMilliseconds(800);
    /// <summary>DDC/CI and the Y70 serial link are read rarely; a write schedules the confirming read.</summary>
    private static readonly TimeSpan SlowRefreshAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OptimisticFor = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultInputTtl = TimeSpan.FromSeconds(5);

    private readonly IVolumeProvider _volume;
    private readonly IAudioDeviceProvider _devices;
    private readonly AudioMixerService _mixer;
    private readonly DisplayBrightnessController _displays;
    private readonly IConfigStore _store;
    private readonly IY70Provider _y70;
    private readonly MultiplexHub _hub;
    private readonly TimeProvider _clock;

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LatestValueWriter> _writers = new(StringComparer.Ordinal);
    private readonly object _inputLock = new();
    private (string Id, DateTimeOffset At)? _defaultInput;

    private sealed class Entry
    {
        private readonly object _gate = new();
        private DialReading _reading;
        private DateTimeOffset _readAt;
        private DateTimeOffset _writtenAt = DateTimeOffset.MinValue;
        private int _version;
        public int Refreshing;

        /// <summary>Counts writes; a read that started under an older version is stale.</summary>
        public int Version
        {
            get { lock (_gate) { return _version; } }
        }

        public Entry(DialReading reading, DateTimeOffset readAt)
        {
            _reading = reading;
            _readAt = readAt;
        }

        public DialReading Reading
        {
            get { lock (_gate) { return _reading; } }
        }

        public (DateTimeOffset ReadAt, DateTimeOffset WrittenAt) Times
        {
            get { lock (_gate) { return (_readAt, _writtenAt); } }
        }

        /// <summary>Applies a live read unless a write landed since it started or inside the optimistic window; a discarded read leaves the read time alone so the confirming read still happens.</summary>
        public void ApplyRead(DialReading live, DateTimeOffset now, TimeSpan optimisticFor, int startedAtVersion)
        {
            lock (_gate)
            {
                if (_version == startedAtVersion && now - _writtenAt > optimisticFor)
                {
                    _reading = live;
                    _readAt = now;
                }
            }
        }

        public void Update(Func<DialReading, DialReading> change, DateTimeOffset? writtenAt = null, DateTimeOffset? readAt = null)
        {
            lock (_gate)
            {
                _reading = change(_reading);
                if (writtenAt is not null)
                {
                    _version++;
                }
                _writtenAt = writtenAt ?? _writtenAt;
                _readAt = readAt ?? _readAt;
            }
        }
    }

    public DeckDialValueService(
        IVolumeProvider volume,
        IAudioDeviceProvider devices,
        AudioMixerService mixer,
        DisplayBrightnessController displays,
        IConfigStore store,
        IY70Provider y70,
        MultiplexHub hub,
        TimeProvider? clock = null)
    {
        _volume = volume;
        _devices = devices;
        _mixer = mixer;
        _displays = displays;
        _store = store;
        _y70 = y70;
        _hub = hub;
        _clock = clock ?? TimeProvider.System;
    }

    public DialReading Read(DeckDialAction action)
    {
        if (!DeckDialTypes.IsServiceValueType(action.Type))
        {
            return DialReading.Unsupported;
        }
        var key = TargetKey(action);
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(key, out var entry))
        {
            var (readAt, writtenAt) = entry.Times;
            var refreshAfter = DeckDialTypes.IsSlowType(action.Type) ? SlowRefreshAfter : RefreshAfter;
            if (now - writtenAt > OptimisticFor && now - readAt > refreshAfter
                && Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) == 0)
            {
                _ = Task.Run(() => Refresh(action, entry));
            }
            return entry.Reading;
        }

        // The first read never blocks the caller (the input thread holds the worker lock); the next state-key diff repaints.
        var fresh = new Entry(DialReading.PendingRead, now) { Refreshing = 1 };
        if (!_cache.TryAdd(key, fresh))
        {
            return _cache[key].Reading;
        }
        _ = Task.Run(() => Refresh(action, fresh));
        return DialReading.PendingRead;
    }

    private void Refresh(DeckDialAction action, Entry entry)
    {
        try
        {
            var version = entry.Version;
            entry.ApplyRead(ReadLive(action), _clock.GetUtcNow(), OptimisticFor, version);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[streamdeck] dial read failed ({action.Type}): {ex.Message}");
            entry.Update(_ => DialReading.Unsupported, readAt: _clock.GetUtcNow());
        }
        finally
        {
            Volatile.Write(ref entry.Refreshing, 0);
        }
    }

    private DialReading ReadLive(DeckDialAction action)
    {
        switch (action.Type)
        {
            case DeckDialTypes.Volume:
                return FromVolume(string.IsNullOrEmpty(action.DeviceId) ? _volume.GetState() : _volume.GetState(action.DeviceId));
            case DeckDialTypes.MicVolume:
            {
                var id = MicDeviceId(action);
                return id.Length == 0 ? DialReading.Unsupported : FromVolume(_volume.GetState(id));
            }
            case DeckDialTypes.AppVolume:
            {
                var state = _mixer.GetState();
                var session = state.Supported
                    ? state.Sessions.FirstOrDefault(s => string.Equals(s.Id, action.AppId, StringComparison.OrdinalIgnoreCase))
                    : null;
                return session is null ? DialReading.Unsupported : new DialReading(true, false, session.Volume * 100, session.Muted);
            }
            case DeckDialTypes.DisplayBrightness:
                return string.IsNullOrEmpty(action.DisplayId) || _displays.GetBrightness(action.DisplayId) is not { } display
                    ? DialReading.Unsupported
                    : new DialReading(true, false, display, false);
            case DeckDialTypes.LightingBrightness:
                return new DialReading(true, false, _store.Load().Lighting.GlobalBrightness * 100.0, false);
            case DeckDialTypes.Y70Brightness:
                return _y70.IsConnected() ? new DialReading(true, false, _y70.GetBrightness(), false) : DialReading.Unsupported;
            default:
                return DialReading.Unsupported;
        }
    }

    private static DialReading FromVolume(Nexus.Service.Models.Activity.VolumeState state) =>
        state.Supported ? new DialReading(true, false, Math.Clamp(state.Volume, 0, 1) * 100, state.Muted) : DialReading.Unsupported;

    /// <summary>The configured capture endpoint, else the default input's id. Empty off Windows (see the class comment).</summary>
    private string MicDeviceId(DeckDialAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }
        if (!string.IsNullOrEmpty(action.DeviceId))
        {
            return action.DeviceId;
        }
        var now = _clock.GetUtcNow();
        lock (_inputLock)
        {
            if (_defaultInput is { } cached && now - cached.At < DefaultInputTtl)
            {
                return cached.Id;
            }
        }
        var id = _devices.ListDevices().Inputs.FirstOrDefault(d => d.IsDefault)?.Id ?? "";
        lock (_inputLock)
        {
            _defaultInput = (id, now);
        }
        return id;
    }

    public void Write(DeckDialAction action, double percent)
    {
        if (!DeckDialTypes.IsServiceValueType(action.Type))
        {
            return;
        }
        var clamped = Math.Clamp(percent, 0, 100);
        var key = TargetKey(action);
        var now = _clock.GetUtcNow();
        var entry = _cache.GetOrAdd(key, _ => new Entry(new DialReading(true, false, clamped, false), now));
        // ReadAt resets so a read confirms the write once the optimistic window ends.
        entry.Update(r => r with { Supported = true, Pending = false, Percent = clamped }, writtenAt: now, readAt: DateTimeOffset.MinValue);
        _writers.GetOrAdd(key, _ => new LatestValueWriter(value => WriteLiveAsync(action, value))).Submit(clamped);
    }

    private async Task WriteLiveAsync(DeckDialAction action, double percent)
    {
        switch (action.Type)
        {
            case DeckDialTypes.Volume:
                if (string.IsNullOrEmpty(action.DeviceId))
                {
                    _volume.SetVolume(percent / 100.0);
                }
                else
                {
                    _volume.SetVolume(action.DeviceId, percent / 100.0);
                }
                PanelTopics.BroadcastVolume(_hub);
                return;
            case DeckDialTypes.MicVolume:
            {
                var id = MicDeviceId(action);
                if (id.Length > 0)
                {
                    _volume.SetVolume(id, percent / 100.0);
                }
                return;
            }
            case DeckDialTypes.AppVolume:
                if (!string.IsNullOrEmpty(action.AppId))
                {
                    _mixer.SetVolume(action.AppId, percent / 100.0, commit: true);
                }
                return;
            case DeckDialTypes.DisplayBrightness:
                if (!string.IsNullOrEmpty(action.DisplayId))
                {
                    await _displays.SetBrightnessAsync(action.DisplayId, (int)Math.Round(percent)).ConfigureAwait(false);
                }
                return;
            case DeckDialTypes.LightingBrightness:
            {
                var value = (float)(percent / 100.0);
                _store.Update(s => s.Lighting.GlobalBrightness = value);
                PanelTopics.BroadcastLighting(_hub);
                return;
            }
            case DeckDialTypes.Y70Brightness:
                _y70.SetBrightness((int)Math.Round(percent));
                return;
        }
    }

    public void SetMuted(DeckDialAction action, bool muted)
    {
        if (!DeckDialTypes.IsMuteType(action.Type))
        {
            return;
        }
        var key = TargetKey(action);
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.Update(r => r with { Muted = muted }, writtenAt: _clock.GetUtcNow());
        }
        _ = Task.Run(() =>
        {
            try
            {
                switch (action.Type)
                {
                    case DeckDialTypes.Volume:
                        if (string.IsNullOrEmpty(action.DeviceId))
                        {
                            _volume.SetMuted(muted);
                        }
                        else
                        {
                            _volume.SetMuted(action.DeviceId, muted);
                        }
                        PanelTopics.BroadcastVolume(_hub);
                        break;
                    case DeckDialTypes.MicVolume:
                        if (MicDeviceId(action) is { Length: > 0 } id)
                        {
                            _volume.SetMuted(id, muted);
                        }
                        break;
                    case DeckDialTypes.AppVolume:
                        if (!string.IsNullOrEmpty(action.AppId))
                        {
                            _mixer.SetMuted(action.AppId, muted);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[streamdeck] dial mute failed ({action.Type}): {ex.Message}");
            }
        });
    }

    /// <summary>Identity of the thing a dial controls, so two dials on one target share a cache entry and writer.</summary>
    internal static string TargetKey(DeckDialAction action) => action.Type switch
    {
        DeckDialTypes.Volume => "volume:" + action.DeviceId,
        DeckDialTypes.MicVolume => "mic:" + action.DeviceId,
        DeckDialTypes.AppVolume => "app:" + action.AppId,
        DeckDialTypes.DisplayBrightness => "display:" + action.DisplayId,
        _ => action.Type,
    };
}
