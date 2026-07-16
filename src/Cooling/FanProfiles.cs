using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Defaults;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Built-in fan presets: Off, Silent, Balanced, Turbo, Custom.
///
/// Applying Silent / Balanced / Turbo ensures a single shared "preset
/// curve" exists with id `preset-{name}`, attaches every non-locked fan to
/// it, and detaches those fans from any user curve. User curves are NOT
/// deleted. Locked channels (pumps by default, or any channel the user
/// locked via <see cref="IsLocked"/>) are exempt and keep whatever already
/// drives them.
///
/// Applying Custom restores the last-known per-fan curve assignments and
/// manual duties that were active before a preset took over. Locked channels
/// are exempt.
///
/// Applying Off detaches every non-locked fan from every curve and releases
/// every non-locked fan to BIOS Control.
///
/// The preset curve's parameters survive round-trips: re-applying a preset
/// after the user edits its MinTemp/MaxTemp/etc. keeps the user's edits.
/// Deleting a preset curve is allowed; the next activation recreates it
/// with the default values from PresetDefaults.
/// </summary>
public static class FanProfiles
{
    /// <summary>
    /// True when a channel is exempt from Silent/Balanced/Turbo/Off/Custom
    /// preset applies. An explicit entry in <paramref name="overrides"/> wins;
    /// absent from the dict defaults to locked for pumps, unlocked otherwise.
    /// </summary>
    public static bool IsLocked(FanChannel ch, IReadOnlyDictionary<string, bool> overrides) =>
        overrides.TryGetValue(ch.Id, out var v) ? v : ch.Kind == FanKinds.Pump;

    /// <summary>
    /// Write ch's lock override, collapsing to "no entry" when the requested
    /// value matches the channel's default (see <see cref="IsLocked"/>) so
    /// the dict only holds real deviations.
    /// </summary>
    public static void SetLockOverride(FanChannel ch, bool locked, IConfigStore store)
    {
        var def = ch.Kind == FanKinds.Pump;
        store.Update(s =>
        {
            if (locked == def)
            {
                s.Cooling.FanLockOverrides.Remove(ch.Id);
            }
            else
            {
                s.Cooling.FanLockOverrides[ch.Id] = locked;
            }
        });
    }

    public static List<FanProfile> GetBuiltInProfiles() => new()
    {
        new FanProfile { Name = "off", Description = "All fans released to BIOS Control" },
        new FanProfile { Name = "silent", Description = "Quiet operation - fans stay low until temperatures demand it" },
        new FanProfile { Name = "balanced", Description = "Moderate cooling - responsive but not aggressive" },
        new FanProfile { Name = "turbo", Description = "Maximum cooling - fans run fast to keep temperatures low" },
        new FanProfile { Name = "custom", Description = "User-defined per-fan curve assignments" },
    };

