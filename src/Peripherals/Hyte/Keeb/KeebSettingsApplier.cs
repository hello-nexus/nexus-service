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
/// FirmwareLighting.Brightness dims only the firmware animation (the byte), so
/// when the firmware animation is showing the knob drives it directly. While a
/// software effect streams the firmware animation is suppressed; global then mirrors the
/// firmware byte's 0-100 position via <see cref="PollKnobToGlobal"/> - one value, so there
/// is no separate software level to drift out of sync with the byte.
/// </summary>
public sealed class KeebSettingsApplier
{
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _panel;
    // The knob is only observable by reading the settings page: this keyboard
    // handles the rotary internally and reports no input event for it (its
    // FF02 collection has never logged one). KeebHub's read would serialize
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
    // Streaming knob -> global, under _gate. On a byte change, global = byte/100 directly
    // (no smoothing): global mirrors the firmware byte's 0-100 position, one value.
    private int _lastKnobPct = -1;
    // Set while a software effect streams. Any settings write that lands during
    // a stream must not put an animated mode back on the device: the firmware
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

    /// <summary>Build the page from current settings and write it. No-op (false) when disconnected.</summary>
    public bool Apply()
    {
        if (!_gates.Lighting) return false;
        if (!_hub.IsConnected) return false;
        try
        {
            var page = KeebSettingsCodec.BuildSettingsPage(_store.Load().Keeb);
            if (_streaming) page[3] = KeebSettingsCodec.AnimationModeByte("static");
            return _hub.WriteSettings(page);
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
    /// Apply a knob detent to brightness, host-side.
    ///
    /// The knob used to be followed by polling the device's brightness byte,
    /// but that read shares KeebHub's IO lock with the LED stream and stalled a
    /// frame every time (camera-measured flicker). The rotary already reports
    /// its detents as interrupt-IN events on the input worker's OWN handle, so
    /// the same value can be tracked without touching the stream's lock at all.
    ///
    /// Master brightness moves with it, which is what dims a live software
    /// effect; the stored firmware byte moves with it too, so the two never
    /// drift and <see cref="Apply"/> re-synchronises the device when streaming
    /// stops. Returns false when this encoder is not assigned to brightness.
    /// </summary>
    public bool HandleEncoderScroll(KeebProtocol.KeebEncoder encoder, bool up)
    {
        if (encoder == KeebProtocol.KeebEncoder.None) return false;
        var settings = _store.Load().Keeb;
        var fn = encoder == KeebProtocol.KeebEncoder.Left ? settings.RotaryLeft : settings.RotaryRight;
        if (!string.Equals(fn, "BrightnessAdjustment", StringComparison.OrdinalIgnoreCase)) return false;

        var next = 0;
        _store.Update(s =>
        {
            next = Math.Clamp(s.Keeb.FirmwareLighting.Brightness + (up ? KnobStepPercent : -KnobStepPercent), 0, 100);
            s.Keeb.FirmwareLighting.Brightness = next;
            s.Lighting.GlobalBrightness = next / 100f;
            Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
        });
        lock (_gate) { _lastKnobPct = next; }
        PanelTopics.BroadcastLighting(_panel);
        return true;
    }

    /// <summary>Percent per rotary detent.</summary>
    private const int KnobStepPercent = 5;

    /// <summary>Hand the firmware animation back; the next Apply restores it.</summary>
    public void ReleaseFirmwareAnimation() => _streaming = false;

    /// <summary>
    /// Re-reference the knob so the next read adopts the byte's current position. The frame
    /// writer calls this when a software effect starts streaming.
    /// </summary>
    public void ResetKnobBaseline()
    {
        lock (_gate) { _lastKnobPct = -1; }
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
            var raw = _hub.ReadSettings();
            var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
            var brightIndex = animIndex + 1;
            if (raw is null || raw.Length <= brightIndex) return;
            _lastAnimByte = raw[animIndex];
            _lastBrightByte = raw[brightIndex];
        }
    }

    /// <summary>
    /// On a <paramref name="readByte"/> tick while a software effect streams, read the
    /// firmware brightness byte and, when it changed, set global = byte/100 directly. No
    /// smoothing: global mirrors the byte's 0-100 position. The frame writer calls this.
    /// </summary>
    public void PollKnobToGlobal(bool readByte)
    {
        if (!readByte || !_hub.IsConnected) return;
        var changed = false;
        var raw = ReadSettingsOffStream();
        lock (_gate)
        {
            var brightIndex = (OperatingSystem.IsWindows() ? 3 : 2) + 1;
            if (raw is null || raw.Length <= brightIndex) return;
            var pct = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
            if (pct == _lastKnobPct) return;
            _lastKnobPct = pct;
            _store.Update(s =>
            {
                s.Lighting.GlobalBrightness = pct / 100f;
                Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
            });
            changed = true;
        }
        if (changed) PanelTopics.BroadcastLighting(_panel);
    }

    /// <summary>
    /// Mutate the persisted keeb state and (when <paramref name="writeDevice"/>)
    /// write the full page to the device, as one gated step so the read-back poll
    /// can't interleave between the store mutate and the byte write. Callers pass
    /// writeDevice=false while a software effect streams: the 0x06 page write would
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
    /// device-initiated change into persisted state, so the panel and the software
    /// stream follow the hardware. In firmware rotary mode the middle button cycles
    /// the effect and the knob moves the brightness byte with NO host callback, so
    /// the connection worker calls this each poll tick. Adopts the byte into the
    /// keeb master (FirmwareLighting.Brightness) only - never the per-zone software
    /// prefs, which stay the user's LED-map values. The equality check makes our own
    /// writes a no-op, so only a hardware-side change broadcasts. Gated against
    /// <see cref="ApplyGated"/> so it can't clobber an in-flight host write.
    /// </summary>
    public bool SyncFromDevice()
    {
        if (!_hub.IsConnected) return false;
        string? effect = null;
        var brightness = 0;
        var changed = false;
        lock (_gate)
        {
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
            // fight, and a host-set master (written to the store but not the device
            // while streaming) survives reading back the unchanged knob byte.
            var animChanged = animByte != _lastAnimByte;
            var brightChanged = brightByte != _lastBrightByte;
            _lastAnimByte = animByte;
            _lastBrightByte = brightByte;
            if (!animChanged && !brightChanged) return false;

            effect = KeebSettingsCodec.AnimationModeName((byte)animByte);
            brightness = KeebSettingsCodec.BrightnessPercentFromByte((byte)brightByte);
            // The byte is the firmware animation brightness; adopt it as the keeb
            // master. The connection worker only calls this with no software effect
            // (the firmware animation showing); while streaming the frame writer reads
            // the byte instead, as a delta onto global - see NudgeGlobalFromKnob.
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
