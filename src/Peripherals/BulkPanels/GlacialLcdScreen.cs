using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Drives the Glacial LCD's native modes the way MB Manager does: every change is a state
/// query (0xDC) followed by the reports the capture showed for it, and the hardware monitor
/// resends its widgets on a fixed cadence. Owned by one worker thread; nothing here locks.
/// </summary>
public sealed class GlacialLcdScreen
{
    /// <summary>Gap between two widget resends in the captures.</summary>
    public const int SlotResendMs = 5100;

    /// <summary>Gap between the reports of one burst in the captures.</summary>
    private const int BurstGapMs = 15;

    /// <summary>Delay before the app's second mode repeat after a settings write.</summary>
    private const int RepeatGapMs = 80;

    private const int StateReplyAttempts = 3;
    private const int StateReplyWaitMs = 200;
    private const int DrainLimit = 64;

    /// <summary>Shown when a widget has no reading; the vendor app never showed this case.</summary>
    public const string NoReading = "--";

    private readonly IHidDevice _hid;
    private readonly Func<GlacialLcdSettings> _settings;
    private readonly Func<GlacialLcdSlot, int, (string Label, string Value)> _reader;
    private readonly Func<long> _clock;
    private readonly Action<int> _pause;

    private GlacialLcdSettings? _applied;
    private long _lastSlotsMs;

    public GlacialLcdScreen(
        IHidDevice hid,
        Func<GlacialLcdSettings> settings,
        Func<GlacialLcdSlot, int, (string Label, string Value)> reader,
        Func<long>? clock = null,
        Action<int>? pause = null)
    {
        _hid = hid;
        _settings = settings;
        _reader = reader;
        _clock = clock ?? (() => Environment.TickCount64);
        _pause = pause ?? (ms => System.Threading.Thread.Sleep(ms));
    }

    /// <summary>
    /// The app-start sequence (hello, then a state query), followed by the whole saved state.
    /// False when the panel did not take a report.
    /// </summary>
    public bool Open()
    {
        if (!_hid.Write(GlacialLcdProtocol.EncodeHello()) || !_hid.Write(GlacialLcdProtocol.EncodeQuery()))
        {
            return false;
        }
        var wanted = _settings().Copy();
        _applied = null;
        bool ok = wanted.DisplayOn
            ? WriteSettings(wanted, repeats: 0) && SelectMode(wanted)
            : WriteOff(wanted);
        if (ok)
        {
            _applied = wanted;
        }
        return ok;
    }

    /// <summary>Sends whatever differs between the saved state and what the panel was last given.</summary>
    public bool Reconcile()
    {
        var wanted = _settings().Copy();
        var applied = _applied;
        if (applied is null)
        {
            return false;
        }
        bool flagsChanged = wanted.Brightness != applied.Brightness
            || wanted.StayOnWhileAsleep != applied.StayOnWhileAsleep
            || wanted.DisplayOn != applied.DisplayOn;
        bool modeChanged = ModeKey(wanted) != ModeKey(applied);
        if (!flagsChanged && (!modeChanged || !wanted.DisplayOn))
        {
            // A mode picked while the display is off is only remembered; it is sent when the display comes on.
            _applied = wanted;
            return true;
        }

        bool ok;
        if (flagsChanged)
        {
            // Off is the 5C write and a bare mode off; on repeats the whole selection; a
            // brightness or sleep change writes 5C and repeats the current mode select twice.
            ok = !wanted.DisplayOn
                ? WriteOff(wanted)
                : wanted.DisplayOn != applied.DisplayOn
                    ? WriteSettings(wanted, repeats: 0) && SelectMode(wanted)
                    : WriteSettings(wanted, repeats: 2) && (!modeChanged || SelectMode(wanted));
        }
        else
        {
            ok = SelectMode(wanted);
        }
        if (ok)
        {
            _applied = wanted;
        }
        return ok;
    }

    /// <summary>Resends the monitor widgets when their interval has run out.</summary>
    public bool Tick()
    {
        var applied = _applied;
        if (applied is null || !applied.DisplayOn || applied.Mode != GlacialLcdSettings.ModeMonitor)
        {
            return true;
        }
        if (_clock() - _lastSlotsMs < SlotResendMs)
        {
            return true;
        }
        // Readings come from the saved slots so a sensor pick shows at once, but the count
        // and mode are the ones last announced.
        var current = _settings().Copy();
        current.SlotCount = applied.SlotCount;
        return SendSlots(current);
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

    private bool Query() => _hid.Write(GlacialLcdProtocol.EncodeQuery());

    /// <summary>Drains stale input, queries, and returns the fresh 5C reply or null.</summary>
    private byte[]? ReadState()
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        for (int i = 0; i < DrainLimit && _hid.Read(buffer, 0) > 0; i++)
        {
        }
        if (!Query())
        {
            return null;
        }
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

    private bool WriteSettingsReport(GlacialLcdSettings s)
    {
        var reply = ReadState();
        if (reply is null)
        {
            return false;
        }
        // Read-modify-write from the panel's own reply; with no reply the saved state is complete enough.
        var report = reply.Length > 0
            ? GlacialLcdProtocol.EncodeSettingsFrom(reply, s.Brightness, s.DisplayOn, s.StayOnWhileAsleep)
            : GlacialLcdProtocol.EncodeSettings(s.Brightness, s.DisplayOn, s.StayOnWhileAsleep);
        return _hid.Write(report);
    }

    /// <summary>The 5C write, then the app's repeats of the mode select (query, select) after it.</summary>
    private bool WriteSettings(GlacialLcdSettings s, int repeats)
    {
        if (!WriteSettingsReport(s))
        {
            return false;
        }
        for (int i = 0; i < repeats; i++)
        {
            _pause(i == 0 ? BurstGapMs : RepeatGapMs);
            if (!Query() || !_hid.Write(ModeSelectReport(s)))
            {
                return false;
            }
        }
        return true;
    }

    private bool WriteOff(GlacialLcdSettings s)
    {
        if (!WriteSettingsReport(s))
        {
            return false;
        }
        _pause(BurstGapMs);
        return _hid.Write(GlacialLcdProtocol.EncodeOff());
    }

    private static byte[] ModeSelectReport(GlacialLcdSettings s) => s.Mode switch
    {
        GlacialLcdSettings.ModeStatic => GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, s.StaticAsset),
        GlacialLcdSettings.ModeMonitor => GlacialLcdProtocol.EncodeHardwareMonitor(),
        _ => GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeAnimatedAsset, s.AnimatedAsset),
    };

    /// <summary>The full selection: preset modes are one report, the monitor is widgets, count, mode, widgets again.</summary>
    private bool SelectMode(GlacialLcdSettings s)
    {
        if (!Query())
        {
            return false;
        }
        if (s.Mode != GlacialLcdSettings.ModeMonitor)
        {
            return _hid.Write(ModeSelectReport(s));
        }
        if (!SendSlots(s))
        {
            return false;
        }
        _pause(BurstGapMs);
        if (!_hid.Write(GlacialLcdProtocol.EncodeSlotCount(s.SlotCount)))
        {
            return false;
        }
        _pause(BurstGapMs);
        if (!_hid.Write(GlacialLcdProtocol.EncodeHardwareMonitor()))
        {
            return false;
        }
        _pause(BurstGapMs);
        return SendSlots(s);
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
