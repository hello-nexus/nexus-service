using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Actions;
using Nexus.Service.Activity;
using Nexus.Service.Audio;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Media;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Deck;

/// <summary>
/// Service-side port of nexus-web's <c>deckExecutor.ts</c> plus the toggle
/// unwrap that lives client-side in <c>DeckWidget.tsx</c> (the naive TS
/// executor always fires a toggle's "on" branch; there is no React
/// component here to hold the unwrap, so this is the single entry point for
/// both a directly-bound toggle slot and a toggle nested in a sequence step).
/// </summary>
public sealed class DeckActionExecutor : IDeckActionExecutor
{
    private const double VolumeStepPercent = 5;
    private const double BrightnessStep = 10;
    private const int DefaultGapMs = 60;
    private const int DeckBrightnessStep = 10;

    private readonly SystemActions _system;
    private readonly ILightingDeviceProvider _lightingDevices;
    private readonly ILightingProvider _lighting;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly IY70Provider _y70;
    private readonly DisplayBrightnessController _displayBrightness;
    private readonly IMediaProvider _media;
    private readonly MultiplexHub _hub;
    private readonly Lazy<IDeckSurfaceControl> _deckSurface;
    private readonly AudioFilePlayer _audioPlayer;
    private readonly MediaLibrary _mediaLibrary;
    // The three ActivateLayoutPreset needs beyond what this class already
    // holds. RgbBridge is registered on the desktop platforms only, so it is
    // nullable here exactly as it is at the route and in AppPresetSwitcher.
    private readonly Nexus.Service.Lighting.Rgb.RgbBridge? _bridge;
    private readonly Nexus.Service.Lighting.Smart.SmartLightProvider _smart;
    private readonly Nexus.Service.Lighting.Engine.LightingEngine _engine;

    /// <summary>
    /// In-memory only, matching the web widget's per-tab <c>useState</c>
    /// flip: resets on service restart. Written only for toggle states with
    /// no live query (kind "internal" or unrecognized).
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _latches = new();

    private enum DispatchOutcome { Ok, Unknown }

    private readonly FeatureGates _gates;

    public DeckActionExecutor(
        SystemActions system,
        ILightingDeviceProvider lightingDevices,
        ILightingProvider lighting,
        IFanControlProvider fans,
        IConfigStore store,
        IY70Provider y70,
        DisplayBrightnessController displayBrightness,
        IMediaProvider media,
        MultiplexHub hub,
        Lazy<IDeckSurfaceControl> deckSurface,
        AudioFilePlayer audioPlayer,
        MediaLibrary mediaLibrary,
        Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
        Nexus.Service.Lighting.Smart.SmartLightProvider smart,
        Nexus.Service.Lighting.Engine.LightingEngine engine,
        FeatureGates? gates = null)
    {
        _system = system;
        _lightingDevices = lightingDevices;
        _lighting = lighting;
        _fans = fans;
        _store = store;
        _y70 = y70;
        _displayBrightness = displayBrightness;
        _media = media;
        _hub = hub;
        _deckSurface = deckSurface;
        _audioPlayer = audioPlayer;
        _mediaLibrary = mediaLibrary;
        _bridge = bridge;
        _smart = smart;
        _engine = engine;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    /// <summary>Test seam: the most recent dispatch's outcome ("ok" | "unknown" | "failed") and, for a failure, its error text - mirrors the [streamdeck] dispatch log line without needing a console-capture harness.</summary>
    internal (string Outcome, string? Error) LastOutcome { get; private set; }

    public async Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        if (action is null || string.IsNullOrEmpty(action.Type))
        {
            LastOutcome = ("unknown", null);
            ServiceLog.Info($"[streamdeck] dispatch serial={serial} key={keyIndex} type=none outcome=unknown");
            return;
        }

        string outcome;
        try
        {
            var result = await DispatchAsync(action, serial, latchKey, ct).ConfigureAwait(false);
            outcome = result == DispatchOutcome.Unknown ? "unknown" : "ok";
        }
        catch (Exception ex)
        {
            LastOutcome = ("failed", ex.Message);
            ServiceLog.Error($"[streamdeck] dispatch serial={serial} key={keyIndex} type={action.Type} outcome=failed ({ex.Message})");
            return;
        }
        LastOutcome = (outcome, null);
        ServiceLog.Info($"[streamdeck] dispatch serial={serial} key={keyIndex} type={action.Type} outcome={outcome}");
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey)
    {
        return TryLiveQuery(state, out var liveOn) ? liveOn : _latches.TryGetValue(latchKey, out var flip) && flip;
    }

