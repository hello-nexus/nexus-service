using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>One keyboard frame that can carry key reactions, and the device its config is stored under.</summary>
public readonly record struct KeyReactiveKeyboard(string Id, string DeviceId, int FrameIndex, int LedCount, int NamedKeys);

/// <summary>
/// Lights keyboards as keys are pressed. Configs are stored per physical device
/// (<see cref="DeviceFrame.DeviceId"/>), so re-partitioning a board keeps them.
/// Presses queue from any thread; the engine paints enabled boards between the
/// canvas sample and the colour locks, so every keyboard writer carries them.
/// </summary>
public sealed class KeyReactiveOverlay
{
    // DeviceId null = an OS key press for every board without its own key source;
    // CardId set = a simulated press on that card.
    private readonly record struct PendingPress(string? DeviceId, string? CardId, string? Key, int? Led, long AtMs);

    private sealed class BoardState
    {
        public KeyboardGeometry? Geometry;
        public float[]? U;
        public float[]? V;
        public string?[]? Names;
        public bool[]? Disabled;
        public string Effect = "";
        public readonly KeyReactionRenderer Renderer = new();
    }

    // Presses queue while nothing drains them (no engine effect, paused), so the
    // queue is capped and a drain skips presses older than any reaction lasts.
    private const int MaxPending = 128;
    private const long StalePressMs = 1500;

    private readonly IConfigStore? _store;
    private readonly object _hydrateLock = new();
    // Guards board state: the engine paints under it, and so does a writer
    // painting on its own while no effect runs.
    private readonly object _paintLock = new();
    private readonly ConcurrentQueue<PendingPress> _pending = new();
    private readonly Dictionary<string, BoardState> _boards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _hardwareSources = new(StringComparer.Ordinal);
    private readonly Random _rng = new();
    private Dictionary<string, KeyReaction> _configs = new(StringComparer.Ordinal);
    private bool _armed;

    /// <summary>Raised when the OS key source should start or stop listening; handlers run under the overlay's config lock and must not block.</summary>
    public event Action<bool>? ArmedChanged;

    public KeyReactiveOverlay(IConfigStore? store = null)
    {
        _store = store;
        if (store is null) return;
        Hydrate();
        store.OnChanged += Hydrate;
    }

    /// <summary>Reports whether a live OS key source is connected; unset on platforms without one.</summary>
    public Func<bool>? InputAvailable { get; set; }

    /// <summary>True while an enabled board relies on the OS key source (it has no key source of its own).</summary>
    public bool Armed => Volatile.Read(ref _armed);

    /// <summary>The id a frame's config is stored under.</summary>
    public static string ConfigKey(DeviceFrame dev) => dev.DeviceId ?? dev.Id;

    /// <summary>Config for <paramref name="deviceId"/>, sanitised; disabled defaults when none is stored.</summary>
    public KeyReaction Get(string deviceId) =>
        Volatile.Read(ref _configs).TryGetValue(deviceId, out var cfg) ? cfg : KeyReactionCatalog.Sanitize(null);

    /// <summary>True when <paramref name="deviceId"/> has reactions on.</summary>
    public bool IsEnabled(string deviceId) =>
        Volatile.Read(ref _configs).TryGetValue(deviceId, out var cfg) && cfg.Enabled;

    /// <summary>Persists <paramref name="cfg"/> for <paramref name="deviceId"/> and returns the stored, sanitised copy.</summary>
    public KeyReaction Set(string deviceId, KeyReaction cfg)
    {
        var clean = KeyReactionCatalog.Sanitize(cfg);
        if (_store is null)
        {
            lock (_hydrateLock)
            {
                var next = new Dictionary<string, KeyReaction>(Volatile.Read(ref _configs), StringComparer.Ordinal) { [deviceId] = clean };
                Publish(next);
            }
            return clean;
        }
        _store.Update(s => s.Lighting.KeyReactions[deviceId] = clean);
        Hydrate();
        return clean;
    }

    /// <summary>
    /// Marks <paramref name="deviceId"/> as reporting its own key presses (the
    /// keeb's key-matrix callbacks). While it does, OS key presses skip it, so
    /// a press is never counted twice and it reacts only to its own keys.
    /// </summary>
    public void SetHardwareKeySource(string deviceId, bool present)
    {
        lock (_hydrateLock)
        {
            if (present) _hardwareSources[deviceId] = true;
            else _hardwareSources.TryRemove(deviceId, out _);
            Publish(Volatile.Read(ref _configs));
        }
    }

