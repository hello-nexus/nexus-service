using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Sensors;

/// <summary>
/// Linux sensor provider. Reads CPU/fan/voltage data from /sys/class/hwmon/,
/// motherboard info from /sys/class/dmi/id/, memory from /proc/meminfo,
/// and GPU metrics from nvidia-smi (NVIDIA only; AMD/Intel fall back to lspci name).
///
/// All reads are cheap (file reads under sysfs/procfs) except nvidia-smi which
/// is a subprocess - GPU output is cached for 1s so the hot path stays fast.
/// Every method catches internally and returns empty on failure (never throws).
/// </summary>
public sealed class LinuxSensorProvider : ISensorProvider
{
    private static readonly IReadOnlyList<HardwareSensor> EmptySensors = Array.Empty<HardwareSensor>();
    private static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();

    // Linux hardware reads are shell / sysfs-driven and synchronous - Get*
    // methods cache lazily on first call. No async warmup window to wait on.
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

    private string? _cpuModel;
    private string? _moboModel;
    private string? _memTotal;
    private string? _ramBrandModel;
    private string? _storageBrandModel;
    private long _memTotalBytes = -1;
    private int? _cpuLogicalCores;
    private int? _cpuPhysicalCores;

    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _hasPrev;

    // Per-core tick cache (idx → (idle, total))
    private readonly Dictionary<int, (ulong Idle, ulong Total)> _prevCoreTicks = new();

    // Context switches / interrupts / processes - delta-based rates
    private ulong _prevCtxt;
    private ulong _prevIntr;
    private ulong _prevProcesses;
    private DateTime _prevStatTime;

    // RAPL energy counters (Intel) - µJ delta per interval
    private long _prevRaplEnergyUj = -1;
    private DateTime _prevRaplTime;

    // Per-NIC bytes - prev rx/tx per interface
    private readonly Dictionary<string, (ulong Rx, ulong Tx, DateTime At)> _prevNetStats = new();

    private List<string>? _gpuModels;
    private string _gpuSmiCsv = "";
    private DateTime _gpuSmiFetchedAt;

    private static readonly TimeSpan GpuCacheTtl = TimeSpan.FromSeconds(1);

