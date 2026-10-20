using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// What the panel is known to hold, learned only from its own replies and from writes it
/// took. A field never read is null, which compares unequal to everything. Kept by the worker
/// across reconnects, so an edit that never reached the panel is still different next time.
/// </summary>
public sealed class GlacialLcdConfirmed
{
    public int? Brightness { get; set; }
    public bool? DisplayOn { get; set; }
    public bool? KeepOnWhileAsleep { get; set; }

    /// <summary>Key of the mode last sent and taken, or assumed held after a restart; null when unknown.</summary>
    public string? ModeKey { get; set; }
}

/// <summary>
/// Drives the Glacial LCD's native modes the way MB Manager does: a state query ahead of
/// every change, then the reports the capture showed for it, with the hardware monitor
/// resending its widgets on a fixed cadence. Until the user changes something in Nexus the
/// panel is only read, and afterwards only the fields the user edited are written. Owned by
/// one worker thread; nothing here locks.
/// </summary>
public sealed class GlacialLcdScreen
{
    /// <summary>Gap between two widget resends in the captures.</summary>
    public const int SlotResendMs = 5100;

    /// <summary>Wait before asking again after the panel gave no state reply.</summary>
    public const int NoReplyRetryMs = 10_000;

    /// <summary>Shown when a widget has no reading.</summary>
    public const string NoReading = "--";

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
    private readonly Action<string> _modeLanded;
    private readonly Func<long> _clock;
    private readonly Action<int> _pause;

    private long _lastSlotsMs;
    private long _seedRetryAtMs;
    private long _settingsRetryAtMs;
    private int _announcedCount;
    private bool _seeded;

    public GlacialLcdScreen(
        IHidDevice hid,
        Func<GlacialLcdSettings> settings,
        Func<GlacialLcdSlot, int, (string Label, string Value)> reader,
        GlacialLcdConfirmed confirmed,
        Action<int, bool, bool>? seed = null,
        Action<string>? warn = null,
        Action? chooseDefaultMode = null,
        Action<string>? modeLanded = null,
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
        _modeLanded = modeLanded ?? (_ => { });
        _clock = clock ?? (() => Environment.TickCount64);
        _pause = pause ?? (ms => System.Threading.Thread.Sleep(ms));
    }

    /// <summary>Identity of the mode content, so a change of mode, asset, widget count or sensors is one comparison.</summary>
    public static string ModeKeyOf(GlacialLcdSettings s) => s.Mode switch
    {
        GlacialLcdSettings.ModeStatic => "static:" + s.StaticAsset,
        GlacialLcdSettings.ModeMonitor => "monitor:" + s.SlotCount + ":" + SlotKey(s),
        _ => "animated:" + s.AnimatedAsset,
    };

    /// <summary>
    /// The app-start pair (hello, one state query); the reply seeds the fields the user has
    /// not edited and becomes what the panel is known to hold. A saved mode with no change
    /// pending is assumed to still be on the panel. Only a monitor the user chose is resumed.
    /// False when the panel did not take a report.
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
        RecordReply(reply);

