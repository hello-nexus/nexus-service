using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// What the panel is known to hold. Kept by the worker across reconnects, so an edit that
/// never reached the panel is still different from it next time and gets sent then.
/// </summary>
public sealed class GlacialLcdConfirmed
{
    public GlacialLcdSettings? State { get; set; }
}

/// <summary>
/// Drives the Glacial LCD's native modes the way MB Manager does: a state query (0xDC)
/// ahead of every change, then the reports the capture showed for it, with the hardware
/// monitor resending its widgets on a fixed cadence. Until the user changes something in
/// Nexus the panel is only read. Owned by one worker thread; nothing here locks.
/// </summary>
public sealed class GlacialLcdScreen
{
    /// <summary>Gap between two widget resends in the captures.</summary>
    public const int SlotResendMs = 5100;

    /// <summary>Wait before trying again when the panel gave no state reply.</summary>
    public const int NoReplyRetryMs = 10_000;

    /// <summary>Shown when a widget has no reading.</summary>
    public const string NoReading = "--";

    /// <summary>Animation the vendor app starts on.</summary>
    private const int FirstRunAnimation = 1;

    private const int BurstGapMs = 15;
    private const int RepeatGapMs = 80;
    private const int StateReplyAttempts = 3;
    private const int StateReplyWaitMs = 200;
    private const int DrainLimit = 64;

    private enum Step
    {
        Ok,
        NoReply,
        Failed,
    }

    private readonly IHidDevice _hid;
    private readonly Func<GlacialLcdSettings> _settings;
    private readonly Func<GlacialLcdSlot, int, (string Label, string Value)> _reader;
    private readonly GlacialLcdConfirmed _confirmed;
    private readonly Action<int, bool, bool> _seed;
    private readonly Action<string> _warn;
    private readonly Action _chooseDefaultMode;
    private readonly Func<long> _clock;
    private readonly Action<int> _pause;

    private long _lastSlotsMs;
    private long _retryAtMs;
    private bool _seeded;

    public GlacialLcdScreen(
        IHidDevice hid,
        Func<GlacialLcdSettings> settings,
        Func<GlacialLcdSlot, int, (string Label, string Value)> reader,
        GlacialLcdConfirmed confirmed,
        Action<int, bool, bool>? seed = null,
        Action<string>? warn = null,
        Action? chooseDefaultMode = null,
        Func<long>? clock = null,
        Action<int>? pause = null)
    {
        _hid = hid;
        _settings = settings;
        _reader = reader;
        _confirmed = confirmed;
        _seed = seed ?? ((_, _, _) => { });
        _warn = warn ?? (_ => { });
        _chooseDefaultMode = chooseDefaultMode ?? (() => { });
        _clock = clock ?? (() => Environment.TickCount64);
        _pause = pause ?? (ms => System.Threading.Thread.Sleep(ms));
    }

    /// <summary>
    /// The app-start pair (hello, one state query), then the first reply seeds the saved
    /// brightness, display and sleep state and becomes what the panel is known to hold. Only a
    /// monitor the user already chose is resumed; anything else that differs from the panel is
    /// sent by the next <see cref="Reconcile"/>, field by field. False when the panel did not
    /// take a report.
    /// </summary>
    public bool Open()
    {
        Drain();
        if (!_hid.Write(GlacialLcdProtocol.EncodeHello()) || !_hid.Write(GlacialLcdProtocol.EncodeQuery()))
        {
            return false;
        }
        var reply = AwaitState();
        if (reply is null)
        {
            return false;
        }
        TrySeed(reply);

        var wanted = _settings().Copy();
        _confirmed.State ??= wanted.Copy();
        if (GlacialLcdProtocol.ParseState(reply) is { } state)
        {
            ConfirmFlags(state.BrightnessPercent, state.DisplayOn, state.KeepOnWhileAsleep);
        }
        if (wanted.UserEdited && wanted.ModeChosen && wanted.DisplayOn && wanted.Mode == GlacialLcdSettings.ModeMonitor)
        {
            if (SelectMode(wanted) == Step.Failed)
            {
                return false;
            }
            ConfirmMode(wanted);
        }
        return true;
    }

    private void ConfirmFlags(int brightness, bool displayOn, bool keepOn)
    {
        var c = _confirmed.State!;
        c.Brightness = brightness;
        c.DisplayOn = displayOn;
        c.StayOnWhileAsleep = keepOn;
    }

    private void ConfirmMode(GlacialLcdSettings wanted)
    {
        var c = _confirmed.State!;
        c.Mode = wanted.Mode;
        c.StaticAsset = wanted.StaticAsset;
        c.AnimatedAsset = wanted.AnimatedAsset;
        c.SlotCount = wanted.SlotCount;
        c.Slots = wanted.Slots;
        c.ModeChosen = wanted.ModeChosen;
    }

