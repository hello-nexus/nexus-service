using System;
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

    public KeebSettingsApplier(KeebHub hub, IConfigStore store, MultiplexHub panel)
    {
        _hub = hub;
        _store = store;
        _panel = panel;
    }

    /// <summary>Build the page from current settings and write it. No-op (false) when disconnected.</summary>
    public bool Apply()
    {
        if (!_hub.IsConnected) return false;
        try
        {
            var page = KeebSettingsCodec.BuildSettingsPage(_store.Load().Keeb);
            return _hub.WriteSettings(page);
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] apply settings failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

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
        lock (_gate)
        {
            var raw = _hub.ReadSettings();
            var brightIndex = (OperatingSystem.IsWindows() ? 3 : 2) + 1;
            if (raw is null || raw.Length <= brightIndex) return;
            var pct = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
            if (pct == _lastKnobPct) return;
            _lastKnobPct = pct;
            _store.Update(s => s.Lighting.GlobalBrightness = pct / 100f);
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