    public string GetCpuModel()
    {
        if (_cpuModel is not null)
        {
            return _cpuModel;
        }

        _cpuModel = "";
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        _cpuModel = line[(idx + 1)..].Trim();
                    }
                    break;
                }
            }
        }
        catch { }
        return _cpuModel;
    }

    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        var sensors = new List<HardwareSensor>();
        var model = GetCpuModel();

        var load = ReadCpuLoad();
        if (load is not null)
        {
            sensors.Add(MakeSensor("cpu/load", "CPU Total", "Load", (float)load.Value, "%", model));
        }

        EnsureCoreCounts();
        if (_cpuLogicalCores is int logical && logical > 0)
        {
            sensors.Add(MakeSensor("cpu/cores", "Logical Cores", "Factor", logical, "", model));
        }
        if (_cpuPhysicalCores is int physical && physical > 0)
        {
            sensors.Add(MakeSensor("cpu/physical-cores", "Physical Cores", "Factor", physical, "", model));
        }

        var maxMhz = ReadCpuMaxFreqMhz();
        if (maxMhz > 0)
        {
            sensors.Add(MakeSensor("cpu/clock", "Max Clock", "Clock", maxMhz, "MHz", model));
        }

        foreach (var (name, idFrag, tempC) in EnumerateHwmonTemps(preferredNames: new[] { "k10temp", "coretemp", "zenpower" }))
        {
            sensors.Add(MakeSensor($"cpu/temp/{idFrag}", name, "Temperature", tempC, "°C", model));
        }

        // Per-core load
        foreach (var (coreIndex, pct) in ReadPerCoreLoad())
        {
            sensors.Add(MakeSensor($"cpu/core/{coreIndex}/load", $"Core {coreIndex} Load", "Load", pct, "%", model));
        }

        // Per-core current clock (MHz)
        var coreClocks = new List<HardwareSensor>();
        foreach (var (coreIndex, mhz) in ReadPerCoreClock())
        {
            coreClocks.Add(MakeSensor($"cpu/core/{coreIndex}/clock", $"Core {coreIndex} Clock", "Clock", mhz, "MHz", model));
        }
        sensors.AddRange(coreClocks);
        CpuClockAggregates.Append("cpu", model, coreClocks, sensors);

        // Package power (AMD hwmon `power1_average` or Intel RAPL `energy_uj` delta)
        var pkgPower = ReadCpuPackagePower();
        if (pkgPower.HasValue)
        {
            sensors.Add(MakeSensor("cpu/package-power", "Package Power", "Power", pkgPower.Value, "W", model));
        }

        // Extra counters from /proc/stat tail (ctxt, intr, processes) - delta-based rates
        foreach (var extra in ReadCpuCounters())
        {
            sensors.Add(MakeSensor(extra.Id, extra.Name, extra.Type, extra.Value, extra.Units, model));
        }

        return sensors;
    }

    public float? GetCpuTjMaxC()
    {
        try
        {
            foreach (var hwmonDir in Directory.EnumerateDirectories("/sys/class/hwmon"))
            {
                var name = TryRead(Path.Combine(hwmonDir, "name"));
                if (name is not ("k10temp" or "coretemp" or "zenpower"))
                {
                    continue;
                }
                foreach (var suffix in new[] { "_crit", "_max" })
                {
                    foreach (var input in Directory.EnumerateFiles(hwmonDir, "temp*_input"))
                    {
                        var index = Path.GetFileName(input).Replace("temp", "").Replace("_input", "");
                        var milli = ParseFloat(TryRead(Path.Combine(hwmonDir, $"temp{index}{suffix}")));
                        if (milli > 0)
                        {
                            return milli / 1000f;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public (bool Healthy, float DistanceToTJMax) GetCpuHealth()
    {
        try
        {
            foreach (var hwmonDir in Directory.EnumerateDirectories("/sys/class/hwmon"))
            {
                var name = TryRead(Path.Combine(hwmonDir, "name"));
                if (name is not ("k10temp" or "coretemp" or "zenpower"))
                {
                    continue;
                }

                float? tctl = null;
                float? tjmax = null;
                foreach (var input in Directory.EnumerateFiles(hwmonDir, "temp*_input"))
                {
                    var index = Path.GetFileName(input).Replace("temp", "").Replace("_input", "");
                    var label = TryRead(Path.Combine(hwmonDir, $"temp{index}_label"));
                    var valueMilli = ParseFloat(TryRead(input));
                    if (valueMilli <= 0)
                    {
                        continue;
                    }
                    var valueC = valueMilli / 1000f;

                    if (label == "Tctl" || (tctl is null && label.Length == 0))
                    {
                        tctl = valueC;
                    }

                    var maxRaw = TryRead(Path.Combine(hwmonDir, $"temp{index}_max"));
                    var maxMilli = ParseFloat(maxRaw);
                    if (maxMilli > 0)
                    {
                        tjmax = maxMilli / 1000f;
                    }
                }

                if (tctl is float t && tjmax is float tj && tj > t)
                {
                    return (t < tj - 5f, tj - t);
                }
                if (tctl is float t2)
                {
                    return (t2 < 90f, 95f - t2);
                }
            }
        }
        catch { }
        return (true, 0f);
    }

    public IReadOnlyList<string> GetGpuModels()
    {
        if (_gpuModels is not null)
        {
            return _gpuModels;
        }

        var models = new List<string>();
        try
        {
            var csv = ShellOut("/usr/bin/nvidia-smi", 2000, "--query-gpu=name", "--format=csv,noheader");
            foreach (var line in csv.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0)
                {
                    models.Add(name);
                }
            }

            if (models.Count == 0)
            {
                // Fallback: parse lspci for VGA/3D controllers
                var lspci = ShellOut("/usr/bin/lspci", 2000, "-mm");
                foreach (var line in lspci.Split('\n'))
                {
                    if (line.Contains("\"VGA compatible controller\"") || line.Contains("\"3D controller\""))
                    {
                        var parts = line.Split('"');
                        if (parts.Length >= 6)
                        {
                            models.Add($"{parts[3]} {parts[5]}".Trim());
                        }
                    }
                }
            }
        }
        catch { }

        _gpuModels = models;
        return _gpuModels;
    }

    public IReadOnlyList<HardwareSensor> GetGpuSensors() => GetGpus().SelectMany(g => g.Sensors).ToList();

    public IReadOnlyList<GpuReadout> GetGpus()
    {
        var gpus = new List<GpuReadout>();
        var csv = GetNvidiaSmiCsv();

        int gpuIndex = 0;
        foreach (var line in csv.Split('\n'))
        {
            var row = line.Trim();
            if (row.Length == 0)
            {
                continue;
            }

            var cols = row.Split(',', StringSplitOptions.TrimEntries);
            if (cols.Length < 10)
            {
                continue;
            }

            var sensors = new List<HardwareSensor>();
            var name = cols[0];
            var tempGpu = ParseFloat(cols[1]);
            var tempMem = ParseFloat(cols[2]);
            var utilGpu = ParseFloat(cols[3]);
            var utilMem = ParseFloat(cols[4]);
            var memUsed = ParseFloat(cols[5]);
            var memTotal = ParseFloat(cols[6]);
            var memFree = ParseFloat(cols[7]);
            var power = ParseFloat(cols[8]);
            var powerLimit = ParseFloat(cols[9]);
            var fan = cols.Length > 10 ? ParseFloat(cols[10]) : 0;
            var coreClk = cols.Length > 11 ? ParseFloat(cols[11]) : 0;
            var memClk = cols.Length > 12 ? ParseFloat(cols[12]) : 0;
            var smClk = cols.Length > 13 ? ParseFloat(cols[13]) : 0;
            var pstate = cols.Length > 14 ? cols[14] : "";
            var encSessions = cols.Length > 15 ? ParseFloat(cols[15]) : 0;

            if (tempGpu > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/temp", "GPU Temperature", "Temperature", tempGpu, "°C", name));
            if (tempMem > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-temp", "GPU Memory Temp", "Temperature", tempMem, "°C", name));
            if (utilGpu > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/load", "GPU Core", "Load", utilGpu, "%", name));
            if (utilMem > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-util", "GPU Memory Util", "Load", utilMem, "%", name));
            if (memTotal > 0)
            {
                // TheoreticalMaximum = total VRAM (GB), so VRAM Used/Free can scale a
                // proportional gauge without the client also reading memory-total.
                float vramTotalGb = memTotal / 1024f;
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-used", "GPU Memory Used", "SmallData", memUsed / 1024f, "GB", name, theoreticalMax: vramTotalGb));
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-total", "GPU Memory", "SmallData", vramTotalGb, "GB", name));
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-free", "GPU Memory Free", "SmallData", memFree / 1024f, "GB", name, theoreticalMax: vramTotalGb));
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-load", "GPU Memory Load", "Load", memUsed * 100f / memTotal, "%", name));
            }
            if (power > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/power", "GPU Power", "Power", power, "W", name));
            if (powerLimit > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/power-limit", "GPU Power Limit", "Power", powerLimit, "W", name));
            if (fan >= 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/fan", "GPU Fan", "Load", fan, "%", name));
            if (coreClk > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/core-clock", "GPU Core Clock", "Clock", coreClk, "MHz", name));
            if (memClk > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/memory-clock", "GPU Memory Clock", "Clock", memClk, "MHz", name));
            if (smClk > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/sm-clock", "GPU SM Clock", "Clock", smClk, "MHz", name));
            if (encSessions > 0)
                sensors.Add(MakeSensor($"gpu/{gpuIndex}/encoder-sessions", "Encoder Sessions", "Factor", encSessions, "", name));

            gpus.Add(new GpuReadout
            {
                Id = $"gpu/{gpuIndex}",
                Name = name,
                Vendor = "nvidia",
                Integrated = false,
                Sensors = sensors,
            });
            gpuIndex++;
        }

        // No nvidia-smi telemetry: try amdgpu sysfs (no vendor CLI tool the
        // way NVIDIA has nvidia-smi) before falling back to a name-only row.
        if (gpus.Count == 0)
        {
            gpus.AddRange(ReadAmdGpus());

            // amdgpu cards found, but the box may also carry a non-AMD
            // controller with no telemetry source (driverless NVIDIA, Intel
            // iGPU): keep listing those as name-only rows so they do not
            // vanish from the GPU list.
            if (gpus.Count > 0)
            {
                var models = GetGpuModels();
                var nextIndex = gpus.Count;
                for (int i = 0; i < models.Count; i++)
                {
                    var (vendor, integrated) = GpuClassifier.FromName(models[i]);
                    if (vendor == "amd")
                        continue;
                    gpus.Add(new GpuReadout
                    {
                        Id = $"gpu/{nextIndex}",
                        Name = models[i],
                        Vendor = vendor,
                        Integrated = integrated,
                        Sensors = new List<HardwareSensor>(),
                    });
                    nextIndex++;
                }
            }
        }

        // Still nothing (Intel GPU, unsupported AMD driver, or no driver at
        // all): surface the controllers from the cached lspci/name list so
        // the GPU still appears, with a name-based vendor guess and no live
        // sensors.
        if (gpus.Count == 0)
        {
            var models = GetGpuModels();
            for (int i = 0; i < models.Count; i++)
            {
                var (vendor, integrated) = GpuClassifier.FromName(models[i]);
                gpus.Add(new GpuReadout
                {
                    Id = $"gpu/{i}",
                    Name = models[i],
                    Vendor = vendor,
                    Integrated = integrated,
                    Sensors = new List<HardwareSensor>(),
                });
            }
        }

        return gpus;
    }

    private IEnumerable<GpuReadout> ReadAmdGpus()
    {
#if LINUX
        // Pair enumerated cards with the AMD entries of the lspci name list
        // by ordinal - a heuristic (drm and lspci order can differ) but it
        // keeps a dual-AMD box (iGPU + dGPU) from rendering two cards under
        // one shared name and one shared integrated flag.
        var amdNames = new List<string>();
        foreach (var candidate in GetGpuModels())
        {
            if (GpuClassifier.FromName(candidate).Vendor == "amd")
                amdNames.Add(candidate);
        }

        var gpuIndex = 0;
        foreach (var deviceDir in AmdGpuSysfs.EnumerateAmdCardDirs())
        {
            var name = gpuIndex < amdNames.Count ? amdNames[gpuIndex] : "AMD GPU";
            var integrated = gpuIndex < amdNames.Count && GpuClassifier.FromName(name).Integrated;
            var reading = AmdGpuSysfs.ReadCard(deviceDir);
            yield return AmdGpuSysfs.BuildReadout(gpuIndex, name, "amd", integrated, reading);
            gpuIndex++;
        }
#else
        yield break;
#endif
    }

    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        var sensors = new List<HardwareSensor>();
        try
        {
            long total = 0;
            long available = 0;
            long buffers = 0;
            long cached = 0;
            foreach (var raw in File.ReadAllLines("/proc/meminfo"))
            {
                if (raw.StartsWith("MemTotal:", StringComparison.Ordinal))
                    total = ParseMemLineKb(raw);
                else if (raw.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    available = ParseMemLineKb(raw);
                else if (raw.StartsWith("Buffers:", StringComparison.Ordinal))
                    buffers = ParseMemLineKb(raw);
                else if (raw.StartsWith("Cached:", StringComparison.Ordinal))
                    cached = ParseMemLineKb(raw);
            }

            if (total <= 0)
            {
                return sensors;
            }

            long used = total - available;
            float totalGb = total / 1024f / 1024f;
            float usedGb = used / 1024f / 1024f;
            float freeGb = available / 1024f / 1024f;
            float buffersGb = buffers / 1024f / 1024f;
            float cachedGb = cached / 1024f / 1024f;
            float usagePct = total > 0 ? (float)(used * 100.0 / total) : 0f;

            // TheoreticalMaximum = installed RAM (GB) so the client can scale a
            // "X / Y GB" chart without a separate /system/memory/total fetch.
            sensors.Add(MakeSensor("mem/used", "Memory Used", "Data", usedGb, "GB", "Memory", theoreticalMax: totalGb));
            sensors.Add(MakeSensor("mem/available", "Memory Available", "Data", freeGb, "GB", "Memory", theoreticalMax: totalGb));
            sensors.Add(MakeSensor("mem/usage", "Memory Usage", "Load", usagePct, "%", "Memory", theoreticalMax: 100f));
            if (buffersGb > 0)
                sensors.Add(MakeSensor("mem/buffers", "Buffers", "Data", buffersGb, "GB", "Memory"));
            if (cachedGb > 0)
                sensors.Add(MakeSensor("mem/cached", "Cached", "Data", cachedGb, "GB", "Memory"));

            foreach (var extra in ReadExtraMemory())
            {
                sensors.Add(MakeSensor(extra.Id, extra.Name, extra.Type, extra.Value, extra.Units, "Memory"));
            }
        }
        catch { }
        return sensors;
    }

    public string GetMemoryTotalFormatted()
    {
        if (_memTotal is not null)
        {
            return _memTotal;
        }
        var bytes = GetMemTotalBytes();
        _memTotal = bytes > 0
            ? $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
            : "";
        return _memTotal;
    }

    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true)
    {
        var result = new Dictionary<string, StorageComponent>();
        foreach (var di in GetRealDrives())
        {
            var totalGb = di.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = di.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usedGb = totalGb - freeGb;
            var usePct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;
            var label = di.Name.TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(label))
            {
                label = "/";
            }

            result[label] = new StorageComponent
            {
                Id = label,
                Name = di.VolumeLabel.Length > 0 ? di.VolumeLabel : label,
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
        return result;
    }

    public IReadOnlyList<string> GetStoragePartitions()
    {
        var list = new List<string>();
        foreach (var d in GetRealDrives())
        {
            list.Add(d.Name);
        }
        return list;
    }

    public IReadOnlyList<StorageDriveInfo> GetStorageInfo()
    {
        var list = new List<StorageDriveInfo>();
        foreach (var d in GetRealDrives())
        {
            var totalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0);
            list.Add(new StorageDriveInfo
            {
                Name = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.Name,
                Partition = d.Name,
                Capacity = FormatGb(totalGb),
            });
        }
        return list;
    }

    public IReadOnlyList<HardwareSensor> GetMotherboardSensors()
    {
        var sensors = new List<HardwareSensor>();
        var model = GetMotherboardModel();
        if (!string.IsNullOrEmpty(model))
        {
            sensors.Add(MakeSensor("mobo/model", "Model Identifier", "Factor", 0, "", model));
        }

        foreach (var (name, idFrag, rpm) in EnumerateHwmonFans())
        {
            sensors.Add(MakeSensor($"mobo/fan/{idFrag}", name, "Fan", rpm, "RPM", model));
        }

        foreach (var (name, idFrag, millivolts) in EnumerateHwmonVoltages())
        {
            sensors.Add(MakeSensor($"mobo/voltage/{idFrag}", name, "Voltage", millivolts / 1000f, "V", model));
        }

        // Non-CPU/non-GPU temps from hwmon (e.g. nct6687 VRM temps, chipset temps).
        foreach (var (name, idFrag, tempC) in EnumerateHwmonTemps(excludedNames: new[] { "k10temp", "coretemp", "zenpower", "nvme", "amdgpu" }))
        {
            sensors.Add(MakeSensor($"mobo/temp/{idFrag}", name, "Temperature", tempC, "°C", model));
        }

        // Per-NIC rx/tx rates (B/s) - only emit for interfaces with activity.
        foreach (var (iface, rx, tx) in ReadNetRates())
        {
            if (rx <= 0 && tx <= 0)
                continue;
            sensors.Add(MakeSensor($"mobo/nic/{Sanitize(iface)}/rx", $"{iface} RX", "Throughput", rx, "B/s", iface));
            sensors.Add(MakeSensor($"mobo/nic/{Sanitize(iface)}/tx", $"{iface} TX", "Throughput", tx, "B/s", iface));
        }

        // Load average + uptime
        var load = ReadLoadAverage();
        if (load is not null)
        {
            sensors.Add(MakeSensor("mobo/loadavg/1", "Load Avg 1m", "Factor", load.Value.m1, "", model));
            sensors.Add(MakeSensor("mobo/loadavg/5", "Load Avg 5m", "Factor", load.Value.m5, "", model));
            sensors.Add(MakeSensor("mobo/loadavg/15", "Load Avg 15m", "Factor", load.Value.m15, "", model));
        }

        var uptimeSec = ReadUptimeSeconds();
        if (uptimeSec > 0)
        {
            sensors.Add(MakeSensor("mobo/uptime", "System Uptime", "TimeSpan", uptimeSec, "s", model));
        }

        return sensors;
    }

    public string GetMotherboardModel()
    {
        if (_moboModel is not null)
        {
            return _moboModel;
        }

        var vendor = TryRead("/sys/class/dmi/id/board_vendor");
        var name = TryRead("/sys/class/dmi/id/board_name");
        if (string.IsNullOrEmpty(vendor) && string.IsNullOrEmpty(name))
        {
            _moboModel = "";
            return _moboModel;
        }
        _moboModel = $"{vendor} {name}".Trim();
        return _moboModel;
    }

    public string GetRamBrandModel()
    {
        if (_ramBrandModel is not null)
            return _ramBrandModel;
        // /sys/class/dmi populates part-number + vendor for DIMMs only when the
        // kernel parsed the SMBIOS table AND the BIOS wrote sane SPD data. Most
        // desktops do; laptops sometimes don't. Fall back to dmidecode when
        // available (root-only typically), then to an empty string.
        var mfg = TryRead("/sys/class/dmi/id/product_vendor");
        // No reliable sysfs path for per-DIMM info; try the memory subsystem
        // sysfs used by some distros, then dmidecode.
        var parts = new List<string>();
        var vendor = TryDmi("memory-module-manufacturer");
        var partNum = TryDmi("memory-module-part-number");
        if (!string.IsNullOrEmpty(vendor))
            parts.Add(vendor);
        if (!string.IsNullOrEmpty(partNum))
            parts.Add(partNum);
        _ramBrandModel = parts.Count > 0 ? string.Join(" ", parts) : "";
        return _ramBrandModel;
    }

    public string GetStorageBrandModel()
    {
        if (_storageBrandModel is not null)
            return _storageBrandModel;
        // NVMe drives expose model via /sys/block/nvme0n1/device/model; SATA /
        // SCSI drives expose it via /sys/block/sdX/device/{vendor,model}.
        // Pick the first non-removable block device and read whatever fields
        // are populated.
        try
        {
            foreach (var block in Directory.GetDirectories("/sys/block/"))
            {
                var bname = Path.GetFileName(block);
                if (bname.StartsWith("loop") || bname.StartsWith("ram") || bname.StartsWith("dm-"))
                    continue;
                var removable = TryRead(Path.Combine(block, "removable")).Trim();
                if (removable == "1")
                    continue;
                var devDir = Path.Combine(block, "device");
                if (!Directory.Exists(devDir))
                    continue;
                var vendor = TryRead(Path.Combine(devDir, "vendor")).Trim();
                var model = TryRead(Path.Combine(devDir, "model")).Trim();
                if (string.IsNullOrEmpty(vendor) && string.IsNullOrEmpty(model))
                    continue;
                _storageBrandModel = string.IsNullOrEmpty(vendor)
                    ? model
                    : (model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? model : $"{vendor} {model}");
                return _storageBrandModel;
            }
        }
        catch { /* swallow and fall through to empty */ }
        _storageBrandModel = "";
        return _storageBrandModel;
    }

    /// <summary>Wrap dmidecode so a missing binary / lack of privileges returns "".</summary>
    private static string TryDmi(string stringArg)
        => ShellExecutor.Run("/usr/sbin/dmidecode", 2000, "-s", stringArg).Trim();

    public SensorExtras GetSensorExtras()
    {
        // Surfaces battery state from /sys/class/power_supply/BAT* (laptop main
        // battery / UPS) and per-NIC throughput from /proc/net/dev. TODO: PSU
        // / Cooler / NVMe / EC; LHM is Windows-only and the equivalent sysfs
        // reads need their own walkers.
        var extras = new SensorExtras();
        extras.Batteries.AddRange(BuildLinuxBatteries());
        extras.Nics.AddRange(BuildLinuxNics());
        return extras;
    }

    // Independent of _prevNetStats (ReadNetRates' baseline for mobo/nic/*) so
    // this and GetMotherboardSensors never perturb each other's rate window.
    // Guarded by _nicExtrasLock: GetSensorExtras is reached concurrently by
    // the monitoring broadcaster and the deck category resolver.
    private readonly Dictionary<string, (ulong Rx, ulong Tx, DateTime At)> _prevNicExtrasStats = new();
    private readonly object _nicExtrasLock = new();

    private IEnumerable<HardwareComponent> BuildLinuxNics()
    {
        var now = DateTime.UtcNow;
        var results = new List<HardwareComponent>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/net/dev");
        }
        catch
        {
            return results;
        }

        lock (_nicExtrasLock)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Skip the two header lines before the per-interface counter rows.
            for (var i = 2; i < lines.Length; i++)
            {
                var line = lines[i];
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0)
                    continue;
                var iface = line.AsSpan(0, colonIdx).Trim().ToString();
                if (iface == "lo")
                    continue;

                var parts = line.AsSpan(colonIdx + 1).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10 || !ulong.TryParse(parts[0], out var rxBytes) || !ulong.TryParse(parts[8], out var txBytes))
                    continue;
                seen.Add(iface);

                var sensors = new List<HardwareSensor>();
                // Counter reset (driver reload, interface recreated) makes the
                // new value smaller than the baseline; skip the rate for that
                // tick instead of shipping a wrapped ulong delta.
                if (_prevNicExtrasStats.TryGetValue(iface, out var prev))
                {
                    var dtSec = (now - prev.At).TotalSeconds;
                    if (dtSec > 0 && rxBytes >= prev.Rx && txBytes >= prev.Tx)
                    {
                        sensors.Add(MakeSensor($"nic/{Sanitize(iface)}/rx", $"{iface} RX", "Throughput", (float)((rxBytes - prev.Rx) / dtSec), "B/s", iface));
                        sensors.Add(MakeSensor($"nic/{Sanitize(iface)}/tx", $"{iface} TX", "Throughput", (float)((txBytes - prev.Tx) / dtSec), "B/s", iface));
                    }
                }
                _prevNicExtrasStats[iface] = (rxBytes, txBytes, now);

                var speedMbps = ReadNicLinkSpeedMbps(iface);
                if (speedMbps > 0)
                {
                    sensors.Add(MakeSensor($"nic/{Sanitize(iface)}/speed", "Link Speed", "Factor", speedMbps, "Mbps", iface));
                }

                if (sensors.Count == 0)
                    continue;

                results.Add(new HardwareComponent
                {
                    Id = $"nic/{Sanitize(iface)}",
                    Name = iface,
                    Sensors = sensors,
                });
            }

            // Drop baselines for vanished interfaces (veth/docker churn) so the
            // dictionary does not grow for the process lifetime.
            if (_prevNicExtrasStats.Count > seen.Count)
            {
                var stale = new List<string>();
                foreach (var key in _prevNicExtrasStats.Keys)
                {
                    if (!seen.Contains(key))
                        stale.Add(key);
                }
                foreach (var key in stale)
                    _prevNicExtrasStats.Remove(key);
            }
        }
        return results;
    }

    private static int ReadNicLinkSpeedMbps(string iface)
        => int.TryParse(TryRead($"/sys/class/net/{iface}/speed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps) && mbps > 0
            ? mbps
            : 0;

    private static IEnumerable<HardwareComponent> BuildLinuxBatteries()
    {
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var type = SafeRead(Path.Combine(dir, "type"));
            if (!string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(dir);
            var manufacturer = SafeRead(Path.Combine(dir, "manufacturer"));
            var model = SafeRead(Path.Combine(dir, "model_name"));
            var displayName = string.IsNullOrEmpty(model)
                ? name
                : $"{(string.IsNullOrEmpty(manufacturer) ? "" : manufacturer + " ")}{model}";

            var sensors = new List<HardwareSensor>();

            if (TryParseInt(SafeRead(Path.Combine(dir, "capacity")), out var pct))
                sensors.Add(MakeSensor($"battery/{name}/charge", "Charge Level", "Level", pct, "%", displayName));

            var status = SafeRead(Path.Combine(dir, "status"));
            if (!string.IsNullOrEmpty(status))
            {
                sensors.Add(new HardwareSensor
                {
                    Id = $"battery/{name}/status",
                    Name = "Status",
                    Type = "Text",
                    Value = 0f,
                    Units = "",
                    Formatted = status,
                    Parent = new SensorParent { Id = $"battery/{name}", Name = displayName },
                });
            }

            // power_now is reported in microwatts; convert to W.
            if (TryParseInt(SafeRead(Path.Combine(dir, "power_now")), out var powerUw) && powerUw > 0)
                sensors.Add(MakeSensor($"battery/{name}/power", "Charge/Discharge Rate", "Power", powerUw / 1_000_000f, "W", displayName));

            // voltage_now in microvolts.
            if (TryParseInt(SafeRead(Path.Combine(dir, "voltage_now")), out var voltUv) && voltUv > 0)
                sensors.Add(MakeSensor($"battery/{name}/voltage", "Voltage", "Voltage", voltUv / 1_000_000f, "V", displayName));

            // energy_full_design vs energy_full → wear level.
            if (TryParseInt(SafeRead(Path.Combine(dir, "energy_full")), out var efull) && efull > 0
                && TryParseInt(SafeRead(Path.Combine(dir, "energy_full_design")), out var edesign) && edesign > 0)
            {
                var health = (float)efull / edesign * 100f;
                sensors.Add(MakeSensor($"battery/{name}/health", "Capacity Health", "Level", health, "%", displayName));
            }

            if (TryParseInt(SafeRead(Path.Combine(dir, "cycle_count")), out var cycles) && cycles > 0)
            {
                sensors.Add(new HardwareSensor
                {
                    Id = $"battery/{name}/cycles",
                    Name = "Cycle Count",
                    Type = "Factor",
                    Value = cycles,
                    Units = "",
                    Formatted = cycles.ToString(CultureInfo.InvariantCulture),
                    Parent = new SensorParent { Id = $"battery/{name}", Name = displayName },
                });
            }

            if (sensors.Count == 0)
                continue;

            yield return new HardwareComponent
            {
                Id = $"battery/{name}",
                Name = displayName,
                Sensors = sensors,
            };
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool TryParseInt(string text, out int value)
    {
        if (string.IsNullOrEmpty(text))
        {
            value = 0;
            return false;
        }
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public string GetOsVersion() => RuntimeInformation.OSDescription;

    public void SetPollingRate(int pollingRate) { }

    private double? ReadCpuLoad()
    {
        try
        {
            using var r = new StreamReader("/proc/stat");
            var line = r.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong user = ParseUlong(parts, 1);
            ulong nice = ParseUlong(parts, 2);
            ulong system = ParseUlong(parts, 3);
            ulong idle = ParseUlong(parts, 4);
            ulong iowait = parts.Length > 5 ? ParseUlong(parts, 5) : 0;
            ulong irq = parts.Length > 6 ? ParseUlong(parts, 6) : 0;
            ulong softirq = parts.Length > 7 ? ParseUlong(parts, 7) : 0;
            ulong steal = parts.Length > 8 ? ParseUlong(parts, 8) : 0;

            ulong idleAll = idle + iowait;
            ulong total = user + nice + system + idleAll + irq + softirq + steal;

            double? result = null;
            if (_hasPrev)
            {
                ulong dt = total - _prevTotal;
                ulong di = idleAll - _prevIdle;
                if (dt > 0)
                {
                    result = Math.Clamp((dt - di) * 100.0 / dt, 0, 100);
                }
            }
            _prevIdle = idleAll;
            _prevTotal = total;
            _hasPrev = true;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureCoreCounts()
    {
        if (_cpuLogicalCores is not null)
        {
            return;
        }

        int logical = 0;
        var physicalIds = new HashSet<string>();
        var coreIds = new HashSet<string>();
        try
        {
            string? currentPhys = null;
            string? currentCore = null;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("processor", StringComparison.Ordinal))
                {
                    logical++;
                }
                else if (line.StartsWith("physical id", StringComparison.Ordinal))
                {
                    currentPhys = line[(line.IndexOf(':') + 1)..].Trim();
                    physicalIds.Add(currentPhys);
                }
                else if (line.StartsWith("core id", StringComparison.Ordinal))
                {
                    currentCore = line[(line.IndexOf(':') + 1)..].Trim();
                    if (currentPhys is not null)
                    {
                        coreIds.Add($"{currentPhys}:{currentCore}");
                    }
                }
                else if (line.Length == 0)
                {
                    currentPhys = null;
                    currentCore = null;
                }
            }
        }
        catch { }

        _cpuLogicalCores = logical;
        _cpuPhysicalCores = coreIds.Count > 0 ? coreIds.Count : logical;
    }

    private static int ReadCpuMaxFreqMhz()
    {
        try
        {
            var raw = TryRead("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kHz))
            {
                return kHz / 1000;
            }
        }
        catch { }
        return 0;
    }

    private long GetMemTotalBytes()
    {
        if (_memTotalBytes >= 0)
        {
            return _memTotalBytes;
        }
        _memTotalBytes = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    _memTotalBytes = ParseMemLineKb(line) * 1024;
                    break;
                }
            }
        }
        catch { }
        return _memTotalBytes;
    }

    private static IEnumerable<(string Name, string IdFrag, float TempC)> EnumerateHwmonTemps(
        string[]? preferredNames = null,
        string[]? excludedNames = null)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories("/sys/class/hwmon");
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            string hwmonName;
            try
            {
                hwmonName = TryRead(Path.Combine(dir, "name"));
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(hwmonName))
            {
                continue;
            }
            if (preferredNames is not null && Array.IndexOf(preferredNames, hwmonName) < 0)
            {
                continue;
            }
            if (excludedNames is not null && Array.IndexOf(excludedNames, hwmonName) >= 0)
            {
                continue;
            }

            string[] inputs;
            try
            {
                inputs = Directory.GetFiles(dir, "temp*_input");
            }
            catch
            {
                continue;
            }

            foreach (var input in inputs)
            {
                var milliStr = TryRead(input);
                if (!float.TryParse(milliStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var milli))
                {
                    continue;
                }
                if (milli <= 0)
                {
                    continue;
                }

                var fileName = Path.GetFileName(input);
                var index = fileName.Replace("temp", "").Replace("_input", "");
                var label = TryRead(Path.Combine(dir, $"temp{index}_label"));
                var displayName = label.Length > 0
                    ? $"{hwmonName} {label}"
                    : $"{hwmonName} temp{index}";

                // IdFrag = stable chip key + index so two chips sharing a `name`
                // (e.g. dual nct6798) don't collapse to the same sensor id.
                yield return (displayName, $"{LinuxSysfs.ChipKey(dir, hwmonName)}-temp{index}", milli / 1000f);
            }
        }
    }

    private static IEnumerable<(string Name, string IdFrag, float Rpm)> EnumerateHwmonFans()
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories("/sys/class/hwmon");
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var hwmonName = TryRead(Path.Combine(dir, "name"));
            if (string.IsNullOrEmpty(hwmonName))
            {
                continue;
            }

            string[] inputs;
            try
            {
                inputs = Directory.GetFiles(dir, "fan*_input");
            }
            catch
            {
                continue;
            }

            foreach (var input in inputs)
            {
                if (!float.TryParse(TryRead(input), NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm))
                {
                    continue;
                }
                if (rpm <= 0)
                {
                    continue;
                }
                var fileName = Path.GetFileName(input);
                var index = fileName.Replace("fan", "").Replace("_input", "");
                var label = TryRead(Path.Combine(dir, $"fan{index}_label"));
                var displayName = label.Length > 0
                    ? $"{hwmonName} {label}"
                    : $"{hwmonName} fan{index}";
                yield return (displayName, $"{LinuxSysfs.ChipKey(dir, hwmonName)}-fan{index}", rpm);
            }
        }
    }

    private static IEnumerable<(string Name, string IdFrag, float MilliVolts)> EnumerateHwmonVoltages()
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories("/sys/class/hwmon");
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var hwmonName = TryRead(Path.Combine(dir, "name"));
            if (string.IsNullOrEmpty(hwmonName))
            {
                continue;
            }

            string[] inputs;
            try
            {
                inputs = Directory.GetFiles(dir, "in*_input");
            }
            catch
            {
                continue;
            }

            foreach (var input in inputs)
            {
                if (!float.TryParse(TryRead(input), NumberStyles.Float, CultureInfo.InvariantCulture, out var mv))
                {
                    continue;
                }
                if (mv <= 0)
                {
                    continue;
                }
                var fileName = Path.GetFileName(input);
                var index = fileName.Replace("in", "").Replace("_input", "");
                var label = TryRead(Path.Combine(dir, $"in{index}_label"));
                var displayName = label.Length > 0
                    ? $"{hwmonName} {label}"
                    : $"{hwmonName} in{index}";
                yield return (displayName, $"{LinuxSysfs.ChipKey(dir, hwmonName)}-in{index}", mv);
            }
        }
    }

    private static List<DriveInfo> GetRealDrives()
    {
        var drives = new List<DriveInfo>();
        foreach (var di in DriveInfo.GetDrives())
        {
            if (!di.IsReady)
            {
                continue;
            }
            if (di.DriveType != DriveType.Fixed)
            {
                continue;
            }
            if (di.TotalSize < 1L * 1024 * 1024 * 1024)
            {
                continue;
            }
            var name = di.Name;
            // Skip pseudo / virtual / overlay mounts typical on Linux
            if (name.StartsWith("/proc", StringComparison.Ordinal)
                || name.StartsWith("/sys", StringComparison.Ordinal)
                || name.StartsWith("/run", StringComparison.Ordinal)
                || name.StartsWith("/dev", StringComparison.Ordinal)
                || name.StartsWith("/var/lib/docker/", StringComparison.Ordinal)
                || name.StartsWith("/var/lib/containers/", StringComparison.Ordinal))
            {
                continue;
            }
            var fmt = di.DriveFormat;
            if (fmt is "tmpfs" or "devtmpfs" or "overlay" or "squashfs" or "ramfs" or "proc" or "sysfs" or "cgroup" or "cgroup2" or "autofs")
            {
                continue;
            }
            drives.Add(di);
        }
        return drives;
    }

    private string GetNvidiaSmiCsv()
    {
        if (DateTime.UtcNow - _gpuSmiFetchedAt < GpuCacheTtl && _gpuSmiCsv.Length > 0)
        {
            return _gpuSmiCsv;
        }

        _gpuSmiCsv = ShellOut(
            "/usr/bin/nvidia-smi",
            2000,
            "--query-gpu=name,temperature.gpu,temperature.memory,utilization.gpu,utilization.memory,memory.used,memory.total,memory.free,power.draw,power.limit,fan.speed,clocks.current.graphics,clocks.current.memory,clocks.current.sm,pstate,encoder.stats.sessionCount",
            "--format=csv,noheader,nounits");
        _gpuSmiFetchedAt = DateTime.UtcNow;
        return _gpuSmiCsv;
    }

    // Internal (not private): AmdGpuSysfs.BuildReadout reuses this so its
    // sensor formatting matches the rest of this provider exactly.
    internal static HardwareSensor MakeSensor(string id, string name, string type, float value, string units, string parentName, float theoreticalMax = 0f)
    {
        var formatted = type switch
        {
            "Load" or "Level" => $"{value:F1}{units}",
            "Data" or "SmallData" => $"{value:F2} {units}",
            "Clock" or "Frequency" => $"{value:F0} {units}",
            "Temperature" => $"{value:F1} {units}",
            "Fan" => $"{value:F0} {units}",
            "Voltage" => $"{value:F3} {units}",
            "Power" => $"{value:F1} {units}",
            "Throughput" => FormatThroughput(value),
            "TimeSpan" => FormatTimeSpan(value),
            "Factor" => $"{value:F0}{(units.Length > 0 ? " " + units : "")}",
            _ => value > 0 ? $"{value:F0}" : "",
        };

        return new HardwareSensor
        {
            Id = id,
            Name = name,
            Type = type,
            Value = value,
            Units = units,
            TheoreticalMaximum = theoreticalMax,
            Formatted = formatted,
            FormattedMax = "",
            FormattedMin = "",
            FormattedAverage = "",
            FormattedUsage = "",
            Parent = new SensorParent { Id = id.Split('/')[0], Name = parentName },
        };
    }

    private static string FormatGb(double gb) => gb >= 1000 ? $"{gb / 1024.0:F2} TB" : $"{gb:F2} GB";

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

    private static string Sanitize(string input)
    {
        var chars = new char[input.Length];
        int j = 0;
        foreach (var c in input)
        {
            chars[j++] = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-';
        }
        return new string(chars).Replace("--", "-").TrimEnd('-');
    }

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static float ParseFloat(string raw)
    {
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    private static long ParseMemLineKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }
        return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : 0;
    }

    private static ulong ParseUlong(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }
        return ulong.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string ShellOut(string fileName, int timeoutMs, params string[] args)
        => ShellExecutor.Run(fileName, timeoutMs, args);

    private IEnumerable<(int Index, float Percent)> ReadPerCoreLoad()
    {
        List<(int, float)> results = new();
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal) || line.Length < 4)
                {
                    continue;
                }
                if (!char.IsDigit(line[3]))
                {
                    continue; // skip the aggregate "cpu " line (no trailing digit)
                }
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    continue;
                }
                if (!int.TryParse(parts[0].AsSpan(3), out var index))
                {
                    continue;
                }
                ulong user = ParseUlong(parts, 1);
                ulong nice = ParseUlong(parts, 2);
                ulong sys = ParseUlong(parts, 3);
                ulong idle = ParseUlong(parts, 4);
                ulong iowait = parts.Length > 5 ? ParseUlong(parts, 5) : 0;
                ulong irq = parts.Length > 6 ? ParseUlong(parts, 6) : 0;
                ulong softirq = parts.Length > 7 ? ParseUlong(parts, 7) : 0;
                ulong steal = parts.Length > 8 ? ParseUlong(parts, 8) : 0;

                ulong idleAll = idle + iowait;
                ulong total = user + nice + sys + idleAll + irq + softirq + steal;

                float pct = 0f;
                if (_prevCoreTicks.TryGetValue(index, out var prev))
                {
                    ulong dt = total - prev.Total;
                    ulong di = idleAll - prev.Idle;
                    if (dt > 0)
                    {
                        pct = (float)Math.Clamp((dt - di) * 100.0 / dt, 0, 100);
                    }
                }
                _prevCoreTicks[index] = (idleAll, total);
                if (_prevCoreTicks.Count > 1 || index == 0)
                {
                    results.Add((index, pct));
                }
            }
        }
        catch { }
        results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return results;
    }

    private static IEnumerable<(int Index, float Mhz)> ReadPerCoreClock()
    {
        List<(int, float)> results = new();
        try
        {
            var dirs = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*");
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 4 || !char.IsDigit(name[3]))
                {
                    continue;
                }
                if (!int.TryParse(name.AsSpan(3), out var index))
                {
                    continue;
                }
                var path = Path.Combine(dir, "cpufreq", "scaling_cur_freq");
                if (!File.Exists(path))
                {
                    continue;
                }
                if (!int.TryParse(TryRead(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kHz))
                {
                    continue;
                }
                results.Add((index, kHz / 1000f));
            }
        }
        catch { }
        results.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return results;
    }

    private float? ReadCpuPackagePower()
    {
        // AMD: hwmon power*_average (µW) on k10temp or zenpower
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/hwmon"))
            {
                var hwmonName = TryRead(Path.Combine(dir, "name"));
                if (hwmonName is not ("k10temp" or "zenpower" or "zenpower3"))
                {
                    continue;
                }
                foreach (var input in Directory.GetFiles(dir, "power*_average"))
                {
                    if (long.TryParse(TryRead(input), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uW) && uW > 0)
                    {
                        return uW / 1_000_000f;
                    }
                }
            }
        }
        catch { }

        // Intel: RAPL energy counter delta (µJ / elapsed seconds)
        try
        {
            const string path = "/sys/class/powercap/intel-rapl:0/energy_uj";
            if (File.Exists(path) &&
                long.TryParse(TryRead(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var energyUj))
            {
                var now = DateTime.UtcNow;
                if (_prevRaplEnergyUj >= 0)
                {
                    var duj = energyUj - _prevRaplEnergyUj;
                    if (duj < 0)
                        duj = 0; // counter wrap
                    var dtSec = (now - _prevRaplTime).TotalSeconds;
                    _prevRaplEnergyUj = energyUj;
                    _prevRaplTime = now;
                    if (dtSec > 0)
                    {
                        return (float)(duj / 1_000_000.0 / dtSec);
                    }
                }
                _prevRaplEnergyUj = energyUj;
                _prevRaplTime = now;
            }
        }
        catch { }

        return null;
    }

    private IEnumerable<(string Id, string Name, string Type, float Value, string Units)> ReadCpuCounters()
    {
        var now = DateTime.UtcNow;
        ulong ctxt = 0;
        ulong intr = 0;
        ulong processes = 0;
        int runnable = 0;
        int blocked = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("ctxt ", StringComparison.Ordinal))
                {
                    _ = ulong.TryParse(line.AsSpan(5).Trim(), out ctxt);
                }
                else if (line.StartsWith("intr ", StringComparison.Ordinal))
                {
                    var sp = line.IndexOf(' ', 5);
                    if (sp > 0)
                        _ = ulong.TryParse(line.AsSpan(5, sp - 5), out intr);
                }
                else if (line.StartsWith("processes ", StringComparison.Ordinal))
                {
                    _ = ulong.TryParse(line.AsSpan(10).Trim(), out processes);
                }
                else if (line.StartsWith("procs_running ", StringComparison.Ordinal))
                {
                    _ = int.TryParse(line.AsSpan(14).Trim(), out runnable);
                }
                else if (line.StartsWith("procs_blocked ", StringComparison.Ordinal))
                {
                    _ = int.TryParse(line.AsSpan(14).Trim(), out blocked);
                }
            }
        }
        catch { yield break; }

        var dtSec = _prevStatTime == default ? 0 : (now - _prevStatTime).TotalSeconds;
        if (dtSec > 0)
        {
            if (ctxt > _prevCtxt)
                yield return ("cpu/ctx-switches", "Context Switches", "Factor", (float)((ctxt - _prevCtxt) / dtSec), "/s");
            if (intr > _prevIntr)
                yield return ("cpu/interrupts", "Interrupts", "Factor", (float)((intr - _prevIntr) / dtSec), "/s");
            if (processes > _prevProcesses)
                yield return ("cpu/forks", "Forks", "Factor", (float)((processes - _prevProcesses) / dtSec), "/s");
        }
        _prevCtxt = ctxt;
        _prevIntr = intr;
        _prevProcesses = processes;
        _prevStatTime = now;

        if (runnable > 0)
            yield return ("cpu/runnable", "Runnable", "Factor", runnable, "");
        if (blocked > 0)
            yield return ("cpu/blocked", "Blocked", "Factor", blocked, "");
    }

    private static IEnumerable<(string Id, string Name, string Type, float Value, string Units)> ReadExtraMemory()
    {
        long swapTotal = 0, swapFree = 0, dirty = 0, writeback = 0, pagetables = 0, kernelStack = 0, slab = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
                    swapTotal = ParseMemLineKb(line);
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
                    swapFree = ParseMemLineKb(line);
                else if (line.StartsWith("Dirty:", StringComparison.Ordinal))
                    dirty = ParseMemLineKb(line);
                else if (line.StartsWith("Writeback:", StringComparison.Ordinal))
                    writeback = ParseMemLineKb(line);
                else if (line.StartsWith("PageTables:", StringComparison.Ordinal))
                    pagetables = ParseMemLineKb(line);
                else if (line.StartsWith("KernelStack:", StringComparison.Ordinal))
                    kernelStack = ParseMemLineKb(line);
                else if (line.StartsWith("Slab:", StringComparison.Ordinal))
                    slab = ParseMemLineKb(line);
            }
        }
        catch { yield break; }

        if (swapTotal > 0)
        {
            var swapUsed = swapTotal - swapFree;
            var swapTotalGb = swapTotal / 1024f / 1024f;
            var swapUsedGb = swapUsed / 1024f / 1024f;
            var swapPct = swapTotal > 0 ? (swapUsed * 100f / swapTotal) : 0f;
            yield return ("mem/swap-total", "Swap Total", "Data", swapTotalGb, "GB");
            yield return ("mem/swap-used", "Swap Used", "Data", swapUsedGb, "GB");
            yield return ("mem/swap-usage", "Swap Usage", "Load", swapPct, "%");
        }
        if (dirty > 0)
            yield return ("mem/dirty", "Dirty", "SmallData", dirty / 1024f, "MB");
        if (writeback > 0)
            yield return ("mem/writeback", "Writeback", "SmallData", writeback / 1024f, "MB");
        if (pagetables > 0)
            yield return ("mem/pagetables", "Page Tables", "SmallData", pagetables / 1024f, "MB");
        if (kernelStack > 0)
            yield return ("mem/kernel-stack", "Kernel Stack", "SmallData", kernelStack / 1024f, "MB");
        if (slab > 0)
            yield return ("mem/slab", "Slab", "SmallData", slab / 1024f, "MB");
    }

    private IEnumerable<(string Iface, float Rx, float Tx)> ReadNetRates()
    {
        var now = DateTime.UtcNow;
        var results = new List<(string, float, float)>();
        try
        {
            var lines = File.ReadAllLines("/proc/net/dev");
            // Skip first two header lines.
            for (int i = 2; i < lines.Length; i++)
            {
                var line = lines[i];
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0)
                    continue;
                var iface = line.AsSpan(0, colonIdx).Trim().ToString();
                if (iface == "lo")
                    continue;
                var rest = line.AsSpan(colonIdx + 1);
                var parts = rest.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10)
                    continue;
                if (!ulong.TryParse(parts[0], out var rxBytes))
                    continue;
                if (!ulong.TryParse(parts[8], out var txBytes))
                    continue;

                if (_prevNetStats.TryGetValue(iface, out var prev))
                {
                    var dtSec = (now - prev.At).TotalSeconds;
                    if (dtSec > 0)
                    {
                        float rxRate = (float)((rxBytes - prev.Rx) / dtSec);
                        float txRate = (float)((txBytes - prev.Tx) / dtSec);
                        results.Add((iface, rxRate, txRate));
                    }
                }
                _prevNetStats[iface] = (rxBytes, txBytes, now);
            }
        }
        catch { }
        return results;
    }

    private static (float m1, float m5, float m15)? ReadLoadAverage()
    {
        try
        {
            var raw = TryRead("/proc/loadavg");
            if (raw.Length == 0)
                return null;
            var parts = raw.Split(' ');
            if (parts.Length < 3)
                return null;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m1))
                return null;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m5))
                return null;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var m15))
                return null;
            return (m1, m5, m15);
        }
        catch { return null; }
    }

    private static float ReadUptimeSeconds()
    {
        try
        {
            var raw = TryRead("/proc/uptime");
            if (raw.Length == 0)
                return 0;
            var sp = raw.IndexOf(' ');
            if (sp < 0)
                return 0;
            return float.TryParse(raw.AsSpan(0, sp), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }
}