    /// <summary>Sends the fields that differ between the saved state and what the panel is known to hold.</summary>
    public bool Reconcile()
    {
        var wanted = _settings().Copy();
        var confirmed = _confirmed.State;
        if (confirmed is null)
        {
            return false;
        }
        if (!wanted.UserEdited)
        {
            _confirmed.State = wanted;
            return true;
        }
        if (_clock() < _retryAtMs)
        {
            return true;
        }
        int? brightnessEdit = wanted.Brightness != confirmed.Brightness ? wanted.Brightness : null;
        bool? displayEdit = wanted.DisplayOn != confirmed.DisplayOn ? wanted.DisplayOn : null;
        bool? sleepEdit = wanted.StayOnWhileAsleep != confirmed.StayOnWhileAsleep ? wanted.StayOnWhileAsleep : null;
        bool flagsChanged = brightnessEdit is not null || displayEdit is not null || sleepEdit is not null;

        // Turning the display on with no mode ever chosen would leave it dark after the bare off,
        // so it gets the vendor's first-run animation, as a chosen mode.
        bool defaultMode = displayEdit == true && !wanted.ModeChosen;
        if (defaultMode)
        {
            wanted.ModeChosen = true;
            wanted.Mode = GlacialLcdSettings.ModeAnimated;
            wanted.AnimatedAsset = FirstRunAnimation;
        }
        bool modeChanged = wanted.ModeChosen && (!confirmed.ModeChosen || ModeKey(wanted) != ModeKey(confirmed));
        if (!flagsChanged && (!modeChanged || !wanted.DisplayOn))
        {
            // A mode picked while the display is off is sent when it comes on.
            ConfirmMode(wanted);
            return true;
        }

        Step step;
        if (!flagsChanged)
        {
            step = SelectMode(wanted);
        }
        else if (!wanted.DisplayOn)
        {
            step = WriteOff(brightnessEdit, displayEdit, sleepEdit);
        }
        else
        {
            // Brightness or sleep alone repeats the current mode select; a display turn-on or a
            // mode change selects the mode once, after the settings, so a monitor's widgets
            // always go out before its mode select.
            bool reselect = wanted.ModeChosen && (modeChanged || displayEdit == true);
            step = WriteSettings(brightnessEdit, displayEdit, sleepEdit, wanted, repeats: wanted.ModeChosen && !reselect ? 2 : 0);
            if (step == Step.Ok && reselect)
            {
                step = SelectMode(wanted);
            }
        }
        if (step == Step.Failed)
        {
            return false;
        }
        if (step == Step.NoReply)
        {
            Deferred();
            return true;
        }
        ConfirmFlags(wanted.Brightness, wanted.DisplayOn, wanted.StayOnWhileAsleep);
        ConfirmMode(wanted);
        if (defaultMode)
        {
            _chooseDefaultMode();
        }
        return true;
    }

    /// <summary>Resends the monitor widgets when their interval has run out, and seeds from the panel if it has not answered yet.</summary>
    public bool Tick()
    {
        var applied = _confirmed.State;
        if (applied is null)
        {
            return true;
        }
        var saved = _settings();
        if (!_seeded && !saved.UserEdited && _clock() >= _retryAtMs)
        {
            var reply = ReadState();
            if (reply is { Length: > 0 })
            {
                TrySeed(reply);
                if (GlacialLcdProtocol.ParseState(reply) is { } seeded)
                {
                    ConfirmFlags(seeded.BrightnessPercent, seeded.DisplayOn, seeded.KeepOnWhileAsleep);
                }
            }
            else
            {
                Deferred();
            }
        }
        if (!saved.UserEdited || !applied.ModeChosen || !applied.DisplayOn || applied.Mode != GlacialLcdSettings.ModeMonitor)
        {
            return true;
        }
        if (_clock() - _lastSlotsMs < SlotResendMs)
        {
            return true;
        }
        // Readings follow the saved slots so a sensor pick shows at once; the count is the one last announced.
        var current = _settings().Copy();
        current.SlotCount = applied.SlotCount;
        return SendSlots(current);
    }

    private void Deferred()
    {
        _retryAtMs = _clock() + NoReplyRetryMs;
        _warn("no state reply from the panel; will retry");
    }

    private void TrySeed(byte[] reply)
    {
        if (GlacialLcdProtocol.ParseState(reply) is { } state)
        {
            _seeded = true;
            _seed(state.BrightnessPercent, state.DisplayOn, state.KeepOnWhileAsleep);
        }
    }

    private static string ModeKey(GlacialLcdSettings s) => s.Mode switch
    {
        GlacialLcdSettings.ModeStatic => "static:" + s.StaticAsset,
        GlacialLcdSettings.ModeMonitor => "monitor:" + s.SlotCount + ":" + SlotKey(s),
        _ => "animated:" + s.AnimatedAsset,
    };