    public bool HasHardwareKeySource(string deviceId) => _hardwareSources.ContainsKey(deviceId);

    /// <summary>An OS key press; every enabled board without its own key source reacts. <paramref name="key"/> is a canonical <see cref="KeyNames"/> name.</summary>
    public void PressKey(string key)
    {
        if (!Armed || string.IsNullOrEmpty(key)) return;
        Enqueue(new PendingPress(null, null, key, null, Environment.TickCount64));
    }

    /// <summary>A press reported by <paramref name="deviceId"/>'s own hardware.</summary>
    public void PressFromDevice(string deviceId, string key)
    {
        if (string.IsNullOrEmpty(key) || !IsEnabled(deviceId)) return;
        Enqueue(new PendingPress(deviceId, null, key, null, Environment.TickCount64));
    }

    /// <summary>A simulated press on one card: at <paramref name="key"/>, else at <paramref name="led"/>, else a random key.</summary>
    public void PressOn(string cardId, string? key, int? led = null) =>
        Enqueue(new PendingPress(null, cardId, key, led, Environment.TickCount64));

    private void Enqueue(PendingPress press)
    {
        _pending.Enqueue(press);
        while (_pending.Count > MaxPending && _pending.TryDequeue(out _)) { }
    }

    /// <summary>The frames in <paramref name="devices"/> that are per-key keyboards.</summary>
    public static List<KeyReactiveKeyboard> Keyboards(DeviceFrame[] devices)
    {
        var list = new List<KeyReactiveKeyboard>();
        foreach (var dev in devices)
        {
            if (!IsKeyboard(dev)) continue;
            var named = 0;
            if (dev.LedKeys is { } keys)
            {
                foreach (var k in keys) if (KeyNames.Normalize(k) is not null) named++;
            }
            list.Add(new KeyReactiveKeyboard(dev.Id, ConfigKey(dev), dev.Index, dev.LedCount, named));
        }
        return list;
    }

    /// <summary>Same geometry the engine pass uses, for <paramref name="dev"/>; null when it is not a per-key keyboard.</summary>
    public static KeyboardGeometry? GeometryOf(DeviceFrame dev) =>
        IsKeyboard(dev) ? KeyboardGeometry.Build(dev.LedU!, dev.LedV!, dev.LedKeys, dev.LedDisabled) : null;

    public static bool IsKeyboard(DeviceFrame dev) =>
        dev.Archetype == "keyboard"
        && dev.LedCount >= KeyReactionCatalog.MinKeyLeds
        && dev.LedU is { } u && dev.LedV is { } v
        && u.Length == dev.LedCount && v.Length == dev.LedCount;

    /// <summary>Engine thread: drain presses and paint every enabled board not in <paramref name="skip"/>. True when any frame was repainted.</summary>
    public bool Apply(DeviceFrame[] devices, bool[]? skip, long nowMs)
    {
        var configs = Volatile.Read(ref _configs);
        if (configs.Count == 0)
        {
            _pending.Clear();
            return false;
        }
        lock (_paintLock)
        {
            DrainPresses(devices, configs, nowMs);
            var painted = false;
            for (var di = 0; di < devices.Length; di++)
            {
                if (skip is not null && di < skip.Length && skip[di]) continue;
                painted |= Paint(devices[di], configs, nowMs, standalone: false);
            }
            return painted;
        }
    }

    /// <summary>
    /// For a writer whose device runs with no engine effect (the keeb on its
    /// onboard animation): paints reactions over black into the paint buffers of
    /// <paramref name="frames"/>, which the caller owns and publishes. False when
    /// none of them has reactions on.
    /// </summary>
    public bool PaintStandalone(DeviceFrame[] frames, long nowMs)
    {
        var configs = Volatile.Read(ref _configs);
        var any = false;
        lock (_paintLock)
        {
            DrainPresses(frames, configs, nowMs);
            foreach (var dev in frames)
            {
                if (!configs.TryGetValue(ConfigKey(dev), out var cfg) || !cfg.Enabled) continue;
                dev.Fill(0, 0, 0);
                Paint(dev, configs, nowMs, standalone: true);
                any = true;
            }
        }
        return any;
    }

