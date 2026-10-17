using System;
using System.Diagnostics;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Shared per-device frame cap for first-party LED writers. A writer asks
/// <see cref="TryWrite(string, int, NexusSettings?)"/> once per device per tick, immediately before it would
/// send LED frame data, and skips the write when the answer is false. The writer
/// keeps composing from the engine every tick, so a skipped tick costs nothing and
/// the next passing tick sends the then-current frame: the last frame always lands
/// within one cap interval. Init, mode, bind, keepalive and recovery traffic never
/// goes through this gate.
/// With no user cap for the key the call returns true without touching any state,
/// so an uncapped device behaves exactly as it did before the gate existed.
/// </summary>
public sealed class LightingFrameCap
{
    private readonly IConfigStore _store;
    private readonly Func<long> _nowMs;
    private readonly PushRateLimiter<string> _limiter = new();

    public LightingFrameCap(IConfigStore store, Func<long>? nowMs = null)
    {
        _store = store;
        _nowMs = nowMs ?? (static () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Resolves the rate that applies: the user cap, held to a stricter device limit.
    /// Zero means the gate has nothing to do, either because there is no user cap or
    /// because the device's own limit is already at least as strict (the device layer
    /// enforces that limit itself).
    /// </summary>
    public static int EffectiveHz(int userHz, int builtInMaxHz)
    {
        if (userHz <= 0)
        {
            return 0;
        }
        if (builtInMaxHz > 0 && builtInMaxHz <= userHz)
        {
            return 0;
        }
        return userHz;
    }

    /// <summary>
    /// True when an LED frame for <paramref name="capKey"/> (the owning device: the cards' ParentDeviceId, else the card id)
    /// may be written now. <paramref name="settings"/> lets a writer reuse the snapshot it already loaded this tick.
    /// </summary>
    public bool TryWrite(string capKey, int builtInMaxHz = 0, NexusSettings? settings = null)
    {
        var rates = (settings ?? _store.Load()).Devices.LightingMaxRates;
        if (rates.Count == 0 || !rates.TryGetValue(capKey, out var userHz))
        {
            return true;
        }
        var hz = EffectiveHz(userHz, builtInMaxHz);
        return hz <= 0 || _limiter.TryPass(capKey, hz, _nowMs());
    }

    /// <summary>True when the user has set a cap for <paramref name="capKey"/>.</summary>
    public bool IsCapped(string capKey, NexusSettings settings)
        => settings.Devices.LightingMaxRates.TryGetValue(capKey, out var hz) && hz > 0;
}

/// <summary>
/// One tick's decision for one device. A writer that pushes several channels of the same device,
/// and drops some of them before the wire (unchanged frame), asks <see cref="Allow"/> per channel
/// that would really write: the first ask spends the device's single pass for the tick and every
/// later ask in the same tick shares it, so no channel starves behind another.
/// </summary>
public struct FrameCapTick
{
    private readonly LightingFrameCap? _cap;
    private readonly string _key;
    private readonly int _builtInMaxHz;
    private readonly NexusSettings? _settings;
    private bool _decided;
    private bool _open;

    public FrameCapTick(LightingFrameCap? cap, string capKey, int builtInMaxHz, NexusSettings? settings)
    {
        _cap = cap;
        _key = capKey;
        _builtInMaxHz = builtInMaxHz;
        _settings = settings;
        _decided = false;
        _open = true;
    }

    public bool Allow()
    {
        if (_cap is null)
        {
            return true;
        }
        if (!_decided)
        {
            _open = _cap.TryWrite(_key, _builtInMaxHz, _settings);
            _decided = true;
        }
        return _open;
    }
}