    private static string SlotKey(GlacialLcdSettings s)
    {
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(s.SlotCount, s.Slots.Count); i++)
        {
            parts.Add(s.Slots[i].Category + "/" + s.Slots[i].Sensor + "/" + s.Slots[i].Label);
        }
        return string.Join("|", parts);
    }

    private void Drain()
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        for (int i = 0; i < DrainLimit && _hid.Read(buffer, 0) > 0; i++)
        {
        }
    }

    /// <summary>Waits for the 5C reply to a query already written; null when the link failed, empty when none came.</summary>
    private byte[]? AwaitState()
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        for (int attempt = 0; attempt < StateReplyAttempts; attempt++)
        {
            int read = _hid.Read(buffer, StateReplyWaitMs);
            if (read < 0)
            {
                return null;
            }
            if (read > 0 && GlacialLcdProtocol.ParseState(buffer.AsSpan(0, read)) is not null)
            {
                return buffer.AsSpan(0, read).ToArray();
            }
        }
        return Array.Empty<byte>();
    }

    /// <summary>Drains stale input, queries, and returns the fresh 5C reply; null when the link failed, empty when none came.</summary>
    private byte[]? ReadState()
    {
        Drain();
        return _hid.Write(GlacialLcdProtocol.EncodeQuery()) ? AwaitState() : null;
    }

    private bool Query() => _hid.Write(GlacialLcdProtocol.EncodeQuery());

    /// <summary>The 5C write is read-modify-write: only the edited fields change, so it is skipped when no fresh reply arrived.</summary>
    private Step WriteSettingsReport(int? brightness, bool? displayOn, bool? keepOn)
    {
        var reply = ReadState();
        if (reply is null)
        {
            return Step.Failed;
        }
        if (reply.Length == 0)
        {
            return Step.NoReply;
        }
        var report = GlacialLcdProtocol.EncodeSettingsEdit(reply, brightness, displayOn, keepOn);
        return _hid.Write(report) ? Step.Ok : Step.Failed;
    }

    /// <summary>The 5C write, then the app's repeats of the mode select (query, select) after it.</summary>
    private Step WriteSettings(int? brightness, bool? displayOn, bool? keepOn, GlacialLcdSettings s, int repeats)
    {
        var step = WriteSettingsReport(brightness, displayOn, keepOn);
        if (step != Step.Ok)
        {
            return step;
        }
        for (int i = 0; i < repeats; i++)
        {
            _pause(i == 0 ? BurstGapMs : RepeatGapMs);
            if (!Query() || !_hid.Write(ModeSelectReport(s)))
            {
                return Step.Failed;
            }
        }
        return Step.Ok;
    }

    private Step WriteOff(int? brightness, bool? displayOn, bool? keepOn)
    {
        var step = WriteSettingsReport(brightness, displayOn, keepOn);
        if (step != Step.Ok)
        {
            return step;
        }
        _pause(BurstGapMs);
        return _hid.Write(GlacialLcdProtocol.EncodeOff()) ? Step.Ok : Step.Failed;
    }

    private static byte[] ModeSelectReport(GlacialLcdSettings s) => s.Mode switch
    {
        GlacialLcdSettings.ModeStatic => GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, s.StaticAsset),
        GlacialLcdSettings.ModeMonitor => GlacialLcdProtocol.EncodeHardwareMonitor(),
        _ => GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeAnimatedAsset, s.AnimatedAsset),
    };

    /// <summary>The full selection: preset modes are one report, the monitor is widgets, count, mode, widgets again.</summary>
    private Step SelectMode(GlacialLcdSettings s)
    {
        if (!Query())
        {
            return Step.Failed;
        }
        if (s.Mode != GlacialLcdSettings.ModeMonitor)
        {
            return _hid.Write(ModeSelectReport(s)) ? Step.Ok : Step.Failed;
        }
        if (!SendSlots(s))
        {
            return Step.Failed;
        }
        _pause(BurstGapMs);
        if (!_hid.Write(GlacialLcdProtocol.EncodeSlotCount(s.SlotCount)))
        {
            return Step.Failed;
        }
        _pause(BurstGapMs);
        if (!_hid.Write(GlacialLcdProtocol.EncodeHardwareMonitor()))
        {
            return Step.Failed;
        }
        _pause(BurstGapMs);
        return SendSlots(s) ? Step.Ok : Step.Failed;
    }

    private bool SendSlots(GlacialLcdSettings s)
    {
        int count = Math.Clamp(s.SlotCount, 1, GlacialLcdProtocol.MaxMonitorSlots);
        for (int i = 0; i < count; i++)
        {
            var slot = i < s.Slots.Count ? s.Slots[i] : new GlacialLcdSlot();
            var (label, value) = _reader(slot, i);
            if (!_hid.Write(GlacialLcdProtocol.EncodeSlot(i, label, value)))
            {
                return false;
            }
            if (i + 1 < count)
            {
                _pause(BurstGapMs);
            }
        }
        _lastSlotsMs = _clock();
        return true;
    }

    /// <summary>Text for a widget reading in the formats the vendor app showed: one-decimal temperature, three-decimal voltage.</summary>
    public static string FormatValue(string sensorType, float value)
    {
        if (!float.IsFinite(value))
        {
            return NoReading;
        }
        return sensorType switch
        {
            "Temperature" => value.ToString("0.0", CultureInfo.InvariantCulture) + "℃",
            "Voltage" => value.ToString("0.000", CultureInfo.InvariantCulture) + "↊",
            _ => NoReading,
        };
    }
}
