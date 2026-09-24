using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the HYTE Q-series (Q60 / Q80) AIO into the cooling subsystem. The pump
/// head and every fan on the two Nexus Link channels are controllable <see cref="FanChannel"/>s:
/// Manual / Curve drive them under software control, BIOS hands them to the
/// motherboard, FW Control runs the onboard firmware curve.
///
/// The cooler has a SINGLE hub-wide control mode shared by the pump and every fan, so
/// the mode (software / motherboard / firmware) is shared while each channel keeps
/// its own duty. <see cref="QSeriesCoolerHub.DesiredControlMode"/> is the pinned
/// mode the engine must not override: while it is a non-software mode, duty writes
/// are swallowed so the engine doesn't flip a user-chosen BIOS/FW mode back to
/// software. A hand-back (<see cref="HandBackMode"/>) never pins, and clears the
/// pin, so a released hub accepts its next assignment. Mirrors <see cref="Np50CoolingProvider"/>. A Q80 second pump is
/// surfaced read-only. Channel ids: <c>qseries:&lt;serial&gt;:pump</c> / <c>:pump2</c>;
/// a fan is <c>qseries:&lt;serial&gt;:p&lt;channel&gt;:&lt;slot&gt;</c> for a solo FT12 unit, with a
/// trailing <c>:&lt;fanIdx&gt;</c> for a Duo/Trio slot's individual fans. Coolant sensor ids:
/// <c>:coolant-in</c> / <c>:coolant-out</c>.
/// </summary>
public sealed class QSeriesCoolerCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "qseries:";
    private const string PumpSuffix = ":pump";

    /// <summary><see cref="CoolingSettings.HubControlModes"/> values.</summary>
    public const string HandBackFirmware = "firmware";
    public const string HandBackMotherboard = "motherboard";
    // Pre-per-fan-enumeration channel id. A curve/manual-speed binding to it is
    // migrated onto the discovered channel-2 fans the first time they enumerate.
    private const string LegacyFanId = "fans";

    private readonly QSeriesCoolerHub _hub;
    private readonly IConfigStore _store;
    private readonly FeatureGates _gates;

    // Channels under active software control (Manual/Curve). The pump and every fan
    // share the hub's one mode, so the hub stays in software while any channel is
    // driven and reverts to motherboard once all are released. _pumpDuty/_fanDuty
    // are the last commanded duties so GetFanChannels can report them back.
    private readonly object _ctrlLock = new();
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fanDuty = new(StringComparer.Ordinal);
    private int _pumpDuty = 50;

    // Serials whose legacy ":fans" assignment has already been migrated (or confirmed
    // absent). Concurrent so overlapping GetFanChannels calls from different requests
    // never race the underlying set's internals.
    private readonly ConcurrentDictionary<string, byte> _legacyMigratedSerials = new(StringComparer.Ordinal);

    public QSeriesCoolerCoolingProvider(QSeriesCoolerHub hub, IConfigStore store, FeatureGates? gates = null)
    {
        _hub = hub;
        _store = store;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public static bool IsQSeriesId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ":pump" excludes ":pump2" (EndsWith).
    private static bool IsPumpControlId(string id) =>
        IsQSeriesId(id) && id.EndsWith(PumpSuffix, StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();

        // Snapshot once: State.Channel{1,2}Devices publishes a fresh list per poll rather
        // than mutating in place, so one read per channel here is a complete, stable view
        // for the rest of this call - a second read could observe a different snapshot if
        // PollTelemetry runs concurrently.
        var channel1 = _hub.State.Channel1Devices;
        var channel2 = _hub.State.Channel2Devices;

        MigrateLegacyFanAssignments(serial, channel2);

        var pumpId = PumpId(serial, "pump");
        bool pumpSw; int pumpDuty;
        lock (_ctrlLock)
        {
            pumpSw = _softwareControlled.Contains(pumpId);
            pumpDuty = _pumpDuty;
        }

        var deviceId = _hub.DeviceId;
        var deviceName = _hub.ProductName;
        var result = new List<FanChannel>
        {
            new FanChannel
            {
                Id = pumpId,
                Name = "Pump",
                Kind = FanKinds.Pump,
                ReadOnly = false,
                Rpm = _hub.State.PumpRpm,
                DutyPercent = pumpSw ? pumpDuty : 0,
                Mode = pumpSw ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceName,
                PortLabel = "Pump",
            },
        };
        if (_hub.State.HasPump2)
        {
            result.Add(new FanChannel
            {
                Id = PumpId(serial, "pump2"),
                Name = "Pump 2",
                Kind = FanKinds.Pump,
                ReadOnly = true,
                Rpm = _hub.State.Pump2Rpm,
                Mode = FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceName,
                PortLabel = "Pump 2",
            });
        }
        AddChannelFans(result, serial, 1, channel1, deviceId, deviceName);
        AddChannelFans(result, serial, 2, channel2, deviceId, deviceName);
        return result;
    }

    private void AddChannelFans(List<FanChannel> result, string serial, int channel,
        IReadOnlyList<QSeriesLinkDevice> devices, string deviceId, string deviceName)
    {
        foreach (var dev in devices)
        {
            if (dev.FanCount <= 0) continue;
            for (var fanIdx = 1; fanIdx <= dev.FanCount; fanIdx++)
            {
                var id = FanId(serial, channel, dev.Slot, dev.FanCount, fanIdx);
                bool sw; int duty;
                lock (_ctrlLock)
                {
                    sw = _softwareControlled.Contains(id);
                    duty = _fanDuty.TryGetValue(id, out var d) ? d : 0;
                }
                result.Add(new FanChannel
                {
                    Id = id,
                    Name = FanName(dev.Model, channel, dev.Slot, dev.FanCount, fanIdx),
                    Kind = FanKinds.Fan,
                    ReadOnly = false,
                    Rpm = fanIdx - 1 < dev.FanRpm.Length ? dev.FanRpm[fanIdx - 1] : 0,
                    DutyPercent = sw ? duty : 0,
                    Mode = sw ? FanModes.Manual : FanModes.Auto,
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    PortLabel = $"Port {channel}",
                    FanModel = dev.Model,
                    Orientation = dev.Orientation,
                });
            }
        }
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<TemperatureSource>();

        // A probeless input saturates the thermistor table, which the decode already drops.
        var result = new List<TemperatureSource>();
        if (_hub.State.CoolantTempInC is { } inC)
            result.Add(CoolantSource(serial, "coolant-in", "Coolant in", inC));
        if (_hub.State.CoolantTempOutC is { } outC)
            result.Add(CoolantSource(serial, "coolant-out", "Coolant out", outC));
        AddChannelTemperatures(result, serial, 1, _hub.State.Channel1Devices);
        AddChannelTemperatures(result, serial, 2, _hub.State.Channel2Devices);
        return result;
    }

    private void AddChannelTemperatures(List<TemperatureSource> result, string serial, int channel, IReadOnlyList<QSeriesLinkDevice> devices)
    {
        foreach (var dev in devices)
        {
            if (dev.TemperatureC is not { } t) continue;
            result.Add(new TemperatureSource
            {
                Id = $"{IdPrefix}{serial}:p{channel}:{dev.Slot}:temp",
                Name = $"{dev.Model} probe (Port {channel} #{dev.Slot})",
                Category = "Cooler",
                Value = t,
                DeviceId = _hub.DeviceId,
                DeviceName = _hub.ProductName,
            });
        }
    }

    private TemperatureSource CoolantSource(string serial, string suffix, string name, float value) => new()
    {
        Id = PumpId(serial, suffix),
        Name = name,
        Category = "Cooler",
        Value = value,
        DeviceId = _hub.DeviceId,
        DeviceName = _hub.ProductName,
    };

    public float? ReadTemperature(string sensorId)
    {
        if (!IsQSeriesId(sensorId)) return null;
        foreach (var s in GetTemperatureSources())
            if (s.Id == sensorId) return s.Value;
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) =>
        ApplyChannelWrite(channelId, Math.Clamp(dutyPercent, 0, 100));

    public void ReleaseFan(string channelId)
    {
        if (!IsPumpControlId(channelId) && !TryParseFanId(channelId, out _, out _, out _)) return;
        bool empty;
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            empty = _softwareControlled.Count == 0;
        }
        // Hub mode is shared: only hand back once every channel is released, so
        // releasing one doesn't yank PWM from the rest. A user pin stays: the
        // cooling page's Hardware-control pick pins through the route and releases
        // the fan as separate calls, and an engine tick between the two empties the
        // set through the swallow path without ending the pick.
        if (empty) HandBack(dropUserPin: false);
    }

    public void ReleaseAll()
    {
        bool any;
        lock (_ctrlLock)
        {
            any = _softwareControlled.Count > 0;
            _softwareControlled.Clear();
        }
        // Cooling off forbids any fan write. The toggle-off release itself still
        // hands back (it had channels to release); a later ReleaseAll with
        // nothing driven, such as shutdown, must stay silent.
        if (!any && !_gates.Cooling) return;
        // A bulk release (profile switch, cooling reset, shutdown) ends whatever
        // the user pinned: the incoming settings decide the mode now.
        HandBack(dropUserPin: true);
    }

    /// <summary>
    /// Each connect of the hub: hand it back so the cooler runs its stock mode.
    /// Skipped while Cooling is off (no fan write), and when the settings assign
    /// any channel the hub currently exposes: the engine claims the hub within a
    /// tick, and a hand-back first would only add a mode transition.
    /// </summary>
    public void OnHubConnected()
    {
        if (!_gates.Cooling) return;
        if (HasPersistedAssignments()) return;
        HandBack(dropUserPin: false);
    }

    private bool HasPersistedAssignments()
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in GetFanChannels()) present.Add(ch.Id);
        var cooling = _store.Load().Cooling;
        foreach (var id in cooling.ManualSpeeds.Keys)
        {
            if (present.Contains(id)) return true;
        }
        foreach (var curve in cooling.Curves)
        {
            foreach (var o in curve.Outputs)
            {
                if (present.Contains(o.Id)) return true;
            }
        }
        return false;
    }

    /// <summary>The mode a released hub runs: the persisted choice, else firmware where the cooler has a curve.</summary>
    public byte HandBackMode()
    {
        var choice = _store.Load().Cooling.HubControlModes.GetValueOrDefault(_hub.DeviceId);
        if (choice == HandBackMotherboard) return QSeriesCoolerProtocol.ControlModeMotherboard;
        return _hub.SupportsFirmwareCurve
            ? QSeriesCoolerProtocol.ControlModeFirmware
            : QSeriesCoolerProtocol.ControlModeMotherboard;
    }

    // The "nothing driven" check runs under the hub lock (hub lock, then
    // _ctrlLock; no path takes them the other way round), so an engine claim
    // cannot slip between it and the control write.
    private void HandBack(bool dropUserPin)
    {
        if (!_hub.IsConnected) return;
        _hub.HandBackControlMode(HandBackMode(), NothingDriven, dropUserPin);
    }

    private bool NothingDriven()
    {
        lock (_ctrlLock) return _softwareControlled.Count == 0;
    }

    /// <summary>Remember a control-mode pick as the hub's hand-back: Motherboard or Firmware is stored, Software clears the entry (the next assignment implies it), Mix is a live mode only.</summary>
    public static void RecordHandBackChoice(IConfigStore store, string deviceId, byte mode)
    {
        store.Update(s =>
        {
            switch (mode)
            {
                case QSeriesCoolerProtocol.ControlModeMotherboard:
                    s.Cooling.HubControlModes[deviceId] = HandBackMotherboard;
                    break;
                case QSeriesCoolerProtocol.ControlModeFirmware:
                    s.Cooling.HubControlModes[deviceId] = HandBackFirmware;
                    break;
                case QSeriesCoolerProtocol.ControlModeSoftware:
                    s.Cooling.HubControlModes.Remove(deviceId);
                    break;
            }
        });
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>
        {
            new CoolingDevice
            {
                Id = PumpId(serial, "pump"),
                Name = "Pump",
                Type = "Pump",
                Rpm = _hub.State.PumpRpm,
                Temperature = _hub.State.CoolantTempInC,
                PumpTempIn = _hub.State.CoolantTempInC,
                PumpTempOut = _hub.State.CoolantTempOutC,
            },
        };
        if (_hub.State.HasPump2)
            devices.Add(new CoolingDevice { Id = PumpId(serial, "pump2"), Name = "Pump 2", Type = "Pump", Rpm = _hub.State.Pump2Rpm });
        AddChannelCoolingDevices(devices, serial, 1, _hub.State.Channel1Devices);
        AddChannelCoolingDevices(devices, serial, 2, _hub.State.Channel2Devices);

        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = _hub.ProductName,
                Type = _hub.Variant == QSeriesCoolerProtocol.VariantQ80 ? "Q80" : "Q60",
                Devices = devices,
            },
        };
    }

    private void AddChannelCoolingDevices(List<CoolingDevice> devices, string serial, int channel, IReadOnlyList<QSeriesLinkDevice> list)
    {
        foreach (var dev in list)
        {
            if (dev.FanCount <= 0) continue;
            for (var fanIdx = 1; fanIdx <= dev.FanCount; fanIdx++)
            {
                devices.Add(new CoolingDevice
                {
                    Id = FanId(serial, channel, dev.Slot, dev.FanCount, fanIdx),
                    Name = FanName(dev.Model, channel, dev.Slot, dev.FanCount, fanIdx),
                    Type = "Fan",
                    Rpm = fanIdx - 1 < dev.FanRpm.Length ? dev.FanRpm[fanIdx - 1] : 0,
                    Temperature = dev.TemperatureC,
                });
            }
        }
    }

    // ── Internals ──

    private void ApplyChannelWrite(string channelId, int duty)
    {
        var isPump = IsPumpControlId(channelId);
        var channel = 0;
        var isFan = !isPump && TryParseFanId(channelId, out channel, out _, out _);
        if (!isPump && !isFan) return;
        if (!_hub.IsConnected) return;

        // User pinned a non-software mode (BIOS = motherboard, FW Control =
        // firmware): swallow the duty write so the engine's next tick doesn't
        // flip the shared pump+fan hub back to software. Drop the channel from
        // the software-control set so GetFanChannels reports its pinned state.
        if (_hub.DesiredControlMode is byte pinned && pinned != QSeriesCoolerProtocol.ControlModeSoftware)
        {
            lock (_ctrlLock) _softwareControlled.Remove(channelId);
            return;
        }

        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            if (isPump) _pumpDuty = duty; else _fanDuty[channelId] = duty;
        }
        // Driving implies software control; latch it so a sibling channel's write
        // (or this one's next tick) isn't swallowed.
        _hub.MarkDesiredControlMode(QSeriesCoolerProtocol.ControlModeSoftware);
        if (isPump)
        {
            _hub.SetPumpSpeed(duty);
            return;
        }

        // Snapshot once: reading the property twice in this method could observe two
        // different published lists if PollTelemetry runs concurrently.
        var devices = channel == 1 ? _hub.State.Channel1Devices : _hub.State.Channel2Devices;
        var serial = _hub.State.Serial;

        // BuildSetChannelFanSpeeds maps slotDuties[i] to wire block i = chain position
        // i+1 (legacy SmartHubCommandBase.SetFanSpeedByChannel indexes by position, and a
        // light strip still occupies a block even though it carries no duty). Index by
        // dev.Slot - 1, not by a compacted "fans only" position, or a duty meant for a
        // fan chained behind a strip lands in the strip's block instead.
        var maxSlot = 0;
        foreach (var dev in devices) if (dev.Slot > maxSlot) maxSlot = dev.Slot;
        var slots = new QSeriesCoolerProtocol.QSeriesFanSlotDuty[maxSlot];
        foreach (var dev in devices)
        {
            if (dev.FanCount <= 0) continue;
            var idx = dev.Slot - 1;
            if (idx < 0 || idx >= slots.Length) continue; // defensive: malformed slot index
            slots[idx] = new QSeriesCoolerProtocol.QSeriesFanSlotDuty(
                DutyOf(serial, channel, dev, 1),
                dev.FanCount >= 2 ? DutyOf(serial, channel, dev, 2) : 0,
                dev.FanCount >= 3 ? DutyOf(serial, channel, dev, 3) : 0);
        }
        _hub.SetChannelFanSpeeds((byte)channel, slots);
    }

    private int DutyOf(string serial, int channel, QSeriesLinkDevice dev, int fanIdx)
    {
        var id = FanId(serial, channel, dev.Slot, dev.FanCount, fanIdx);
        lock (_ctrlLock) return _fanDuty.TryGetValue(id, out var d) ? d : 0;
    }

    // A user who had a curve/profile bound to the pre-enumeration "qseries:<serial>:fans"
    // id must not silently lose fan control once it splits into per-fan channels. Runs
    // once the first time channel-2 fans enumerate for this serial (or confirms there is
    // nothing to migrate); a later reconnect of the same serial is a no-op. Covers every
    // per-channel binding that can carry a fan id EXCEPT FanNames, which names the old
    // aggregate specifically and must not be stamped onto five fans - that entry is just
    // deleted.
    private void MigrateLegacyFanAssignments(string serial, IReadOnlyList<QSeriesLinkDevice> channel2)
    {
        if (_legacyMigratedSerials.ContainsKey(serial)) return;

        var newIds = new List<string>();
        foreach (var dev in channel2)
        {
            if (dev.FanCount <= 0) continue;
            for (var fanIdx = 1; fanIdx <= dev.FanCount; fanIdx++)
                newIds.Add(FanId(serial, 2, dev.Slot, dev.FanCount, fanIdx));
        }
        if (newIds.Count == 0) return; // channel 2 hasn't enumerated yet; retry next call

        var legacyId = $"{IdPrefix}{serial}:{LegacyFanId}";
        var settings = _store.Load();
        var cooling = settings.Cooling;
        var anyLegacyBinding =
            cooling.ManualSpeeds.ContainsKey(legacyId) ||
            cooling.CustomManualSpeeds.ContainsKey(legacyId) ||
            cooling.FanOffsets.ContainsKey(legacyId) ||
            cooling.FanLockOverrides.ContainsKey(legacyId) ||
            cooling.FanRoles.ContainsKey(legacyId) ||
            cooling.CustomFanCurveAssignments.ContainsKey(legacyId) ||
            cooling.UncontrolledFanChannels.Contains(legacyId) ||
            (cooling.FanChannelOrder?.Contains(legacyId) ?? false) ||
            cooling.FanNames.ContainsKey(legacyId) ||
            cooling.Curves.Exists(c => c.Outputs.Exists(o => o.Id == legacyId)) ||
            cooling.Presets.Exists(p =>
                p.FanCurveAssignments.ContainsKey(legacyId) ||
                p.ManualSpeeds.ContainsKey(legacyId) ||
                p.FanOffsets.ContainsKey(legacyId));

        if (anyLegacyBinding)
        {
            _store.Update(s =>
            {
                MigrateMap(s.Cooling.ManualSpeeds, legacyId, newIds);
                MigrateMap(s.Cooling.CustomManualSpeeds, legacyId, newIds);
                MigrateMap(s.Cooling.FanOffsets, legacyId, newIds);
                MigrateMap(s.Cooling.FanLockOverrides, legacyId, newIds);
                MigrateMap(s.Cooling.FanRoles, legacyId, newIds);
                MigrateMap(s.Cooling.CustomFanCurveAssignments, legacyId, newIds);
                MigrateIdList(s.Cooling.UncontrolledFanChannels, legacyId, newIds);
                MigrateOrderList(s.Cooling.FanChannelOrder, legacyId, newIds);
                s.Cooling.FanNames.Remove(legacyId);

                foreach (var curve in s.Cooling.Curves)
                {
                    var legacyOutput = curve.Outputs.Find(o => o.Id == legacyId);
                    if (legacyOutput is null) continue;
                    curve.Outputs.RemoveAll(o => o.Id == legacyId);
                    foreach (var id in newIds)
                    {
                        if (!curve.Outputs.Exists(o => o.Id == id))
                            curve.Outputs.Add(new CurveOutputDocument { Id = id, Type = legacyOutput.Type });
                    }
                }
                foreach (var preset in s.Cooling.Presets)
                {
                    MigrateMap(preset.FanCurveAssignments, legacyId, newIds);
                    MigrateMap(preset.ManualSpeeds, legacyId, newIds);
                    MigrateMap(preset.FanOffsets, legacyId, newIds);
                }
            });
        }
        _legacyMigratedSerials.TryAdd(serial, 0);
    }

    // Replaces legacyId with newIds in a fan-id-keyed map, copying the legacy value onto every new id.
    private static void MigrateMap<TValue>(Dictionary<string, TValue> map, string legacyId, IReadOnlyList<string> newIds)
    {
        if (!map.TryGetValue(legacyId, out var value)) return;
        map.Remove(legacyId);
        foreach (var id in newIds) map[id] = value;
    }

    // Replaces legacyId with newIds in an unordered fan-id list.
    private static void MigrateIdList(List<string> list, string legacyId, IReadOnlyList<string> newIds)
    {
        if (!list.Remove(legacyId)) return;
        foreach (var id in newIds)
            if (!list.Contains(id)) list.Add(id);
    }

    // Replaces legacyId with newIds in place, preserving display order.
    private static void MigrateOrderList(List<string>? list, string legacyId, IReadOnlyList<string> newIds)
    {
        if (list is null) return;
        var idx = list.IndexOf(legacyId);
        if (idx < 0) return;
        list.RemoveAt(idx);
        foreach (var id in newIds)
        {
            if (list.Contains(id)) continue;
            list.Insert(idx, id);
            idx++;
        }
    }

    private static string PumpId(string serial, string suffix) => $"{IdPrefix}{serial}:{suffix}";

    private static string FanId(string serial, int channel, int slot, int fanCount, int fanIdx) =>
        fanCount <= 1 ? $"{IdPrefix}{serial}:p{channel}:{slot}" : $"{IdPrefix}{serial}:p{channel}:{slot}:{fanIdx}";

    private static string FanName(string model, int channel, int slot, int fanCount, int fanIdx) =>
        fanCount <= 1 ? $"{model} (Port {channel} #{slot})" : $"{model} (Port {channel} #{slot}-{fanIdx})";

    // channelId shape: "qseries:<serial>:p<channel>:<slot>[:<fanIdx>]", channel restricted
    // to the two physical Nexus Link connectors this hub actually has.
    private static bool TryParseFanId(string channelId, out int channel, out int slot, out int fanIdx)
    {
        channel = 0; slot = 0; fanIdx = 1;
        if (!IsQSeriesId(channelId)) return false;
        var parts = channelId.Split(':');
        if (parts.Length < 4 || parts.Length > 5) return false;
        var portPart = parts[2];
        if (portPart.Length < 2 || portPart[0] != 'p') return false;
        if (!int.TryParse(portPart.AsSpan(1), out channel)) return false;
        if (channel != 1 && channel != 2) return false;
        if (!int.TryParse(parts[3], out slot)) return false;
        if (parts.Length == 5 && !int.TryParse(parts[4], out fanIdx)) return false;
        return true;
    }
}