    private bool Paint(DeviceFrame dev, Dictionary<string, KeyReaction> configs, long nowMs, bool standalone)
    {
        if (!configs.TryGetValue(ConfigKey(dev), out var cfg) || !cfg.Enabled) return false;
        var board = BoardFor(dev);
        if (board?.Geometry is not { } geo) return false;
        SyncEffect(board, cfg);
        var drew = board.Renderer.Render(geo, cfg, nowMs);
        // Over black there is nothing to reveal, so reveal paints the reaction instead.
        var background = standalone && cfg.Background == KeyReactionCatalog.BackgroundReveal
            ? KeyReactionCatalog.BackgroundEffect
            : cfg.Background;
        // A plain overlay with nothing drawn leaves the frame alone; the other
        // backgrounds repaint the whole board every frame.
        if (!drew && background == KeyReactionCatalog.BackgroundEffect) return false;
        board.Renderer.Composite(dev.PaintBuffer, dev.LedCount, background);
        return true;
    }

    private void DrainPresses(DeviceFrame[] devices, Dictionary<string, KeyReaction> configs, long nowMs)
    {
        while (_pending.TryDequeue(out var press))
        {
            if (nowMs - press.AtMs > StalePressMs) continue;
            foreach (var dev in devices)
            {
                var key = ConfigKey(dev);
                if (press.CardId is not null && press.CardId != dev.Id) continue;
                if (press.DeviceId is not null && press.DeviceId != key) continue;
                if (press.DeviceId is null && press.CardId is null && _hardwareSources.ContainsKey(key)) continue;
                if (!configs.TryGetValue(key, out var cfg) || !cfg.Enabled) continue;
                var board = BoardFor(dev);
                if (board?.Geometry is not { } geo) continue;
                SyncEffect(board, cfg);
                var led = press.Key is not null ? geo.Resolve(press.Key)
                    : press.Led is { } index ? index
                    : RandomLed(geo);
                if (led >= 0) board.Renderer.Press(geo, led, cfg, press.AtMs);
            }
        }
    }

    private BoardState? BoardFor(DeviceFrame dev)
    {
        if (!IsKeyboard(dev)) return null;
        if (!_boards.TryGetValue(dev.Id, out var board))
        {
            board = new BoardState();
            _boards[dev.Id] = board;
        }
        // Layout arrays are swapped whole on change, so reference checks catch every edit.
        if (board.Geometry is null || !ReferenceEquals(board.U, dev.LedU) || !ReferenceEquals(board.V, dev.LedV)
            || !ReferenceEquals(board.Names, dev.LedKeys) || !ReferenceEquals(board.Disabled, dev.LedDisabled))
        {
            board.U = dev.LedU;
            board.V = dev.LedV;
            board.Names = dev.LedKeys;
            board.Disabled = dev.LedDisabled;
            board.Geometry = KeyboardGeometry.Build(dev.LedU!, dev.LedV!, dev.LedKeys, dev.LedDisabled);
            board.Renderer.Reset();
        }
        return board;
    }

    private static void SyncEffect(BoardState board, KeyReaction cfg)
    {
        if (board.Effect == cfg.Effect) return;
        board.Effect = cfg.Effect;
        board.Renderer.Reset();
    }

    private int RandomLed(KeyboardGeometry geo)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var led = _rng.Next(geo.LedCount);
            if (geo.IsActive(led)) return led;
        }
        return -1;
    }

    private void Hydrate()
    {
        var store = _store;
        if (store is null) return;
        lock (_hydrateLock)
        {
            // Load() hands back the live document, so a write landing mid-read
            // can invalidate the enumeration; the next attempt sees it whole.
            Dictionary<string, KeyReaction> next;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    next = new Dictionary<string, KeyReaction>(StringComparer.Ordinal);
                    foreach (var (id, cfg) in store.Load().Lighting.KeyReactions)
                    {
                        if (!string.IsNullOrEmpty(id)) next[id] = KeyReactionCatalog.Sanitize(cfg);
                    }
                    break;
                }
                catch (InvalidOperationException) when (attempt < 3)
                {
                }
            }
            Publish(next);
        }
    }

    private void Publish(Dictionary<string, KeyReaction> next)
    {
        Volatile.Write(ref _configs, next);
        var armed = false;
        foreach (var (id, cfg) in next)
        {
            if (cfg.Enabled && !_hardwareSources.ContainsKey(id)) { armed = true; break; }
        }
        // Callers hold _hydrateLock, so arm changes are raised in the order they happen.
        if (armed == Volatile.Read(ref _armed)) return;
        Volatile.Write(ref _armed, armed);
        ArmedChanged?.Invoke(armed);
    }
}