        var wanted = _settings().Copy();
        if (_confirmed.ModeKey is null && wanted.ModeChosen && !wanted.ModePending)
        {
            _confirmed.ModeKey = ModeKeyOf(wanted);
        }
        if (wanted.UserEdited && wanted.ModeChosen && wanted.DisplayOn && wanted.Mode == GlacialLcdSettings.ModeMonitor)
        {
            if (SelectMode(wanted) == Step.Failed)
            {
                return false;
            }
            Landed(wanted);
        }
        return true;
    }

    /// <summary>Sends what the user changed and the panel does not hold yet.</summary>
    public bool Reconcile()
    {
        var wanted = _settings().Copy();
        if (!wanted.UserEdited)
        {
            return true;
        }
        var c = _confirmed;
        bool flagsPending = FlagEditPending(wanted.BrightnessEdited, wanted.Brightness, c.Brightness)
            || FlagEditPending(wanted.DisplayEdited, wanted.DisplayOn, c.DisplayOn)
            || FlagEditPending(wanted.SleepEdited, wanted.StayOnWhileAsleep, c.KeepOnWhileAsleep);
        bool modeChanged = wanted.ModeChosen && ModeKeyOf(wanted) != c.ModeKey;
        if (!flagsPending && !modeChanged)
        {
            return true;
        }

        // A change that needs no reply goes out whether or not a settings write is waiting out a silent panel.
        bool turningOn = wanted.DisplayEdited && wanted.DisplayOn && c.DisplayOn != true;
        if (flagsPending && _clock() >= _settingsRetryAtMs)
        {
            var step = WriteFlags(wanted, modeChanged);
            if (step == Step.Failed)
            {
                return false;
            }
            if (step == Step.NoReply)
            {
                _settingsRetryAtMs = _clock() + NoReplyRetryMs;
                _warn("no state reply from the panel; will retry");
                return !modeChanged || turningOn || !wanted.DisplayOn || SelectAndLand(wanted) != Step.Failed;
            }
            return true;
        }
        if (modeChanged && wanted.DisplayOn && !turningOn)
        {
            return SelectAndLand(wanted) != Step.Failed;
        }
        return true;
    }

    /// <summary>Resends the monitor widgets when their interval has run out, and seeds from the panel if it has not answered yet.</summary>
    public bool Tick()
    {
        var saved = _settings();
        if (!_seeded && _clock() >= _seedRetryAtMs)
        {
            var reply = ReadState();
            if (reply is { Length: > 0 })
            {
                RecordReply(reply);
            }
            else
            {
                _seedRetryAtMs = _clock() + NoReplyRetryMs;
                _warn("no state reply from the panel; will retry");
            }
        }
        if (!saved.UserEdited || _confirmed.DisplayOn == false || _announcedCount == 0
            || _confirmed.ModeKey is not { } key || !key.StartsWith("monitor:", StringComparison.Ordinal))
        {
            return true;
        }
        if (_clock() - _lastSlotsMs < SlotResendMs)
        {
            return true;
        }
        // Readings follow the saved slots so a sensor pick shows at once; the count is the one last announced.
        var current = saved.Copy();
        current.SlotCount = _announcedCount;
        return SendSlots(current);
    }

    private static bool FlagEditPending<T>(bool edited, T wanted, T? known)
        where T : struct, IEquatable<T> => edited && (known is null || !wanted.Equals(known.Value));

    /// <summary>Records everything the reply says, then lets the saved state follow it for fields the user has not edited.</summary>
    private void RecordReply(byte[] reply)
    {
        if (GlacialLcdProtocol.ParseState(reply) is not { } state)
        {
            return;
        }
        _seeded = true;
        _confirmed.Brightness = state.BrightnessPercent;
        _confirmed.DisplayOn = state.DisplayOn;
        _confirmed.KeepOnWhileAsleep = state.KeepOnWhileAsleep;
        _seed(state.BrightnessPercent, state.DisplayOn, state.KeepOnWhileAsleep);
    }

    /// <summary>
    /// One fresh query, then the settings write with only the edited fields changed, then what
    /// that change needs: a bare off, repeats of the mode select, or the full mode selection.
    /// </summary>
    private Step WriteFlags(GlacialLcdSettings wanted, bool modeChanged)
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
        RecordReply(reply);
        // The seed may have moved fields the user never edited; edits are judged against the reply.
        wanted = _settings().Copy();
        var c = _confirmed;
        int? brightness = FlagEditPending(wanted.BrightnessEdited, wanted.Brightness, c.Brightness) ? wanted.Brightness : null;
        bool? display = FlagEditPending(wanted.DisplayEdited, wanted.DisplayOn, c.DisplayOn) ? wanted.DisplayOn : null;
        bool? sleep = FlagEditPending(wanted.SleepEdited, wanted.StayOnWhileAsleep, c.KeepOnWhileAsleep) ? wanted.StayOnWhileAsleep : null;
        bool wrote = brightness is not null || display is not null || sleep is not null;
        if (wrote && !_hid.Write(GlacialLcdProtocol.EncodeSettingsEdit(reply, brightness, display, sleep)))
        {
            return Step.Failed;
        }
        if (brightness is not null) { c.Brightness = brightness; }
        if (display is not null) { c.DisplayOn = display; }
        if (sleep is not null) { c.KeepOnWhileAsleep = sleep; }

        bool turnedOn = display == true;
        bool defaultMode = turnedOn && !wanted.ModeChosen;
        if (defaultMode)
        {
            wanted.ModeChosen = true;
            wanted.Mode = GlacialLcdSettings.ModeAnimated;
            wanted.AnimatedAsset = GlacialLcdSettings.FirstRunAnimation;
        }
        modeChanged = wanted.ModeChosen && ModeKeyOf(wanted) != c.ModeKey;

        if (!wanted.DisplayOn)
        {
            if (wrote)
            {
                _pause(BurstGapMs);
                return _hid.Write(GlacialLcdProtocol.EncodeOff()) ? Step.Ok : Step.Failed;
            }
            return Step.Ok;
        }
        // A display turn-on or a mode change selects the mode once, after the settings, so a
        // monitor's widgets always go out before its mode select; a bare brightness or sleep
        // change repeats the current mode select instead.
        bool reselect = wanted.ModeChosen && (modeChanged || turnedOn);
        if (!reselect)
        {
            return wrote && wanted.ModeChosen ? RepeatModeSelect(wanted) : Step.Ok;
        }
        var step = SelectAndLand(wanted);
        if (step == Step.Ok && defaultMode)
        {
            _chooseDefaultMode();
        }
        return step;
    }

    private Step RepeatModeSelect(GlacialLcdSettings s)
    {
        for (int i = 0; i < 2; i++)
        {
            _pause(i == 0 ? BurstGapMs : RepeatGapMs);
            if (!Query() || !_hid.Write(ModeSelectReport(s)))
            {
                return Step.Failed;
            }
        }
        return Step.Ok;
    }

    private Step SelectAndLand(GlacialLcdSettings s)
    {
        var step = SelectMode(s);
        if (step == Step.Ok)
        {
            Landed(s);
        }
        return step;
    }

    private void Landed(GlacialLcdSettings s)
    {
        string key = ModeKeyOf(s);
        _confirmed.ModeKey = key;
        _modeLanded(key);
    }

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

    /// <summary>Waits for the state reply to a query already written; null when the link failed, empty when none came.</summary>
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

    /// <summary>Drains stale input, queries, and returns the fresh state reply; null when the link failed, empty when none came.</summary>
    private byte[]? ReadState()
    {
        Drain();
        return _hid.Write(GlacialLcdProtocol.EncodeQuery()) ? AwaitState() : null;
    }

    private bool Query() => _hid.Write(GlacialLcdProtocol.EncodeQuery());

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
            _announcedCount = 0;
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
        _announcedCount = Math.Clamp(s.SlotCount, 1, GlacialLcdProtocol.MaxMonitorSlots);
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