    /// <summary>
    /// Apply the named preset. Returns the canonical preset name actually applied.
    /// "auto" is treated as a synonym for "off" for backward compatibility.
    /// </summary>
    public static string Apply(string profileName, IFanControlProvider fans, IConfigStore store)
    {
        var canonical = Canonicalize(profileName);
        var channels = fans.GetFanChannels();
        var temps = fans.GetTemperatureSources();
        var inputSensor = PreferredInput(temps);
        var fanIds = channels.Select(c => c.Id).ToHashSet();
        // Locked channels (pumps by default, or any channel the user locked) are
        // excluded from the Silent/Balanced/Turbo and Custom presets, which must
        // not retarget what drives them. Off still releases every channel to
        // BIOS, locked or not.
        var lockOverrides = store.Load().Cooling.FanLockOverrides;
        var lockedIds = channels.Where(c => IsLocked(c, lockOverrides)).Select(c => c.Id).ToHashSet();

        List<(string FanId, int Duty)>? restoredManual = null;
        store.Update(s =>
        {
            // Snapshot the user's custom mapping on the way out of "custom":
            // curve assignments and manual duties both, so Custom restores the
            // full arrangement. The manual copy is load-bearing for the Off
            // round-trip - Off's per-channel release deletes the live
            // ManualSpeeds entries.
            if (s.Cooling.ActivePreset == "custom" && canonical != "custom")
            {
                s.Cooling.CustomFanCurveAssignments = SnapshotMapping(s.Cooling.Curves, fanIds);
                s.Cooling.CustomManualSpeeds = new Dictionary<string, int>(s.Cooling.ManualSpeeds);
            }

            switch (canonical)
            {
                case "silent":
                case "balanced":
                case "turbo":
                    {
                        var presetCurve = EnsurePresetCurve(s.Cooling.Curves, canonical, inputSensor);
                        // Detach fans (not locked channels) from non-preset curves so the
                        // preset curve owns them; a locked channel on its own curve stays
                        // attached.
                        foreach (var curve in s.Cooling.Curves)
                        {
                            if (curve.Preset is null && curve.Id != presetCurve.Id)
                            {
                                curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id) && !lockedIds.Contains(o.Id));
                            }
                            else if (curve.Preset is not null && curve.Id != presetCurve.Id)
                            {
                                // Other preset curves (not the active one) keep only
                                // locked channels, so a fan locked onto that preset stays
                                // put when the user activates a different one.
                                curve.Outputs.RemoveAll(o => !lockedIds.Contains(o.Id));
                            }
                        }
                        // Preserve locked channels already attached to THIS curve (e.g. a
                        // fan locked while sitting on this preset) alongside every
                        // non-locked channel, which this preset now claims.
                        var keepLocked = presetCurve.Outputs.Where(o => lockedIds.Contains(o.Id)).ToList();
                        presetCurve.Outputs = channels
                            .Where(c => !lockedIds.Contains(c.Id))
                            .Select(c => new CurveOutputDocument { Id = c.Id, Type = "Fan" })
                            .Concat(keepLocked)
                            .ToList();
                        s.Cooling.ActivePreset = canonical;
                        break;
                    }
                case "custom":
                    {
                        var wasCustom = s.Cooling.ActivePreset == "custom";
                        // Detach every preset curve, except locked channels already
                        // sitting on one - a locked channel must not move.
                        foreach (var curve in s.Cooling.Curves.Where(c => c.Preset is not null))
                        {
                            curve.Outputs.RemoveAll(o => !lockedIds.Contains(o.Id));
                        }
                        // Restore the saved custom mapping. Any fan missing from the
                        // snapshot stays unattached -> falls through to BIOS Control.
                        var snapshot = s.Cooling.CustomFanCurveAssignments ?? new Dictionary<string, string>();
                        // First clear every non-preset curve's outputs for fans we know about,
                        // so we don't leave stale attachments from a prior state. Locked
                        // channels are left alone.
                        foreach (var curve in s.Cooling.Curves.Where(c => c.Preset is null))
                        {
                            curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id) && !lockedIds.Contains(o.Id));
                        }
                        foreach (var (fanId, curveId) in snapshot)
                        {
                            if (!fanIds.Contains(fanId) || lockedIds.Contains(fanId)) continue;
                            var curve = s.Cooling.Curves.FirstOrDefault(c => c.Id == curveId && c.Preset is null);
                            if (curve is null) continue;
                            if (!curve.Outputs.Any(o => o.Id == fanId))
                            {
                                curve.Outputs.Add(new CurveOutputDocument { Id = fanId, Type = "Fan" });
                            }
                        }
                        // Restore the saved manual duties the same way; a curve
                        // attachment restored above wins over a manual entry.
                        // Gated on an actual transition INTO custom - re-applying
                        // custom while custom must not clobber live manual state
                        // with the stale exit snapshot. Unlike the assignment
                        // restore, absent channels keep their restored entry:
                        // the engine replays it when the channel appears. The
                        // explicit lock-override check covers locked fans whose
                        // hub is disconnected right now (absent from lockedIds).
                        if (!wasCustom)
                        {
                            var manualSnapshot = s.Cooling.CustomManualSpeeds ?? new Dictionary<string, int>();
                            foreach (var (fanId, duty) in manualSnapshot)
                            {
                                if (lockedIds.Contains(fanId)) continue;
                                if (s.Cooling.FanLockOverrides.TryGetValue(fanId, out var lockedOverride) && lockedOverride) continue;
                                if (s.Cooling.Curves.Any(c => c.Outputs.Any(o => o.Id == fanId))) continue;
                                s.Cooling.ManualSpeeds[fanId] = duty;
                                (restoredManual ??= new()).Add((fanId, duty));
                            }
                        }
                        s.Cooling.ActivePreset = "custom";
                        break;
                    }
                case "off":
                default:
                    {
                        // Off releases every channel to BIOS, locked or not:
                        // detach all fans from all curves.
                        foreach (var curve in s.Cooling.Curves)
                        {
                            curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id));
                        }
                        s.Cooling.ActivePreset = "off";
                        break;
                    }
            }
        });

        // Off: also actively release fans at the hardware layer so any prior
        // manual override stops holding the duty.
        if (canonical == "off")
        {
            foreach (var ch in channels)
            {
                fans.ReleaseFan(ch.Id);
            }
        }

        // Custom: drive the restored manual duties onto present channels here.
        // CurveEngine's replay latch skips ids it already replayed this run,
        // so entries deleted while Off (and re-created above) would otherwise
        // never reach hardware. SetFanSpeed also re-records the entry via the
        // provider, which is idempotent for the value just restored.
        if (restoredManual is not null)
        {
            foreach (var (fanId, duty) in restoredManual)
            {
                if (fanIds.Contains(fanId))
                {
                    fans.SetFanSpeed(fanId, duty);
                }
            }
        }

        return canonical;
    }

    /// <summary>
    /// Recompute ActivePreset from the per-fan effective control state. Each
    /// fan resolves to one of: BIOS, Manual, or attached to a specific curve.
    /// Rules:
    ///   - Every fan BIOS (no curve attachment, no manual override) -> "off".
    ///   - Every fan attached to the same `preset-{name}` curve and no manual
    ///     overrides -> that preset.
    ///   - Anything else -> "custom".
    /// "Manual on at least one fan" never resolves to "off" or a preset; the
    /// user explicitly broke out of the shared regime.
    /// Fans classified as Unresponsive are ignored: they're physically
    /// disconnected, can't be wired to any curve, and would otherwise force
    /// every preset check to fail on `fanIds.All(...)`.
    /// </summary>
    public static string DerivePresetFromCurves(IConfigStore store, IFanControlProvider fans)
    {
        var channels = fans.GetFanChannels();
        var settings = store.Load();
        // Locked channels never join a preset curve (Apply excludes them), so
        // they must be excluded here too - otherwise their absence from the
        // preset curve would fail the "all fans on the preset" check and force
        // "custom".
        var fanIds = channels
            .Where(c => c.Classification != "Unresponsive" && !IsLocked(c, settings.Cooling.FanLockOverrides))
            .Select(c => c.Id)
            .ToHashSet();
        if (fanIds.Count == 0) return "custom";

        var curves = settings.Cooling.Curves;
        var attachment = new Dictionary<string, string>(); // fanId -> curveId
        foreach (var curve in curves)
        {
            foreach (var o in curve.Outputs)
            {
                if (fanIds.Contains(o.Id) && !attachment.ContainsKey(o.Id))
                {
                    attachment[o.Id] = curve.Id;
                }
            }
        }
        // Manual overrides only count for fans NOT already driven by a curve.
        // CurveEngine writes through DriveFanSpeed (no ManualSpeeds touch), so
        // it can't pollute the dict. But a user can still set manual on a fan
        // and then attach it to a curve via the wire-DnD, leaving a stale
        // entry that's no longer in effect. Counting those would flip a
        // perfectly-driven preset to "custom" the next time curves are saved.
        var manualUnattached = settings.Cooling.ManualSpeeds.Keys
            .Where(id => fanIds.Contains(id) && !attachment.ContainsKey(id))
            .ToHashSet();

        // All fans BIOS = no attachment AND no manual override.
        if (attachment.Count == 0 && manualUnattached.Count == 0) return "off";

        // All fans on the same preset curve, with no manual overrides.
        if (manualUnattached.Count == 0)
        {
            foreach (var presetName in new[] { "silent", "balanced", "turbo" })
            {
                var presetId = $"preset-{presetName}";
                if (fanIds.All(id => attachment.TryGetValue(id, out var cid) && cid == presetId))
                {
                    return presetName;
                }
            }
        }

        return "custom";
    }

    /// <summary>
    /// Remove every curve attachment for this fan. Used by the Manual /
    /// BIOS-release routes so the user's explicit per-fan choice isn't
    /// re-overridden by CurveEngine on the next tick (it would otherwise see
    /// the fan still in a curve's Outputs and drive it again), and so the
    /// active-preset derivation correctly sees the fan as no longer on the
    /// shared preset.
    /// </summary>
    public static void DetachFanFromCurves(string fanId, IConfigStore store)
    {
        store.Update(s =>
        {
            foreach (var curve in s.Cooling.Curves)
            {
                curve.Outputs.RemoveAll(o => o.Id == fanId);
            }
        });
    }

    /// <summary>
    /// Seed the Silent / Balanced / Turbo preset curves on first run so all
    /// three exist the first time the cooling page is opened on a fresh install.
    /// Runs at most once per profile (tracked by
    /// <see cref="CoolingSettings.CurvesSeeded"/>) and only adds curves when the
    /// profile is empty, so neither an existing install nor a user who later
    /// deletes every curve gets the presets resurrected. Fan outputs are
    /// attached separately by <see cref="Apply"/> for the active preset only.
    /// Returns true when curves were seeded.
    /// </summary>
    public static bool SeedDefaultPresetCurves(IFanControlProvider fans, IConfigStore store)
    {
        if (store.Load().Cooling.CurvesSeeded) return false;
        var inputSensor = PreferredInput(fans.GetTemperatureSources());
        var seeded = false;
        store.Update(s =>
        {
            if (s.Cooling.CurvesSeeded) return;
            // Mark first-run seeding done unconditionally: an upgraded install
            // that already has curves must not be re-checked on later boots.
            s.Cooling.CurvesSeeded = true;
            if (s.Cooling.Curves.Count > 0) return;
            foreach (var name in new[] { "silent", "balanced", "turbo" })
            {
                s.Cooling.Curves.Add(BuildPresetCurve(name, inputSensor));
            }
            seeded = true;
        });
        return seeded;
    }

    /// <summary>
    /// True when a preset curve's Type + Graph points still match
    /// <see cref="PresetDefaults"/>. Used by /cooling/curves so the SPA can
    /// gate the Reset-to-defaults button without having to mirror the default
    /// values locally. Returns false for non-Graph preset curves or curves
    /// without a Preset flag.
    /// </summary>
    public static bool IsPresetCurveAtDefaults(CurveDocument c)
    {
        if (c.Preset is null) return false;
        if (c.Type != "Graph" || c.Graph is null) return false;
        var d = PresetDefaults.For(c.Preset);
        // Float `==` is sound here: the default points are whole numbers and
        // the drag / JSON round-trip preserves the same canonical values.
        if (c.Graph.ResponseTime != d.ResponseTime) return false;
        // Default presets carry no global scaling; a changed SpeedModifier
        // (the engine multiplies every point by it) counts as an edit.
        if (c.Graph.SpeedModifier != 1.0) return false;
        var pts = DefaultPresetPoints(d);
        if (c.Graph.Points.Count != pts.Count) return false;
        for (int i = 0; i < pts.Count; i++)
        {
            if (c.Graph.Points[i].Temp != pts[i].Temp || c.Graph.Points[i].Speed != pts[i].Speed)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Restore a Silent / Balanced / Turbo preset curve to its default
    /// Linear template. Fan attachments + list position are preserved so the
    /// active preset stays in effect; only the template resets. If the curve
    /// was previously deleted, recreate it at defaults via EnsurePresetCurve.
    /// </summary>
    public static void ResetPresetCurve(string presetName, IFanControlProvider fans, IConfigStore store)
    {
        var canonical = (presetName ?? "").ToLowerInvariant();
        if (canonical != "silent" && canonical != "balanced" && canonical != "turbo") return;
        var inputSensor = PreferredInput(fans.GetTemperatureSources());
        var defaults = PresetDefaults.For(canonical);

        store.Update(s =>
        {
            // EnsurePresetCurve returns the existing curve (normalizing the
            // Preset flag) or creates a fresh one at defaults. Either way we
            // then forcibly overwrite the template fields below; the redundant
            // write on the just-created path is harmless.
            var curve = EnsurePresetCurve(s.Cooling.Curves, canonical, inputSensor);
            curve.Name = DisplayName(canonical);
            // Preset defaults are multi-point (Graph) curves; the Linear params
            // stay populated as a fallback if the user switches the type.
            curve.Type = "Graph";
            // Only overwrite the input binding when we actually have a sensor
            // to bind to - a transient LHM read during reset shouldn't strip
            // a perfectly valid existing input.
            if (inputSensor is not null)
            {
                curve.Input = new CurveInputDocument { Id = inputSensor.Id, Type = "Temperature", Device = inputSensor.Category };
            }
            curve.Linear = new LinearCurveData
            {
                ResponseTime = defaults.ResponseTime,
                MinTemp = defaults.MinTemp, MaxTemp = defaults.MaxTemp,
                MinSpeed = defaults.MinSpeed, MaxSpeed = defaults.MaxSpeed,
            };
            curve.Graph = new GraphCurveData
            {
                ResponseTime = defaults.ResponseTime,
                SpeedModifier = 1.0,
                Points = DefaultPresetPoints(defaults),
            };
            curve.Flat = null;
            curve.Mixed = null;
        });
    }

    /// <summary>
    /// Snapshot the current per-fan curve assignment. Only fans driven by a
    /// non-preset (user) curve are recorded; fans on BIOS Control or driven
    /// by a preset curve produce no entry.
    /// </summary>
    private static Dictionary<string, string> SnapshotMapping(List<CurveDocument> curves, HashSet<string> fanIds)
    {
        var map = new Dictionary<string, string>();
        foreach (var curve in curves)
        {
            if (curve.Preset is not null) continue;
            foreach (var o in curve.Outputs)
            {
                if (fanIds.Contains(o.Id))
                {
                    map[o.Id] = curve.Id;
                }
            }
        }
        return map;
    }

    private static CurveDocument EnsurePresetCurve(List<CurveDocument> curves, string presetName, TemperatureSource? inputSensor)
    {
        var existing = curves.FirstOrDefault(c => c.Id == $"preset-{presetName}");
        if (existing is not null)
        {
            // Make sure the Preset flag is set; tolerate older settings written
            // before the field existed.
            if (existing.Preset != presetName) existing.Preset = presetName;
            return existing;
        }
        var doc = BuildPresetCurve(presetName, inputSensor);
        curves.Add(doc);
        return doc;
    }

    /// <summary>
    /// A fresh preset curve at its <see cref="PresetDefaults"/> template, with
    /// no fan outputs attached. Used both to create a preset lazily on first
    /// activation and to seed all three on a blank install.
    /// </summary>
    private static CurveDocument BuildPresetCurve(string presetName, TemperatureSource? inputSensor)
    {
        var defaults = PresetDefaults.For(presetName);
        return new CurveDocument
        {
            Id = $"preset-{presetName}",
            Name = DisplayName(presetName),
            Type = "Graph",
            Preset = presetName,
            Input = inputSensor is null
                ? new CurveInputDocument()
                : new CurveInputDocument { Id = inputSensor.Id, Type = "Temperature", Device = inputSensor.Category },
            Outputs = new List<CurveOutputDocument>(),
            Linear = new LinearCurveData
            {
                ResponseTime = defaults.ResponseTime,
                MinTemp = defaults.MinTemp,
                MaxTemp = defaults.MaxTemp,
                MinSpeed = defaults.MinSpeed,
                MaxSpeed = defaults.MaxSpeed,
            },
            Graph = new GraphCurveData
            {
                ResponseTime = defaults.ResponseTime,
                SpeedModifier = 1.0,
                Points = DefaultPresetPoints(defaults),
            },
        };
    }

    private static TemperatureSource? PreferredInput(IReadOnlyList<TemperatureSource> temps)
    {
        return temps.FirstOrDefault(t => t.Category == "CPU" && t.Name.Contains("Package", System.StringComparison.OrdinalIgnoreCase))
            ?? temps.FirstOrDefault(t => t.Category == "CPU")
            ?? temps.FirstOrDefault();
    }

    private static string Canonicalize(string profileName)
    {
        return (profileName ?? "").ToLowerInvariant() switch
        {
            "off" or "auto" => "off",
            "silent" => "silent",
            "balanced" => "balanced",
            "turbo" => "turbo",
            "custom" => "custom",
            _ => "custom",
        };
    }

    private static string DisplayName(string presetName) => presetName switch
    {
        "silent" => "Silent",
        "balanced" => "Balanced",
        "turbo" => "Turbo",
        _ => presetName,
    };

    private const int DefaultPresetPointCount = 5;

    /// <summary>
    /// A preset's default multi-point curve: temps spaced evenly across the
    /// preset's band, with speed eased by smootherstep so the first and last
    /// segments ramp gently and the mid-band climbs steeper. Endpoints stay on
    /// the band's corners. Whole-number rounding keeps
    /// <see cref="IsPresetCurveAtDefaults"/>'s exact comparison valid.
    /// </summary>
    private static List<Persistence.GraphPoint> DefaultPresetPoints(PresetCurveDefaults d)
    {
        var pts = new List<Persistence.GraphPoint>(DefaultPresetPointCount);
        for (int i = 0; i < DefaultPresetPointCount; i++)
        {
            double f = i / (double)(DefaultPresetPointCount - 1);
            double eased = f * f * f * (f * (f * 6 - 15) + 10);
            pts.Add(new Persistence.GraphPoint
            {
                Temp = System.Math.Round(d.MinTemp + f * (d.MaxTemp - d.MinTemp)),
                Speed = System.Math.Round(d.MinSpeed + eased * (d.MaxSpeed - d.MinSpeed)),
            });
        }
        return pts;
    }

    /// <summary>
    /// Default Linear curve parameters per preset. Preset curves are
    /// recreated from these values when the user has deleted them.
    /// Values are sourced from data/install-defaults.json via
    /// <see cref="InstallDefaults"/>.
    /// </summary>
    public readonly record struct PresetCurveDefaults(double ResponseTime, double MinTemp, double MaxTemp, double MinSpeed, double MaxSpeed);

    public static class PresetDefaults
    {
        public static PresetCurveDefaults For(string presetName)
        {
            if (InstallDefaults.Cooling.Presets.TryGetValue(presetName, out var p))
                return new(p.ResponseTime, p.MinTemp, p.MaxTemp, p.MinSpeed, p.MaxSpeed);
            // Fallback for unknown preset names (canonical guards upstream prevent this in practice).
            return new(1.0, 30, 80, 30, 100);
        }
    }
}