    public void OpenApp() => _system.OpenDashboard();

    private async Task<DispatchOutcome> DispatchAsync(DeckAction action, string serial, string latchKey, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "launchApp":
                if (!string.IsNullOrEmpty(action.AppId))
                {
                    _system.LaunchShortcut(action.AppId);
                }
                return DispatchOutcome.Ok;
            case "openFile":
            case "openFolder":
                if (!string.IsNullOrEmpty(action.Path))
                {
                    await _system.OpenPathAsync(action.Path).ConfigureAwait(false);
                }
                return DispatchOutcome.Ok;
            case "openUrl":
                if (!string.IsNullOrEmpty(action.Url))
                {
                    await _system.OpenUrlAsync(action.Url).ConfigureAwait(false);
                }
                return DispatchOutcome.Ok;
            case "system":
                if (action.SystemAction is not null)
                {
                    await DispatchSystemAsync(action.SystemAction, ct).ConfigureAwait(false);
                }
                return DispatchOutcome.Ok;
            case "hotkey":
                await DispatchHotkeyAsync(action.Keys ?? "").ConfigureAwait(false);
                return DispatchOutcome.Ok;
            case "text":
            {
                var response = await _system.SendTextAsync(action.Text ?? "").ConfigureAwait(false);
                if (response.Error)
                {
                    throw new InvalidOperationException(response.Msg);
                }
                return DispatchOutcome.Ok;
            }
            case "power":
                DispatchPower(action.PowerAction);
                return DispatchOutcome.Ok;
            case "audioOutput":
                if (!string.IsNullOrEmpty(action.DeviceId))
                {
                    _system.SetDefaultOutput(action.DeviceId);
                }
                return DispatchOutcome.Ok;
            case "audioInput":
                if (!string.IsNullOrEmpty(action.DeviceId))
                {
                    _system.SetDefaultInput(action.DeviceId);
                }
                return DispatchOutcome.Ok;
            case "nexus":
                if (action.NexusAction is not null)
                {
                    DispatchNexus(action.NexusAction);
                }
                return DispatchOutcome.Ok;
            case "sequence":
                await DispatchSequenceAsync(action.Steps, serial, latchKey, ct).ConfigureAwait(false);
                return DispatchOutcome.Ok;
            case "toggle":
                return await DispatchToggleAsync(action, serial, latchKey, ct).ConfigureAwait(false);
            case "deckBrightness":
                DispatchDeckBrightness(action, serial);
                return DispatchOutcome.Ok;
            case "deckSleep":
                _deckSurface.Value.PutAsleep(serial);
                return DispatchOutcome.Ok;
            case "hotkeySwitch":
                await DispatchHotkeySwitchAsync(action, latchKey).ConfigureAwait(false);
                return DispatchOutcome.Ok;
            case "monitoring":
                DispatchMonitoringPress(action.Press);
                return DispatchOutcome.Ok;
            case "weather":
                return DispatchOutcome.Ok;
            case "playAudio":
                _audioPlayer.Play(action.Path, action.Volume);
                return DispatchOutcome.Ok;
            default:
                // "page" is worker-handled (StreamDeckConnectionWorker
                // intercepts it before ever reaching the executor) and
                // "pageIndicator" is display-only - both fall through to
                // Unknown here rather than getting a dedicated case.
                return DispatchOutcome.Unknown;
        }
    }

    /// <summary>
    /// deckBrightness: set applies action.Value directly; up/down adjust the
    /// currently persisted brightness by action.Step, falling back to
    /// DeckBrightnessStep when unset. Persists the clamped result and pushes
    /// it live to this deck's own surface.
    /// </summary>
    private void DispatchDeckBrightness(DeckAction action, string serial)
    {
        var settings = _store.Load().StreamDeck;
        var current = settings.Decks.TryGetValue(serial, out var deck) ? deck.Brightness : PhysicalDeckSettings.DefaultBrightness;
        int target;
        switch (action.Op)
        {
            case "set":
                if (action.Value is null)
                {
                    return;
                }
                target = action.Value.Value;
                break;
            case "up":
                target = current + (action.Step ?? DeckBrightnessStep);
                break;
            case "down":
                target = current - (action.Step ?? DeckBrightnessStep);
                break;
            default:
                return;
        }
        var clamped = Math.Clamp(target, 0, 100);
        _store.Update(s =>
        {
            if (!s.StreamDeck.Decks.TryGetValue(serial, out var d))
            {
                d = new PhysicalDeckSettings();
                s.StreamDeck.Decks[serial] = d;
            }
            d.Brightness = clamped;
        });
        _deckSurface.Value.SetBrightness(serial, clamped);
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });
    }

    /// <summary>
    /// Alternates between keysA and keysB on each press, using the same
    /// per-key in-memory latch the "internal" toggle state uses, then sends
    /// the chosen combo through the same path the "hotkey" action uses.
    /// </summary>
    private async Task DispatchHotkeySwitchAsync(DeckAction action, string latchKey)
    {
        var sendB = _latches.TryGetValue(latchKey, out var flip) && flip;
        _latches[latchKey] = !sendB;
        var keys = sendB ? action.KeysB : action.KeysA;
        if (!string.IsNullOrEmpty(keys))
        {
            await DispatchHotkeyAsync(keys).ConfigureAwait(false);
        }
    }

    private async Task<DispatchOutcome> DispatchToggleAsync(DeckAction action, string serial, string latchKey, CancellationToken ct)
    {
        if (action.On is null || action.Off is null)
        {
            return DispatchOutcome.Unknown;
        }
        var target = !IsToggleOn(action.State, latchKey);
        if (!TryLiveQuery(action.State, out _))
        {
            _latches[latchKey] = target;
        }
        return await DispatchAsync(target ? action.On : action.Off, serial, latchKey, ct).ConfigureAwait(false);
    }

    private async Task DispatchSequenceAsync(List<DeckSequenceStep>? steps, string serial, string latchKey, CancellationToken ct)
    {
        if (steps is null)
        {
            return;
        }
        foreach (var step in steps)
        {
            try
            {
                await DispatchAsync(step.Action, serial, latchKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[streamdeck] sequence step failed serial={serial}: {ex.Message}");
            }
            // User-configured pacing between steps, not a race-condition patch.
            var wait = (step.PressMs ?? 0) + (step.GapAfterMs ?? DefaultGapMs);
            if (wait > 0)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchHotkeyAsync(string keys)
    {
        var parsed = ParseHotkey(keys);
        if (parsed is null)
        {
            return;
        }
        var response = await _system.SendKeysAsync(new SendKeysBody
        {
            Key = parsed.Value.Key,
            Ctrl = parsed.Value.Ctrl,
            Shift = parsed.Value.Shift,
            Alt = parsed.Value.Alt,
            Meta = parsed.Value.Meta,
        }).ConfigureAwait(false);
        if (response.Error)
        {
            throw new InvalidOperationException(response.Msg);
        }
    }

    private void DispatchPower(string? powerAction)
    {
        switch (powerAction)
        {
            case "lock": _system.Lock(); return;
            case "sleep": _system.Sleep(); return;
            case "shutdown": _system.Shutdown(); return;
            case "restart": _system.Restart(); return;
            case "logout": _system.Logout(); return;
        }
    }

    /// <summary>none/absent is a no-op, matching the pageIndicator slot.</summary>
    private void DispatchMonitoringPress(string? press)
    {
        switch (press)
        {
            case "taskManager": _system.OpenTaskManager(); return;
            case "monitoringPage": _system.OpenDashboard(); return;
        }
    }

    private async Task DispatchSystemAsync(DeckSystemAction sa, CancellationToken ct)
    {
        switch (sa.Op)
        {
            case "volumeUp":
            case "volumeDown":
            {
                var cur = _system.GetVolume().Volume;
                var stepPercent = sa.Step ?? VolumeStepPercent;
                var next = Math.Clamp(cur + (sa.Op == "volumeUp" ? 1 : -1) * (stepPercent / 100.0), 0, 1);
                _system.SetVolume(next);
                PanelTopics.BroadcastVolume(_hub);
                return;
            }
            case "volumeSet":
                _system.SetVolume(Math.Clamp(sa.Value ?? 0, 0, 1));
                PanelTopics.BroadcastVolume(_hub);
                return;
            case "muteToggle":
                _system.SetMuted(!_system.GetVolume().Muted);
                PanelTopics.BroadcastVolume(_hub);
                return;
            case "openSettings":
                await _system.OpenSettingsAsync().ConfigureAwait(false);
                return;
            case "mediaPlayPause":
            case "mediaNext":
            case "mediaPrev":
            {
                var (source, playing) = ResolveMediaSession(sa.Source);
                if (string.IsNullOrEmpty(source))
                {
                    return;
                }
                var act = sa.Op == "mediaNext" ? "next" : sa.Op == "mediaPrev" ? "previous" : playing ? "pause" : "play";
                _media.Control(source, act);
                return;
            }
            case "brightnessUp":
            case "brightnessDown":
            case "brightnessSet":
            {
                if (string.IsNullOrEmpty(sa.DisplayId))
                {
                    return;
                }
                double brightness = sa.Value ?? 0;
                if (sa.Op != "brightnessSet")
                {
                    var cur = _displayBrightness.GetBrightness(sa.DisplayId) ?? 50;
                    var step = sa.Step ?? BrightnessStep;
                    brightness = cur + (sa.Op == "brightnessUp" ? 1 : -1) * step;
                }
                var clamped = (int)Math.Clamp(brightness, 0, 100);
                await _displayBrightness.SetBrightnessAsync(sa.DisplayId, clamped, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    private void DispatchNexus(DeckNexusAction na)
    {
        switch (na.Op)
        {
            case "rgbEffect":
                StartLightingMode(na);
                return;
            case "lightingBrightness":
            {
                var clamped = (float)Math.Clamp(na.Value ?? 1, 0, 1);
                _store.Update(s => s.Lighting.GlobalBrightness = clamped);
                PanelTopics.BroadcastLighting(_hub);
                return;
            }
            case "lightingPreset":
                if (_gates.Lighting && !string.IsNullOrEmpty(na.PresetId) &&
                    !Nexus.Service.Routes.DevicesRoutes.ActivateLayoutPreset(
                        na.PresetId, _store, _hub, _lightingDevices, _lighting, _bridge, _smart, _engine))
                {
                    ServiceLog.Warn($"[streamdeck] lighting preset not found: {na.PresetId}");
                }
                return;
            case "fanProfile":
                if (_gates.Cooling && !string.IsNullOrEmpty(na.Profile))
                {
                    FanProfiles.Apply(na.Profile, _fans, _store);
                    PanelTopics.BroadcastCooling(_hub);
                }
                return;
            case "coolingPreset":
                if (_gates.Cooling && !string.IsNullOrEmpty(na.PresetId))
                {
                    if (CoolingPresets.Activate(na.PresetId, _store, _fans))
                    {
                        PanelTopics.BroadcastCooling(_hub);
                    }
                    else
                    {
                        ServiceLog.Warn($"[streamdeck] cooling preset not found: {na.PresetId}");
                    }
                }
                return;
            case "y70Power":
                _y70.SetToggle(!(na.On ?? true));
                return;
            case "y70Brightness":
                _y70.SetBrightness((int)Math.Clamp(na.Value ?? 0, 0, 100));
                return;
            case "y70Rotation":
                if (!string.IsNullOrEmpty(na.Orientation))
                {
                    _y70.SetOrientation(na.Orientation);
                    _y70.ApplyEffectiveOrientation();
                }
                return;
        }
    }

    /// <summary>
    /// Starts one of the lighting page's three live modes, the same way that
    /// page's own mode switch does, so a key press and the UI land on the same
    /// state. Mirror passes no post-process: StartScreen deliberately keeps
    /// the saved one, so a key can never flatten the user's look.
    /// </summary>
    private void StartLightingMode(DeckNexusAction na)
    {
        switch (na.Mode ?? "animate")
        {
            case "gif":
                // No playable media: idle black keeps Media the active mode
                // rather than dropping the LEDs to Off.
                if (!PlayCurrentOrFirstMedia())
                {
                    _lighting.StartMediaIdle();
                }
                break;
            case "screen":
                _lighting.StartScreen(new Nexus.Service.Models.Lighting.ScreenHeadlessStart
                {
                    Monitor = "",
                    Effect = "average",
                });
                break;
            default:
                if (string.IsNullOrEmpty(na.Effect))
                {
                    return;
                }
                _lighting.StartAnimate(new Nexus.Service.Models.Lighting.AnimateHeadlessStart
                {
                    Effect = na.Effect,
                    Speed = 50,
                    Intensity = 1f,
                    Filter = "none",
                });
                break;
        }
        PanelTopics.BroadcastLighting(_hub);
    }

    /// <summary>Port of nexus-web's playCurrentOrFirstMedia: the last-played
    /// item, else the first in the library. False when neither plays.</summary>
    private bool PlayCurrentOrFirstMedia()
    {
        var lastId = _store.Load().Lighting.LastMediaId;
        if (!string.IsNullOrEmpty(lastId) && _lighting.StartMedia(lastId))
        {
            return true;
        }
        var first = _mediaLibrary.ListItems().FirstOrDefault();
        return first is not null && _lighting.StartMedia(first.Id);
    }

    private (string? Source, bool Playing) ResolveMediaSession(string? explicitSource)
    {
        var sessions = _media.GetSessions();
        if (!string.IsNullOrEmpty(explicitSource))
        {
            var playing = sessions.TryGetValue(explicitSource, out var found) && found.Playback.Playing;
            return (explicitSource, playing);
        }

        string? firstKey = null;
        var firstPlaying = false;
        foreach (var kv in sessions)
        {
            if (firstKey is null)
            {
                firstKey = kv.Key;
                firstPlaying = kv.Value.Playback.Playing;
            }
            if (kv.Value.IsFocused)
            {
                return (kv.Key, kv.Value.Playback.Playing);
            }
        }
        return (firstKey, firstPlaying);
    }

    private bool TryLiveQuery(DeckToggleState? state, out bool on)
    {
        on = false;
        switch (state?.Kind)
        {
            case "mute":
                on = _system.GetVolume().Muted;
                return true;
            case "lightingPower":
                if (string.IsNullOrEmpty(state.DeviceId))
                {
                    return false;
                }
                var device = _lightingDevices.GetAll().Devices.FirstOrDefault(d => d.Id == state.DeviceId);
                if (device is null)
                {
                    return false;
                }
                on = device.LedsOn;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ports deckExecutor.ts's parseHotkey: "ctrl+shift+m" -> a modifier set + a canonical key code.</summary>
    internal static (string Key, bool Ctrl, bool Shift, bool Alt, bool Meta)? ParseHotkey(string keys)
    {
        var tokens = keys.Split('+');
        var key = "";
        bool ctrl = false, shift = false, alt = false, meta = false;
        foreach (var raw in tokens)
        {
            var tok = raw.Trim().ToLowerInvariant();
            if (tok.Length == 0)
            {
                continue;
            }
            switch (tok)
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                case "option":
                case "opt":
                    alt = true;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                case "super":
                    meta = true;
                    break;
                default:
                    key = CanonicalKey(tok);
                    break;
            }
        }
        return key.Length == 0 ? null : (key, ctrl, shift, alt, meta);
    }

    private static string CanonicalKey(string tok)
    {
        if (tok.Length == 1 && tok[0] >= 'a' && tok[0] <= 'z')
        {
            return "Key" + char.ToUpperInvariant(tok[0]);
        }
        if (tok.Length == 1 && tok[0] >= '0' && tok[0] <= '9')
        {
            return "Digit" + tok;
        }
        if (tok.Length is 2 or 3 && tok[0] == 'f' && int.TryParse(tok.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return "F" + fn;
        }
        return tok switch
        {
            "space" => "Space",
            "enter" => "Enter",
            "return" => "Enter",
            "tab" => "Tab",
            "esc" => "Escape",
            "escape" => "Escape",
            "backspace" => "Backspace",
            "delete" => "Delete",
            "del" => "Delete",
            "insert" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            "up" => "ArrowUp",
            "down" => "ArrowDown",
            "left" => "ArrowLeft",
            "right" => "ArrowRight",
            "." => "Period",
            "," => "Comma",
            "/" => "Slash",
            ";" => "Semicolon",
            "'" => "Quote",
            "[" => "BracketLeft",
            "]" => "BracketRight",
            "\\" => "Backslash",
            "-" => "Minus",
            "=" => "Equal",
            "`" => "Backquote",
            "printscreen" => "PrintScreen",
            _ => "",
        };
    }
}
