using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors.Astral;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Service.Sensors;

/// <summary>
/// Windows sensor provider backed by LibreHardwareMonitorLib. Reads CPU, GPU,
/// Memory, Storage, Motherboard sensors including temperatures, fan speeds,
/// voltages, clock speeds, and load.
///
/// Uses the shared LhmComputer singleton for hardware access so the Computer
/// instance is shared with WindowsFanControlProvider. Updates are cheap
/// (~1-5ms per cycle) because LHM caches hardware handles.
///
/// On non-Windows platforms this class should never be instantiated - the
/// factory in Program.cs gates on RuntimeInformation.IsOSPlatform.
/// </summary>
public sealed class LibreHardwareSensorProvider : ISensorProvider
{
    private readonly LhmComputer _lhm;
    private readonly AstralGpuSupplement _astral = new(new AstralNvApiClient());
    private readonly NvmlPowerLimits _nvmlPowerLimits = new();
    private string? _ramBrandModel;
    private string? _storageBrandModel;

    public LibreHardwareSensorProvider(LhmComputer lhm)
    {
        _lhm = lhm;
    }

    public Task ReadyAsync(CancellationToken ct = default) => _lhm.OpenTask.WaitAsync(ct);

    public string GetCpuModel()
    {
        _lhm.Update();
        var cpu = FindHardware(HardwareType.Cpu).FirstOrDefault();
        return cpu?.Name ?? "";
    }

    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        _lhm.Update();
        var result = new List<HardwareSensor>();
        foreach (var hw in FindHardware(HardwareType.Cpu))
        {
            var mapped = MapSensors(hw);
            result.AddRange(mapped);
            CpuClockAggregates.Append(hw.Identifier.ToString(), hw.Name, mapped, result);
        }
        return result;
    }

    // LibreHardwareMonitor exposes no TjMax sensor, only each core's distance to it.
    public float? GetCpuTjMaxC()
    {
        _lhm.Update();
        var cpu = FindHardware(HardwareType.Cpu).FirstOrDefault();
        if (cpu is null)
        {
            return null;
        }
        var temps = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
            .Select(s => (s.Name, (double)s.Value!.Value));
        return (float?)Nexus.Service.Cooling.ThermalLimits.TjMaxFromCoreDistances(temps);
    }

    public (bool Healthy, float DistanceToTJMax) GetCpuHealth()
    {
        _lhm.Update();
        var cpu = FindHardware(HardwareType.Cpu).FirstOrDefault();
        if (cpu is null) return (true, 0f);

        var tjMax = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault();

        var maxTemp = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault();

        if (tjMax.HasValue && maxTemp.HasValue)
        {
            var distance = tjMax.Value - maxTemp.Value;
            return (distance > 10, distance);
        }

        return (true, 0f);
    }

    public IReadOnlyList<string> GetGpuModels() => GetGpus().Select(g => g.Name).ToList();

    public IReadOnlyList<HardwareSensor> GetGpuSensors() => GetGpus().SelectMany(g => g.Sensors).ToList();

    public IReadOnlyList<GpuReadout> GetGpus()
    {
        _lhm.Update();
        var result = new List<GpuReadout>();
        // DXGI adapter descriptions are never Astral/AIB-enriched, so LUID
        // matching below needs each GPU's raw LHM name alongside the
        // (possibly renamed) display name, keyed by the untouched Id.
        var rawNames = new Dictionary<string, string>();
        foreach (var hw in FindHardware(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel))
        {
            var mapped = MapSensors(hw);
            var displayName = hw.Name;
            if (hw.HardwareType == HardwareType.GpuNvidia)
            {
                var hwIdentifier = hw.Identifier.ToString();
                _astral.AppendSensors(hwIdentifier, hwIdentifier, hw.Name, mapped);
                displayName = _astral.EnrichName(hwIdentifier, hw.Name);
                var powerLimit = _nvmlPowerLimits.WattsFor(hw.Name);
                if (powerLimit > 0)
                {
                    foreach (var sensor in mapped)
                    {
                        if (sensor.Type == "Power" && sensor.Name == "GPU Package") sensor.TheoreticalMaximum = powerLimit;
                    }
                }
            }
            // VRAM total comes from the GPU's "GPU Memory Total" sensor; reuse it
            // as the ceiling for "GPU Memory Used" / "Free" so the client can
            // draw a proportional gauge without juggling sibling lookups.
            float vramTotalMb = hw.Sensors
                .Where(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                .Select(s => SensorValueSanitizer.Sanitize(s.Value ?? 0f))
                .FirstOrDefault();
            if (vramTotalMb > 0)
            {
                foreach (var sensor in mapped)
                {
                    if (sensor.Type == "SmallData" && (sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Free", StringComparison.OrdinalIgnoreCase)))
                    {
                        sensor.TheoreticalMaximum = vramTotalMb;
                    }
                }
            }
            var (vendor, integrated) = ClassifyGpu(hw.HardwareType, hw.Name, vramTotalMb);
            var id = hw.Identifier.ToString();
            rawNames[id] = hw.Name;
            result.Add(new GpuReadout
            {
                Id = id,
                Name = displayName,
                Vendor = vendor,
                Integrated = integrated,
                Sensors = mapped,
            });
        }
        GpuAdapterLuids.Attach(result, rawNames);
        return result;
    }

    // LHM's HardwareType is authoritative for vendor; NVIDIA is always discrete,
    // Intel client GPUs always integrated. AMD is the ambiguous one: a discrete
    // Radeon and an APU's integrated "Radeon Graphics" both report HardwareType
    // GpuAmd. An APU exposes only a tiny UMA carve-out as "GPU Memory Total"
    // (≈512 MB, reported in MB here despite the field name) and an "…Graphics"
    // name with no RX/Pro model, whereas a discrete card reports multiple GB and
    // a model number. Treat either signal as integrated.
    private static (string Vendor, bool Integrated) ClassifyGpu(HardwareType type, string name, float vramTotalMb) => type switch
    {
        HardwareType.GpuNvidia => ("nvidia", false),
        HardwareType.GpuIntel => ("intel", true),
        HardwareType.GpuAmd => ("amd", GpuClassifier.FromName(name).Integrated || (vramTotalMb > 0f && vramTotalMb < 1024f)),
        _ => ("", false),
    };

    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        _lhm.Update();
        var result = new List<HardwareSensor>();
        // Skip LHM's `/vram` (pagefile) hardware - it exposes "Memory Used"
        // and "Memory Available" with the same Type/Name as the physical `/ram`,
        // so flattening both would collide on the client's name-based find()
        // and could display pagefile metrics with a pagefile ceiling.
        foreach (var hw in FindHardware(HardwareType.Memory).Where(h => h.Identifier.ToString() == "/ram"))
        {
            var mapped = MapSensors(hw);
            // RAM ceiling = Used + Available reported by this hardware instance.
            // LHM splits physical and virtual memory into separate hardware (`/ram`
            // and `/vram`); attaching the per-instance total keeps each set's
            // sensors self-describing.
            float used = hw.Sensors
                .Where(s => s.SensorType == SensorType.Data && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                .Select(s => SensorValueSanitizer.Sanitize(s.Value ?? 0f))
                .FirstOrDefault();
            float avail = hw.Sensors
                .Where(s => s.SensorType == SensorType.Data && s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase))
                .Select(s => SensorValueSanitizer.Sanitize(s.Value ?? 0f))
                .FirstOrDefault();
            float total = used + avail;
            if (total > 0)
            {
                foreach (var sensor in mapped)
                {
                    if (sensor.Type == "Data") sensor.TheoreticalMaximum = total;
                    else if (sensor.Type == "Load") sensor.TheoreticalMaximum = 100f;
                }
            }
            result.AddRange(mapped);
        }
        return result;
    }

    public string GetMemoryTotalFormatted()
    {
        _lhm.Update();
        // LHM reports both physical RAM and virtual memory (pagefile) as HardwareType.Memory.
        // Physical RAM has identifier "/ram", virtual has "/vram". Pick physical.
        var mem = FindHardware(HardwareType.Memory)
            .FirstOrDefault(h => h.Identifier.ToString() == "/ram")
            ?? FindHardware(HardwareType.Memory).FirstOrDefault();
        if (mem is null) return "";

        var used = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase));
        var avail = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase));

        if (used?.Value != null && avail?.Value != null)
            return $"{used.Value.Value + avail.Value.Value:F1} GB";

        return "";
    }

    public IReadOnlyDictionary<string, bool> GetRotationalDrives()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var hw in FindHardware(HardwareType.Storage))
        {
            if (_lhm.IsRotational(hw) is { } rotational) result[hw.Identifier.ToString()] = rotational;
        }
        return result;
    }

    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true)
    {
        _lhm.Update();
        var result = new Dictionary<string, StorageComponent>();
        foreach (var di in System.IO.DriveInfo.GetDrives())
        {
            if (!di.IsReady || di.DriveType != System.IO.DriveType.Fixed) continue;
            if (di.TotalSize < 1L * 1024 * 1024 * 1024) continue;
            var label = di.Name.TrimEnd('\\');
            var totalGb = di.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = di.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usedGb = totalGb - freeGb;
            var usePct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;
            result[label] = new StorageComponent
            {
                Id = label,
                Name = di.VolumeLabel.Length > 0 ? $"{di.VolumeLabel} ({label})" : label,
                Capacity = FormatGb(totalGb),
                FreeSpace = FormatGb(freeGb),
                UsedSpace = FormatGb(usedGb),
                UsedPercentage = $"{usePct:F0}%",
                Format = di.DriveFormat,
                Sensors = new List<HardwareSensor>
                {
                    MakeSensor($"storage/{label}/used", "Used", "Data", (float)usedGb, "GB", label, theoreticalMax: (float)totalGb),
                    MakeSensor($"storage/{label}/free", "Free", "Data", (float)freeGb, "GB", label, theoreticalMax: (float)totalGb),
                    MakeSensor($"storage/{label}/usage", "Usage", "Level", (float)usePct, "%", label),
                },
            };
        }
        // LHM SMART per physical drive (composite/warning/critical temp, life,
        // activity, power-on hours, etc), additional to the DriveInfo rows above.
        // The "smart/" id keeps these out of consumers that only want the
        // logical-volume subset (LhmComponentIdentifiers.IsSmartStorageComponent).
        if (includeSmart)
        {
            foreach (var hw in FindHardware(HardwareType.Storage))
            {
                var component = BuildComponent(hw);
                var id = LhmComponentIdentifiers.BuildSmartStorageId(hw.Identifier.ToString());
                result[id] = new StorageComponent
                {
                    Id = id,
                    Name = component.Name,
                    Sensors = component.Sensors,
                };
            }
        }
        return result;
    }

    public IReadOnlyList<string> GetStoragePartitions()
    {
        return System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed && d.TotalSize >= 1L * 1024 * 1024 * 1024)
            .Select(d => d.Name).ToList();
    }

    public IReadOnlyList<StorageDriveInfo> GetStorageInfo()
    {
        return System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed && d.TotalSize >= 1L * 1024 * 1024 * 1024)
            .Select(d => new StorageDriveInfo
            {
                Name = d.VolumeLabel.Length > 0 ? $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})" : d.Name.TrimEnd('\\'),
                Partition = d.Name,
                Capacity = FormatGb(d.TotalSize / (1024.0 * 1024.0 * 1024.0)),
            }).ToList();
    }

    public IReadOnlyList<HardwareSensor> GetMotherboardSensors()
    {
        _lhm.Update();
        var mobo = FindHardware(HardwareType.Motherboard).FirstOrDefault();
        if (mobo is null) return Array.Empty<HardwareSensor>();

        // Motherboard has sub-hardware (IO chips) with the real sensors
        var sensors = new List<HardwareSensor>();
        sensors.AddRange(MapSensors(mobo));
        foreach (var sub in mobo.SubHardware)
        {
            sub.Update();
            sensors.AddRange(MapSensors(sub));
        }
        return sensors;
    }

    public string GetMotherboardModel()
    {
        _lhm.Update();
        return FindHardware(HardwareType.Motherboard).FirstOrDefault()?.Name ?? "";
    }

    public string GetRamBrandModel()
    {
        if (_ramBrandModel is not null) return _ramBrandModel;
        // PowerShell CIM is faster + AOT-safer than System.Management WMI. Pull
        // Manufacturer + PartNumber for the first physical DIMM. Multi-DIMM rigs
        // almost always mix kits from the same SKU, so one module is enough for
        // catalog matching.
        var csv = ShellOut("powershell.exe", 5000,
            "-NoProfile", "-Command",
            "Get-CimInstance -ClassName Win32_PhysicalMemory | Select-Object -First 1 Manufacturer,PartNumber | ConvertTo-Csv -NoTypeInformation");
        _ramBrandModel = ParseCsvBrandModel(csv);
        return _ramBrandModel;
    }

    public string GetStorageBrandModel()
    {
        if (_storageBrandModel is not null) return _storageBrandModel;
        // The disk holding the OS partition: the benchmark's DiskSpd file lands
        // in the service temp dir under %SystemRoot%. Get-Disk covers an OS
        // volume with no Get-PhysicalDisk row (Storage Spaces, RAID).
        var csv = ShellOut("powershell.exe", 5000,
            "-NoProfile", "-Command",
            "$n = (Get-Partition -DriveLetter $env:SystemDrive[0]).DiskNumber; $d = Get-PhysicalDisk | Where-Object DeviceId -eq $n; if (-not $d) { $d = Get-Disk -Number $n }; $d | Select-Object -First 1 Manufacturer,Model | ConvertTo-Csv -NoTypeInformation");
        _storageBrandModel = ParseCsvBrandModel(csv);
        return _storageBrandModel;
    }

    /// <summary>
    /// ConvertTo-Csv output on a 2-column (Manufacturer, Model) select produces
    /// two quoted-or-bare lines: header row, then the values row. Strip quotes,
    /// drop sentinel values like "Standard disk drives" / "Not Specified", join
    /// manufacturer + model into a single matcher string. Returns "" if neither
    /// column yields useful content.
    /// </summary>
    private static string ParseCsvBrandModel(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return "";
        var rows = csv.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (rows.Count < 2) return "";
        var vals = SplitCsvRow(rows[1]);
        if (vals.Count < 2) return "";
        var mfg = CleanField(vals[0]);
        var model = CleanField(vals[1]);
        if (string.IsNullOrEmpty(mfg) && string.IsNullOrEmpty(model)) return "";
        if (string.IsNullOrEmpty(mfg)) return model;
        if (string.IsNullOrEmpty(model)) return mfg;
        // Avoid "Samsung Samsung 980 PRO" when the model already starts with
        // the manufacturer, which PowerShell's Model column often does.
        if (model.StartsWith(mfg, StringComparison.OrdinalIgnoreCase)) return model;
        return $"{mfg} {model}";
    }

    private static List<string> SplitCsvRow(string row)
    {
        var cells = new List<string>();
        var i = 0;
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;
        while (i < row.Length)
        {
            var c = row[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"') { cur.Append('"'); i += 2; continue; }
                if (c == '"') { inQuotes = false; i++; continue; }
                cur.Append(c); i++;
            }
            else
            {
                if (c == '"') { inQuotes = true; i++; continue; }
                if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); i++; continue; }
                cur.Append(c); i++;
            }
        }
        cells.Add(cur.ToString());
        return cells;
    }

    private static string CleanField(string v)
    {
        var s = v.Trim();
        // Common sentinel placeholders PowerShell surfaces when the BIOS / SPD
        // chip didn't populate the field. Treat as unknown.
        if (s.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("Standard disk drives", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("(Standard disk drives)", StringComparison.OrdinalIgnoreCase)) return "";
        return s;
    }

    private static string ShellOut(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            return stdout;
        }
        catch { return ""; }
    }

    public SensorExtras GetSensorExtras()
    {
        _lhm.Update();

        var extras = new SensorExtras();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Battery:
                    extras.Batteries.Add(BuildComponent(hw));
                    break;
                case HardwareType.Network:
                    extras.Nics.Add(BuildComponent(hw));
                    break;
                case HardwareType.Cooler:
                    extras.Coolers.Add(BuildComponent(hw));
                    break;
                case HardwareType.Psu:
                    extras.Psus.Add(BuildComponent(hw));
                    break;
                case HardwareType.Storage:
                    extras.NvmeStorage.Add(BuildComponent(hw));
                    break;
                case HardwareType.EmbeddedController:
                    extras.EmbeddedControllers.Add(BuildComponent(hw));
                    break;
                case HardwareType.Memory:
                    if (LhmComponentIdentifiers.IsDimmModule(hw.Identifier.ToString()))
                        extras.MemoryModules.Add(BuildComponent(hw));
                    break;
            }
        }

        return extras;
    }

    private static HardwareComponent BuildComponent(IHardware hw)
    {
        // Sub-hardware (e.g. SuperIO chips on motherboards) carries the actual
        // sensors for some HardwareTypes; walk one level so we surface them all.
        var sensors = MapSensors(hw);
        foreach (var sub in hw.SubHardware)
        {
            sub.Update();
            sensors.AddRange(MapSensors(sub));
        }
        return new HardwareComponent
        {
            Id = hw.Identifier.ToString(),
            Name = string.IsNullOrWhiteSpace(hw.Name) ? hw.HardwareType.ToString() : hw.Name,
            Sensors = sensors,
        };
    }

    public string GetOsVersion() => RuntimeInformation.OSDescription;

    public void SetPollingRate(int pollingRate) { }

    // ── Helpers ──

    private IEnumerable<IHardware> FindHardware(params HardwareType[] types)
    {
        return _lhm.Instance.Hardware.Where(h => types.Contains(h.HardwareType));
    }

    private static List<HardwareSensor> MapSensors(IHardware hw)
    {
        return hw.Sensors.Select(s =>
        {
            var value = SensorValueSanitizer.Sanitize(s.Value ?? 0f);
            var min = SensorValueSanitizer.Sanitize(s.Min ?? 0f);
            var max = SensorValueSanitizer.Sanitize(s.Max ?? 0f);
            return new HardwareSensor
            {
                Id = s.Identifier.ToString(),
                Name = s.Name,
                Type = MapSensorType(s.SensorType),
                Value = value,
                Min = min,
                Max = max,
                Units = MapUnits(s.SensorType),
                Formatted = FormatValue(value, s.SensorType),
                FormattedMax = FormatValue(max, s.SensorType),
                FormattedMin = FormatValue(min, s.SensorType),
                Parent = new SensorParent { Id = hw.Identifier.ToString(), Name = hw.Name },
            };
        }).ToList();
    }

    private static string MapSensorType(SensorType type) => type switch
    {
        SensorType.Voltage => "Voltage",
        SensorType.Current => "Current",
        SensorType.Clock => "Clock",
        SensorType.Temperature => "Temperature",
        SensorType.Load => "Load",
        SensorType.Frequency => "Frequency",
        SensorType.Fan => "Fan",
        SensorType.Flow => "Flow",
        SensorType.Control => "Control",
        SensorType.Level => "Level",
        SensorType.Factor => "Factor",
        SensorType.Power => "Power",
        SensorType.Data => "Data",
        SensorType.SmallData => "SmallData",
        SensorType.Throughput => "Throughput",
        SensorType.TimeSpan => "TimeSpan",
        SensorType.Energy => "Energy",
        SensorType.Noise => "Noise",
        _ => type.ToString(),
    };

    private static string MapUnits(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Power => "W",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.TimeSpan => "s",
        _ => "",
    };

    private static string FormatValue(float value, SensorType type) => type switch
    {
        SensorType.Temperature => $"{value:F1} °C",
        SensorType.Load or SensorType.Control or SensorType.Level => $"{value:F1}%",
        SensorType.Clock => $"{value:F0} MHz",
        SensorType.Voltage => $"{value:F3} V",
        SensorType.Current => $"{value:F3} A",
        SensorType.Fan => $"{value:F0} RPM",
        SensorType.Power => $"{value:F1} W",
        SensorType.Data => $"{value:F2} GB",
        SensorType.SmallData => $"{value:F0} MB",
        SensorType.Energy => $"{value:F0} mWh",
        SensorType.Throughput => FormatThroughput(value),
        SensorType.TimeSpan => FormatTimeSpan(value),
        _ => $"{value:F1}",
    };

    private static string FormatThroughput(float bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "0 B/s";
        if (bytesPerSec >= 1024 * 1024 * 1024)
            return $"{bytesPerSec / 1024.0 / 1024.0 / 1024.0:F2} GB/s";
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / 1024.0 / 1024.0:F2} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    // LHM reports Battery "Remaining Time" and PSU "Uptime"/"Total uptime" in seconds.
    private static string FormatTimeSpan(float seconds)
    {
        if (seconds <= 0)
            return "0";
        if (seconds < 3600)
            return $"{seconds / 60:F0}m";
        if (seconds < 86400)
            return $"{seconds / 3600:F1}h";
        return $"{seconds / 86400:F1}d";
    }

    private static string FormatGb(double gb) => gb >= 1000 ? $"{gb / 1024.0:F2} TB" : $"{gb:F2} GB";

    private static HardwareSensor MakeSensor(string id, string name, string type, float value, string units, string parentName, float theoreticalMax = 0f)
    {
        var formatted = type switch
        {
            "Load" or "Level" => $"{value:F1}{units}",
            "Data" => $"{value:F2} {units}",
            _ => value > 0 ? $"{value:F0}" : "",
        };
        return new HardwareSensor
        {
            Id = id, Name = name, Type = type, Value = value, Units = units,
            Formatted = formatted,
            TheoreticalMaximum = theoreticalMax,
            Parent = new SensorParent { Id = id.Split('/')[0], Name = parentName },
        };
    }
}
