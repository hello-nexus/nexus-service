using System;
using Nexus.Service.Lifecycle;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pushes the persisted keeb desired-state (game mode + firmware animation +
/// rotary) to the device as a single 0x06 settings write. Shared by:
///   - the REST provider, after any settings change,
///   - the connection worker, when the keeb first appears (so saved settings
///     take effect on plug-in),
///   - the lighting frame writer, when software streaming stops (re-asserts the
///     firmware animation, which streaming had suppressed).
/// Desired-state model: we always write the COMPLETE state from settings.json.
/// The one exception is device-initiated changes the host can't otherwise see -
/// the firmware rotary cycles the effect and moves the brightness byte without a
/// host callback - which <see cref="SyncFromDevice"/> reads back.
/// FirmwareLighting.Brightness is the keeb's own level. With the firmware
/// animation showing, the knob moves it on the device and
/// <see cref="SyncFromDevice"/> adopts the byte. While the frame writer streams, it scales the keeb stream by that level on top of the global master,
/// and the knob changes the global master only (<see cref="HandleEncoderScroll"/>,
/// <see cref="PollKnobToGlobal"/>): the level never flows into global.
/// </summary>
public sealed class KeebSettingsApplier
{
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _panel;
    // The knob can always be observed by reading the settings page, and may also
    // report each detent as an input event; both are handled (see
    // HandleEncoderScroll and PollKnobToGlobal). KeebHub's read would serialize
    // against the 30 Hz LED stream on one IO lock - a request/response pair
    // with a 250 ms timeout, so one slow answer stalls frames and the board
    // repaints the gap. This is a SECOND handle to the same collection, used
    // only for that read, so the stream's lock is never taken.
    private readonly IHidEnumerator? _hid;
    private readonly object _knobIo = new();
    private IHidDevice? _knobReader;
    // Serializes a host settings write (store mutate + byte write) against the
    // device read-back, so the poll can't read a pre-write byte and clobber the
    // value a Settings-slider write just stored before its byte reaches the device.
    private readonly object _gate = new();
    // Last device bytes SyncFromDevice saw. It adopts a byte only when it changes,
    // so a redundant read can't re-apply the same value and bounce the brightness.
    // Touched only under _gate.
    private int _lastBrightByte = -1;
    private int _lastAnimByte = -1;
    // Streaming knob position (device byte as a percent) last accounted for, under
    // _gate; -1 until the first read of a stream, which only records it.
    private int _lastKnobPct = -1;
    // Bumped under _gate by every detent and every re-baseline. A byte read happens
    // outside the lock, so a poll that saw the generation change while reading holds a
    // result from before that event and must discard it.
    private int _knobGen;
    // Set once a detent event has arrived since the last connect: the firmware reports
    // detents, so they alone step global and the byte poll stands down.
    private bool _detentsSeen;
    // Global points the byte poll has applied that a late detent event may still claim
    // (the poll can read the moved byte before the event arrives), and when.
    private int _pollCredit;
    private long _pollCreditAtMs;
    private const long PollCreditWindowMs = 1000;
    // Set while the frame writer streams (a software effect, or key reactions alone).
    // It is the one signal for "the firmware animation is suppressed and the knob
    // drives global": the provider, the knob handlers and SyncFromDevice all gate on it.
    // Any settings write that lands during a stream must not put an animated mode on
    // the device: the firmware
    // would repaint between our frames and fight the stream (camera-verified:
    // our yellow alternating with the firmware's animation).
    private volatile bool _streaming;

    private readonly FeatureGates _gates;

    public KeebSettingsApplier(KeebHub hub, IConfigStore store, MultiplexHub panel, IHidEnumerator? hid = null, FeatureGates? gates = null)
    {
        _hub = hub;
        _store = store;
        _panel = panel;
        _hid = hid;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    /// <summary>
    /// Read the settings page on the knob's own handle. Null when no handle can
    /// be opened, in which case the knob simply is not followed - it must never
    /// fall back to the hub's read, which is what stalled the stream.
    /// </summary>
    private byte[]? ReadSettingsOffStream()
    {
        if (_hid is null) return null;
        // Skip this tick rather than wait: an onboard layer/macro transaction
        // is in flight, and a settings request now would land inside it.
        if (!System.Threading.Monitor.TryEnter(_hub.OnboardLock)) return null;
        try
        {
            lock (_knobIo)
            {
                try
                {
                    if (_knobReader is null)
                    {
                        HidDeviceInfo? info = null;
                        foreach (var i in _hid.Find(KeebProtocol.VendorId, KeebProtocol.ProductId))
                            if (i.UsagePage == KeebProtocol.VendorUsagePage && i.Usage == KeebProtocol.VendorUsage) { info = i; break; }
                        if (info is null) return null;
                        _knobReader = _hid.Open(info.Path, forInput: true);
                        if (_knobReader is null) return null;
                        ServiceLog.Info("[keeb] knob reader opened on its own handle");
                    }
                    KeebHub.DrainStaleInput(_knobReader);
                    if (!_knobReader.SetFeature(KeebProtocol.SettingsReadFeature)) { DropKnobReader(); return null; }
                    var buf = new byte[KeebLayout.PageSize];
                    var n = _knobReader.Read(buf, 250);
                    if (n <= 0) { DropKnobReader(); return null; }
                    return buf.AsSpan(0, n).ToArray();
                }
                catch { DropKnobReader(); return null; }
            }
        }
        finally { System.Threading.Monitor.Exit(_hub.OnboardLock); }
    }

    private void DropKnobReader()
    {
        try { _knobReader?.Dispose(); } catch { /* best-effort */ }
        _knobReader = null;
    }

    /// <summary>True while the frame writer is streaming; see <c>_streaming</c>.</summary>
    public bool IsStreaming => _streaming;

    /// <summary>Build the page from current settings and write it. No-op (false) when disconnected.</summary>
    public bool Apply()
    {
        if (!_gates.Lighting) return false;
        if (!_hub.IsConnected) return false;
        try
        {
            lock (_gate)
            {
                var page = KeebSettingsCodec.BuildSettingsPage(_store.Load().Keeb);
                if (_streaming) page[3] = KeebSettingsCodec.AnimationModeByte("static");
                var ok = _hub.WriteSettings(page);
                if (ok)
                {
                    // The device now holds these bytes: SyncFromDevice must not read our own
                    // write back as a knob turn.
                    _lastAnimByte = page[3];
                    _lastBrightByte = page[4];
                }
                // During a stream the poll must not count the byte rewrite as a knob turn.
                if (_streaming) ResetKnobBaselineLocked();
                return ok;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] apply settings failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pin the firmware to its non-animated mode for the duration of a software
    /// stream, WITHOUT touching the persisted desired state.
    ///
    /// Streaming suppresses the onboard animation only while frames keep
    /// arriving. Any gap - a late frame, a settings read stealing the
    /// interface - lets the firmware repaint one of its own frames, and with
    /// the knob left on Rainbow/Wave that reads as a coloured flash across
    /// some or all keys (bench-hit: flicker in Static, never in Animation,
    /// because a moving host image hides the same interleave). Holding the
    /// firmware on Static makes a gap invisible instead of colourful.
    ///
    /// The persisted mode is untouched, so <see cref="Apply"/> restores the
    /// user's animation verbatim when streaming stops.
    /// </summary>
    public bool SuppressFirmwareAnimation()
    {
        _streaming = true;
        if (!_hub.IsConnected) return false;
        try
        {
            var settings = _store.Load().Keeb;
            var page = KeebSettingsCodec.BuildSettingsPage(settings);
            page[3] = KeebSettingsCodec.AnimationModeByte("static");
            lock (_gate)
            {
                // The write moves the device's anim byte; re-reference it so the
                // next SyncFromDevice does not read our own write back as a
                // user knob turn and persist Static over their choice.
                var ok = _hub.WriteSettings(page);
                if (ok) _lastAnimByte = page[3];
                ServiceLog.Info($"[keeb] firmware animation pinned to Static for the stream (was {settings.FirmwareLighting.AnimationMode}, ok={ok})");
                return ok;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] suppress firmware animation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply a knob detent to the global master brightness, host-side.
    ///
    /// The rotary can report its detents as interrupt-IN events on the input worker's
    /// OWN handle, which costs the LED stream nothing (reading the settings page shares
    /// KeebHub's IO lock and stalled frames). Whether or not the firmware also moves its
    /// byte per detent, the detent steps global once: it re-baselines the byte poll, so
    /// the poll only records the byte afterwards and never counts the same turn again.
    ///
    /// Only while the frame writer streams: the knob then steps the global master
    /// every device follows and leaves the keeb's own level alone, so the stream is
    /// global x level without squaring. With the firmware animation showing the
    /// device handles the knob itself and <see cref="SyncFromDevice"/> adopts the
    /// byte as the keeb level, so a detent changes nothing here. Returns false when
    /// this encoder is not assigned to brightness.
    /// </summary>
    public bool HandleEncoderScroll(KeebProtocol.KeebEncoder encoder, bool up)
    {
        if (encoder == KeebProtocol.KeebEncoder.None) return false;
        var settings = _store.Load().Keeb;
        var fn = encoder == KeebProtocol.KeebEncoder.Left ? settings.RotaryLeft : settings.RotaryRight;
        if (!string.Equals(fn, "BrightnessAdjustment", StringComparison.OrdinalIgnoreCase)) return false;

        var step = up ? KnobStepPercent : -KnobStepPercent;
        lock (_gate)
        {
            // The firmware reports detents, so from here the byte poll stands down.
            var firstEvent = !_detentsSeen;
            _detentsSeen = true;
            if (!_streaming) return true;
            // The poll may have read the moved byte just before this first event arrived;
            // that detent is already in global.
            var claimed = firstEvent && _pollCredit * step > 0 && Environment.TickCount64 - _pollCreditAtMs < PollCreditWindowMs;
            _pollCredit = 0;
            if (claimed)
            {
                ResetKnobBaselineLocked();
                return true;
            }
            _store.Update(s =>
            {
                var now = (int)Math.Round(s.Lighting.GlobalBrightness * 100);
                s.Lighting.GlobalBrightness = Math.Clamp(now + step, 0, 100) / 100f;
                Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
            });
            ResetKnobBaselineLocked();
        }
        PanelTopics.BroadcastLighting(_panel);
        return true;
    }

    /// <summary>Percent per rotary detent.</summary>
    private const int KnobStepPercent = 5;

    /// <summary>
    /// Hand the firmware animation back and restore it, as one gated step: a
    /// <see cref="SyncFromDevice"/> in between would adopt a knob-moved byte as the level.
    /// </summary>
    public bool EndStream()
    {
        lock (_gate)
        {
            _streaming = false;
            return Apply();
        }
    }

    /// <summary>
    /// Forget the knob position so the next read only records the byte as the baseline,
    /// whatever its value. The frame writer calls this when it starts streaming, so the keeb level never reads as a knob turn.
    /// </summary>
    public void ResetKnobBaseline()
    {
        lock (_gate) { ResetKnobBaselineLocked(); }
    }

    private void ResetKnobBaselineLocked()
    {
        _lastKnobPct = -1;
        _knobGen++;
    }

    /// <summary>
    /// Read the device's current animation + brightness bytes and record them
    /// as the sync baselines WITHOUT adopting them into the store. Called on
    /// (re)connect, where the persisted desired state must win (the device
    /// can't have changed while unplugged): the following <see cref="Apply"/>
    /// pushes the store, and later knob turns still register as byte CHANGES
    /// against these baselines and sync normally.
    /// </summary>
    public void SeedBaselines()
    {
        if (!_hub.IsConnected) return;
        lock (_gate)
        {
            // A reconnect may be a different firmware or a different state: learn again
            // whether it reports detents.
            _detentsSeen = false;
            _pollCredit = 0;
            var raw = _hub.ReadSettings();
            var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
            var brightIndex = animIndex + 1;
            if (raw is null || raw.Length <= brightIndex) return;
            _lastAnimByte = raw[animIndex];
            _lastBrightByte = raw[brightIndex];
        }
    }

    /// <summary>
    /// On a <paramref name="readByte"/> tick while the frame writer streams, read the
    /// firmware brightness byte. The first read of a stream only records the baseline;
    /// after that each change in the byte moves the global master by the same amount.
    /// The byte's absolute value is the keeb level and is never copied into global.
    /// Does nothing once a detent event has been seen: events alone then step global.
    /// The frame writer calls this.
    /// </summary>
    public void PollKnobToGlobal(bool readByte)
    {
        if (!readByte || !_hub.IsConnected) return;
        var changed = false;
        int generation;
        lock (_gate)
        {
            if (_detentsSeen) return;
            generation = _knobGen;
        }
        var raw = ReadSettingsOffStream();
        lock (_gate)
        {
            // A detent or a re-baseline landed while the read was in flight: the byte may
            // predate it, so drop this result and let the next read record the baseline.
            if (generation != _knobGen)
            {
                _lastKnobPct = -1;
                return;
            }
            var brightIndex = (OperatingSystem.IsWindows() ? 3 : 2) + 1;
            if (raw is null || raw.Length <= brightIndex) return;
            var pct = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
            var delta = _lastKnobPct < 0 ? 0 : pct - _lastKnobPct;
            _lastKnobPct = pct;
            if (delta == 0) return;
            _store.Update(s =>
            {
                var now = (int)Math.Round(s.Lighting.GlobalBrightness * 100);
                s.Lighting.GlobalBrightness = Math.Clamp(now + delta, 0, 100) / 100f;
                Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
            });
            _pollCredit = delta;
            _pollCreditAtMs = Environment.TickCount64;
            changed = true;
        }
        if (changed) PanelTopics.BroadcastLighting(_panel);
    }

    /// <summary>
    /// Mutate the persisted keeb state and (when <paramref name="writeDevice"/>)
    /// write the full page to the device, as one gated step so the read-back poll
    /// can't interleave between the store mutate and the byte write. Callers pass
    /// writeDevice=false while the frame writer streams: the 0x06 page write would
    /// flash the firmware animation through the live stream, and the frame writer
    /// re-writes the page when streaming stops anyway.
    /// </summary>
    public bool ApplyGated(Action<NexusSettings> mutate, bool writeDevice = true)
    {
        lock (_gate)
        {
            _store.Update(mutate);
            return writeDevice && Apply();
        }
    }

    /// <summary>
    /// Read the device's current firmware effect and brightness and mirror a
    /// device-initiated change into persisted state, so the panel follows the
    /// hardware. In firmware rotary mode the middle button cycles the effect and the
    /// knob moves the brightness byte with no host callback, so the connection worker
    /// calls this each poll tick. Adopts the byte into the keeb level
    /// (FirmwareLighting.Brightness) only - never the global master or the per-zone
    /// software prefs, which stay the user's LED-map values. Does nothing while the
    /// frame writer streams: the knob then drives global instead (see
    /// <see cref="PollKnobToGlobal"/>). The equality check makes our own writes a
    /// no-op, so only a hardware-side change broadcasts. Gated against
    /// <see cref="ApplyGated"/> so it can't clobber an in-flight host write.
    /// </summary>
    public bool SyncFromDevice()
    {
        if (_streaming || !_hub.IsConnected) return false;
        string? effect = null;
        var brightness = 0;
        var changed = false;
        lock (_gate)
        {
            // A stream may have started while this waited for the gate.
            if (_streaming) return false;
            var raw = _hub.ReadSettings();
            if (raw is null) return false;
            // Read layout (verified on the bench): Linux hidraw returns the 64-byte
            // page with NO report-id prefix (raw[0]=debounce, raw[2]=animation,
            // raw[3]=brightness); Windows HID prepends the report id (+1 to each).
            var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
            var brightIndex = animIndex + 1;
            if (raw.Length <= brightIndex) return false;

            int animByte = raw[animIndex];
            int brightByte = raw[brightIndex];
            // Adopt only what the device actually changed since the last read. An
            // unchanged byte is skipped, so two pollers reading the same value never
            // fight, and a host-set level (written to the store but not the device
            // while streaming) survives reading back the unchanged knob byte.
            var animChanged = animByte != _lastAnimByte;
            var brightChanged = brightByte != _lastBrightByte;
            _lastAnimByte = animByte;
            _lastBrightByte = brightByte;
            if (!animChanged && !brightChanged) return false;

            effect = KeebSettingsCodec.AnimationModeName((byte)animByte);
            brightness = KeebSettingsCodec.BrightnessPercentFromByte((byte)brightByte);
            // The byte is the firmware animation brightness; adopt it as the keeb
            // level. Both workers call this unconditionally; it does nothing while the
            // frame writer streams, which follows the knob into global instead, see PollKnobToGlobal.
            _store.Update(s =>
            {
                if (animChanged && effect is not null
                    && !string.Equals(s.Keeb.FirmwareLighting.AnimationMode, effect, StringComparison.OrdinalIgnoreCase))
                {
                    s.Keeb.FirmwareLighting.AnimationMode = effect;
                    changed = true;
                }
                if (brightChanged && s.Keeb.FirmwareLighting.Brightness != brightness)
                {
                    s.Keeb.FirmwareLighting.Brightness = brightness;
                    changed = true;
                }
            });
        }
        if (changed)
        {
            ServiceLog.Info($"[keeb] device state synced -> effect={effect} brightness={brightness}");
            PanelTopics.BroadcastLighting(_panel);
        }
        return changed;
    }
}
