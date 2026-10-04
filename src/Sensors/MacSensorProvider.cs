using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors;

/// <summary>
/// macOS sensor provider. Every method returns well-formed data (never null,
/// never throws); CPU load and VM stats come from Mach <c>host_statistics</c>
/// syscalls via <see cref="Platform.Mac.MachStats"/>, CPU / GPU die and
/// internal-SSD temperatures from IOKit AppleSMC via
/// <see cref="Platform.Mac.MacSmc"/>, GPU utilization from IOAccelerator via
/// <see cref="Platform.Mac.MacGpuStats"/>, and the remaining metadata is
/// gathered from <c>sysctl</c>, <c>df</c>, and <c>system_profiler</c>.
/// Windows uses LibreHardwareSensorProvider and Linux uses
/// LinuxSensorProvider; this class is never constructed on those platforms
/// (see AddNexusSensors in NexusServiceCollectionExtensions).
/// </summary>
public sealed class MacSensorProvider : ISensorProvider, IDisposable
{
    private static readonly IReadOnlyList<HardwareSensor> EmptySensors = Array.Empty<HardwareSensor>();
    private static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();

    private readonly Platform.Mac.MacSmc? _smc;
    private readonly Func<string, float?>? _smcRead;

    public MacSensorProvider()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _smc = new Platform.Mac.MacSmc();
            _smcRead = _smc.ReadFloatKey;
        }
    }

    public void Dispose() => _smc?.Dispose();

    // Polled hardware readings move on a seconds scale while several
    // consumers (broadcaster, summary topic, history sampler, HTTP) poll this
    // provider independently, and each underlying read costs syscalls (a full
    // SMC key sweep is milliseconds) - serve values cached for one consumer
    // tick instead of re-reading per caller. The hardware tests derive their
    // expectations from these same methods, so they share the caches.
    internal const long PollCacheTtlMs = 1000;
    private readonly object _pollLock = new();
    private PolledValueCache _cpuDieTemp;
    private PolledValueCache _gpuDieTemp;
    private PolledValueCache _ssdTemp;
    private PolledValueCache _gpuUtilization;

    private struct PolledValueCache
    {
        public long ReadAtMs;
        public float? Value;
    }

    internal float? ReadCpuDieTemperature()
        => CachedPolledValue(ref _cpuDieTemp, static self =>
            self._smcRead is null ? null : Platform.Mac.MacSmcTemperatures.Average(self._smcRead, Platform.Mac.MacSmcTemperatures.CpuKeys));

    internal float? ReadGpuDieTemperature()
        => CachedPolledValue(ref _gpuDieTemp, static self =>
            self._smcRead is null ? null : Platform.Mac.MacSmcTemperatures.Average(self._smcRead, Platform.Mac.MacSmcTemperatures.GpuKeys));

    internal float? ReadSsdTemperature()
        => CachedPolledValue(ref _ssdTemp, static self =>
            self._smcRead is null ? null : Platform.Mac.MacSmcTemperatures.Average(self._smcRead, Platform.Mac.MacSmcTemperatures.SsdKeys));

    internal float? ReadGpuUtilization()
        => CachedPolledValue(ref _gpuUtilization, static _ => Platform.Mac.MacGpuStats.TryReadDeviceUtilization());

    private float? CachedPolledValue(ref PolledValueCache cache, Func<MacSensorProvider, float?> read)
    {
        var now = Environment.TickCount64;
        lock (_pollLock)
        {
            if (cache.ReadAtMs != 0 && now - cache.ReadAtMs < PollCacheTtlMs)
            {
                return cache.Value;
            }
        }

        var value = read(this);
        lock (_pollLock)
        {
            cache.ReadAtMs = now;
            cache.Value = value;
        }
        return value;
    }

    // Cached static hardware data - fetched once, never changes at runtime.
    private string? _cpuModel;
    private string? _moboModel;
    private string? _memTotal;
    private long _memTotalBytes = -1;
    private List<string>? _gpuModels;
    private string? _gpuProfilerOutput;
    private int? _cpuCoreCount;
    private int? _perfCores;
    private int? _effCores;
    private string? _ramBrandModel;
    private string? _storageBrandModel;

    // Mach CPU tick delta tracking (macOS only). Guarded by _pollLock and
    // cached for one tick: the broadcaster, summary topic, and history
    // sampler all call GetCpuSensors, and an unsynchronized delta would hand
    // each caller a window shortened by whichever caller read last.
    private Platform.Mac.MachStats.CpuLoadInfo _prevCpuTicks;
    private bool _hasPrevCpuTicks;
    private CpuLoadCache _cpuLoad;

    private struct CpuLoadCache
    {
        public long ReadAtMs;
        public bool Available;
        public float UserPct;
        public float SysPct;
    }

    private bool TryGetCpuLoadPercents(out float userPct, out float sysPct)
    {
        var now = Environment.TickCount64;
        lock (_pollLock)
        {
            if (_cpuLoad.ReadAtMs != 0 && now - _cpuLoad.ReadAtMs < PollCacheTtlMs)
            {
                userPct = _cpuLoad.UserPct;
                sysPct = _cpuLoad.SysPct;
                return _cpuLoad.Available;
            }

            // host_statistics is a microsecond syscall; holding the lock
            // through it keeps the delta computation atomic per window.
            if (!Platform.Mac.MachStats.TryGetCpuLoad(out var ticks))
            {
                _cpuLoad = new CpuLoadCache { ReadAtMs = now };
                userPct = 0f;
                sysPct = 0f;
                return false;
            }

            if (_hasPrevCpuTicks)
            {
                uint du = ticks.UserTicks - _prevCpuTicks.UserTicks;
                uint ds = ticks.SystemTicks - _prevCpuTicks.SystemTicks;
                uint di = ticks.IdleTicks - _prevCpuTicks.IdleTicks;
                uint dn = ticks.NiceTicks - _prevCpuTicks.NiceTicks;
                uint dt = du + ds + di + dn;
                userPct = dt > 0 ? du * 100f / dt : 0f;
                sysPct = dt > 0 ? ds * 100f / dt : 0f;
            }
            else
            {
                uint dt = ticks.UserTicks + ticks.SystemTicks + ticks.IdleTicks + ticks.NiceTicks;
                userPct = dt > 0 ? ticks.UserTicks * 100f / dt : 0f;
                sysPct = dt > 0 ? ticks.SystemTicks * 100f / dt : 0f;
            }
            _prevCpuTicks = ticks;
            _hasPrevCpuTicks = true;
            _cpuLoad = new CpuLoadCache { ReadAtMs = now, Available = true, UserPct = userPct, SysPct = sysPct };
            return true;
        }
    }

    // Mac hardware enumeration is shell-driven (sysctl / system_profiler) and
    // synchronous - Get* methods cache lazily on first call. No async warmup
    // window to wait on.
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ── CPU ──────────────────────────────────────────────────────

    public string GetCpuModel()
    {
        if (_cpuModel is not null)
        {
            return _cpuModel;
        }

        _cpuModel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? ShellOut("/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string").Trim()
            : "";
        return _cpuModel;
    }

    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return EmptySensors;
        }

        var sensors = new List<HardwareSensor>();
        var model = GetCpuModel();

        // CPU load via Mach host_statistics (microsecond syscall, no subprocess).
        if (TryGetCpuLoadPercents(out var userPct, out var sysPct))
        {
            sensors.Add(MakeSensor("cpu/load", "CPU Total", "Load", userPct + sysPct, "%", model));
            sensors.Add(MakeSensor("cpu/user", "CPU User", "Load", userPct, "%", model));
            sensors.Add(MakeSensor("cpu/system", "CPU System", "Load", sysPct, "%", model));
        }

        var cpuTemp = ReadCpuDieTemperature();
        if (cpuTemp.HasValue)
        {
            sensors.Add(MakeSensor("cpu/temp", "CPU Die", "Temperature", cpuTemp.Value, "°C", model));
        }

        // Core counts - static, cache on first call.
        if (_cpuCoreCount is null)
        {
            var ncpu = ShellOut("/usr/sbin/sysctl", "-n", "hw.logicalcpu").Trim();
            _cpuCoreCount = int.TryParse(ncpu, out var c) ? c : 0;

            var perfRaw = ShellOut("/usr/sbin/sysctl", "-n", "hw.perflevel0.logicalcpu").Trim();
            _perfCores = int.TryParse(perfRaw, out var p) ? p : 0;

            var effRaw = ShellOut("/usr/sbin/sysctl", "-n", "hw.perflevel1.logicalcpu").Trim();
            _effCores = int.TryParse(effRaw, out var e) ? e : 0;
        }

        if (_cpuCoreCount > 0)
        {
            sensors.Add(MakeSensor("cpu/cores", "Logical Cores", "Factor", _cpuCoreCount.Value, "", model));
        }

        if (_perfCores > 0)
        {
            sensors.Add(MakeSensor("cpu/perf-cores", "Performance Cores", "Factor", _perfCores.Value, "", model));
        }

        if (_effCores > 0)
        {
            sensors.Add(MakeSensor("cpu/eff-cores", "Efficiency Cores", "Factor", _effCores.Value, "", model));
        }

        return sensors;
    }

    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);

    // ── GPU ──────────────────────────────────────────────────────

    private string GetGpuProfilerOutput()
    {
        if (_gpuProfilerOutput is not null)
        {
            return _gpuProfilerOutput;
        }

        _gpuProfilerOutput = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? ShellOut("/usr/sbin/system_profiler", 10000, "SPDisplaysDataType")
            : "";
        return _gpuProfilerOutput;
    }

    public IReadOnlyList<string> GetGpuModels()
    {
        if (_gpuModels is not null)
        {
            return _gpuModels;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        { _gpuModels = new List<string>(); return _gpuModels; }
        _gpuModels = ParseGpuModels(GetGpuProfilerOutput());
        return _gpuModels;
    }

    public IReadOnlyList<HardwareSensor> GetGpuSensors() => GetGpus().SelectMany(g => g.Sensors).ToList();

    public IReadOnlyList<GpuReadout> GetGpus()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new List<GpuReadout>();
        }

        var output = GetGpuProfilerOutput();
        var models = ParseGpuModels(output);
        var gpus = new List<GpuReadout>();

        // The SMC exposes one set of GPU die keys, not per-adapter readings -
        // attach the temperature to the primary GPU only (discrete first,
        // mirroring SummarySensors.PrimaryGpuSensors). When system_profiler
        // yields no adapters the reading is dropped here; the cooling surface
        // still carries it via MacFanControlProvider.
        var gpuTemp = ReadGpuDieTemperature();
        var gpuUtil = ReadGpuUtilization();
        int primaryIdx = 0;
        for (int i = 0; i < models.Count; i++)
        {
            if (!GpuClassifier.FromName(models[i]).Integrated)
            {
                primaryIdx = i;
                break;
            }
        }

        for (int i = 0; i < models.Count; i++)
        {
            var gpu = models[i];
            var sensors = new List<HardwareSensor> { MakeSensor($"gpu/{i}/model", "Model", "Factor", 0, "", gpu) };

            if (i == primaryIdx && gpuTemp.HasValue)
            {
                sensors.Add(MakeSensor($"gpu/{i}/temp", "GPU Die", "Temperature", gpuTemp.Value, "°C", gpu));
            }

            // Named "GPU Core" so the summary (GpuUsage) and history (gpu
            // load series) pickers match it the same way they match the
            // Windows LHM sensor.
            if (i == primaryIdx && gpuUtil.HasValue)
            {
                sensors.Add(MakeSensor($"gpu/{i}/load", "GPU Core", "Load", gpuUtil.Value, "%", gpu, theoreticalMax: 100f));
            }

            // Extract Total Number of Cores
            var coresMatch = Regex.Match(output, @"Total Number of Cores:\s*(\d+)");
            if (coresMatch.Success && int.TryParse(coresMatch.Groups[1].Value, out var gpuCores))
            {
                sensors.Add(MakeSensor($"gpu/{i}/cores", "GPU Cores", "Factor", gpuCores, "", gpu));
            }

            // Extract VRAM if present (discrete GPUs)
            var vramMatch = Regex.Match(output, @"VRAM.*?:\s*(\d+)\s*(MB|GB)");
            if (vramMatch.Success && double.TryParse(vramMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vram))
            {
                var unit = vramMatch.Groups[2].Value;
                sensors.Add(MakeSensor($"gpu/{i}/vram", "VRAM", "SmallData", (float)(unit == "GB" ? vram : vram / 1024.0), "GB", gpu));
            }

            // Classify by name: Apple Silicon and Intel iGPUs are integrated, AMD
            // "Radeon Pro"/RX are discrete. The system_profiler VRAM line isn't
            // scoped to the current GPU, so on a dual-GPU Mac it can't tell them
            // apart - the name is the reliable signal, don't let VRAM override it.
            var (vendor, integrated) = GpuClassifier.FromName(gpu);

            gpus.Add(new GpuReadout
            {
                Id = $"gpu/{i}",
                Name = gpu,
                Vendor = vendor,
                Integrated = integrated,
                Sensors = sensors,
            });
        }

        return gpus;
    }

    // ── Memory ───────────────────────────────────────────────────

    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return EmptySensors;
        }

        var sensors = new List<HardwareSensor>();
        var totalBytes = GetMemsizeBytes();
        var totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);

        // VM stats via Mach host_statistics64 (microsecond syscall, no subprocess).
        if (Platform.Mac.MachStats.TryGetVmStats(out var vm))
        {
            long pageSize = Platform.Mac.MachStats.GetPageSize();
            var active = (long)vm.ActivePages * pageSize;
            var inactive = (long)vm.InactivePages * pageSize;
            var wired = (long)vm.WiredPages * pageSize;
            var compressed = (long)vm.CompressorPages * pageSize;
            var used = active + wired + compressed;
            var usedGb = used / (1024.0 * 1024.0 * 1024.0);
            var freeGb = totalGb - usedGb;
            var usagePct = totalBytes > 0 ? (float)(used * 100.0 / totalBytes) : 0f;

            // TheoreticalMaximum = installed RAM (GB) so the client can scale a
            // "X / Y GB" chart without a separate /system/memory/total fetch.
            sensors.Add(MakeSensor("mem/used", "Memory Used", "Data", (float)usedGb, "GB", "Memory", theoreticalMax: (float)totalGb));
            sensors.Add(MakeSensor("mem/available", "Memory Available", "Data", (float)freeGb, "GB", "Memory", theoreticalMax: (float)totalGb));
            sensors.Add(MakeSensor("mem/usage", "Memory Usage", "Load", usagePct, "%", "Memory", theoreticalMax: 100f));
            sensors.Add(MakeSensor("mem/wired", "Wired", "Data", (float)(wired / (1024.0 * 1024.0 * 1024.0)), "GB", "Memory"));
            sensors.Add(MakeSensor("mem/compressed", "Compressed", "Data", (float)(compressed / (1024.0 * 1024.0 * 1024.0)), "GB", "Memory"));
            sensors.Add(MakeSensor("mem/active", "Active", "Data", (float)(active / (1024.0 * 1024.0 * 1024.0)), "GB", "Memory"));
            sensors.Add(MakeSensor("mem/inactive", "Inactive", "Data", (float)(inactive / (1024.0 * 1024.0 * 1024.0)), "GB", "Memory"));
        }

        return sensors;
    }

    public string GetMemoryTotalFormatted()
    {
        if (_memTotal is not null)
        {
            return _memTotal;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var bytes = GetMemsizeBytes();
            if (bytes > 0)
            { _memTotal = $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB"; return _memTotal; }
        }
        _memTotal = "";
        return _memTotal;
    }

    // ── Storage ──────────────────────────────────────────────────

    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true)
    {
        var result = new Dictionary<string, StorageComponent>();
        var ssdTemp = ReadSsdTemperature();
        foreach (var di in GetRealDrives())
        {
            var totalGb = di.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = di.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usedGb = totalGb - freeGb;
            var usePct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;
            var label = di.Name.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(label))
            {
                label = "/";
            }

            var sensors = new List<HardwareSensor>
            {
                MakeSensor($"storage/{label}/used", "Used", "Data", (float)usedGb, "GB", label, theoreticalMax: (float)totalGb),
                MakeSensor($"storage/{label}/free", "Free", "Data", (float)freeGb, "GB", label, theoreticalMax: (float)totalGb),
                MakeSensor($"storage/{label}/usage", "Usage", "Level", (float)usePct, "%", label),
            };

            // The SMC NAND keys describe the internal SSD; external and
            // secondary volumes get no temperature row.
            if (ssdTemp.HasValue && IsInternalVolume(di.Name))
            {
                sensors.Add(MakeSensor($"storage/{label}/temp", "Drive Temperature", "Temperature", ssdTemp.Value, "°C", label));
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
                Sensors = sensors,
            };
        }
        return result;
    }

    private static bool IsInternalVolume(string mountName)
        => mountName is "/" or "/System/Volumes/Data" or "/System/Volumes/Data/";

    public IReadOnlyList<string> GetStoragePartitions()
    {
        return GetRealDrives().Select(d => d.Name).ToList();
    }

    public IReadOnlyList<StorageDriveInfo> GetStorageInfo()
    {
        return GetRealDrives().Select(d =>
        {
            var totalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0);
            return new StorageDriveInfo
            {
                Name = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.Name,
                Partition = d.Name,
                Capacity = FormatGb(totalGb),
            };
        }).ToList();
    }

    private static List<System.IO.DriveInfo> GetRealDrives()
    {
        var drives = new List<System.IO.DriveInfo>();
        foreach (var di in System.IO.DriveInfo.GetDrives())
        {
            if (!di.IsReady)
            {
                continue;
            }

            if (di.DriveType != System.IO.DriveType.Fixed)
            {
                continue;
            }

            if (di.TotalSize < 1L * 1024 * 1024 * 1024)
            {
                continue; // skip < 1 GB
            }

            var name = di.Name;
            // macOS: skip virtual/developer/temp mounts
            if (name.Contains("/Library/Developer/"))
            {
                continue;
            }

            if (name.Contains("/private/var/"))
            {
                continue;
            }

            if (name.StartsWith("/System/Volumes/") && name != "/System/Volumes/Data")
            {
                continue;
            }

            // macOS APFS: if /System/Volumes/Data exists, skip / (read-only snapshot)
            if (name == "/" && System.IO.DriveInfo.GetDrives().Any(d =>
                d.IsReady && d.Name == "/System/Volumes/Data"))
            {
                continue;
            }

            drives.Add(di);
        }
        return drives;
    }

    // ── Motherboard ──────────────────────────────────────────────

    public IReadOnlyList<HardwareSensor> GetMotherboardSensors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return EmptySensors;
        }

        var model = GetMotherboardModel();
        if (string.IsNullOrEmpty(model))
        {
            return EmptySensors;
        }

        return new[]
        {
            MakeSensor("mobo/model", "Model Identifier", "Factor", 0, "", model),
        };
    }

    public string GetMotherboardModel()
    {
        if (_moboModel is not null)
        {
            return _moboModel;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        { _moboModel = ""; return _moboModel; }
        var output = ShellOut("/usr/sbin/system_profiler", 10000, "SPHardwareDataType");
        var match = Regex.Match(output, @"Model Identifier:\s*(.+)");
        _moboModel = match.Success ? match.Groups[1].Value.Trim() : "";
        return _moboModel;
    }

    public string GetRamBrandModel()
    {
        if (_ramBrandModel is not null)
            return _ramBrandModel;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        { _ramBrandModel = ""; return _ramBrandModel; }
        // On Apple Silicon SPMemoryDataType is terse ("Memory: 16 GB, Type: LPDDR5")
        // and there's no user-replaceable DIMM to brand. Fall back to chip info so
        // the System Builder still gets a reasonable "model" string to match on.
        var output = ShellOut("/usr/sbin/system_profiler", 10000, "SPMemoryDataType");
        // Intel-era DIMMs expose Manufacturer + Part Number blocks per slot.
        var mfg = Regex.Match(output, @"Manufacturer:\s*(?!0x[0-9A-Fa-f]+$)(.+)");
        var part = Regex.Match(output, @"Part Number:\s*(?!0x[0-9A-Fa-f]+$)(.+)");
        if (mfg.Success && part.Success)
        {
            _ramBrandModel = $"{mfg.Groups[1].Value.Trim()} {part.Groups[1].Value.Trim()}";
            return _ramBrandModel;
        }
        // Apple Silicon path: "Type: LPDDR5" / "Manufacturer: Apple" etc.
        var type = Regex.Match(output, @"Type:\s*(.+)");
        var mfgFb = Regex.Match(output, @"Manufacturer:\s*(.+)");
        var parts = new List<string>();
        if (mfgFb.Success)
            parts.Add(mfgFb.Groups[1].Value.Trim());
        if (type.Success)
            parts.Add(type.Groups[1].Value.Trim());
        _ramBrandModel = parts.Count > 0 ? string.Join(" ", parts) : "";
        return _ramBrandModel;
    }

    public string GetStorageBrandModel()
    {
        if (_storageBrandModel is not null)
            return _storageBrandModel;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        { _storageBrandModel = ""; return _storageBrandModel; }
        // NVMe drives (modern Macs) surface via SPNVMeDataType with "Model:" lines.
        // SATA / external drives surface via SPSerialATADataType / SPStorageDataType.
        // Prefer NVMe since every Apple Silicon / modern Intel Mac is NVMe internal.
        var nvme = ShellOut("/usr/sbin/system_profiler", 10000, "SPNVMeDataType");
        var nvmeMatch = Regex.Match(nvme, @"Model:\s*(.+)");
        if (nvmeMatch.Success)
        {
            _storageBrandModel = nvmeMatch.Groups[1].Value.Trim();
            return _storageBrandModel;
        }
        var sata = ShellOut("/usr/sbin/system_profiler", 10000, "SPSerialATADataType");
        var sataMatch = Regex.Match(sata, @"Model:\s*(.+)");
        _storageBrandModel = sataMatch.Success ? sataMatch.Groups[1].Value.Trim() : "";
        return _storageBrandModel;
    }

    // macOS has no equivalent surface for the Detailed-tab extras (battery /
    // PSU / per-NIC throughput / cooler / NVMe controller / EC). Return an
    // empty extras frame and let the SPA hide the corresponding sections.
    public SensorExtras GetSensorExtras() => new();

    // ── OS ───────────────────────────────────────────────────────

    public string GetOsVersion() => RuntimeInformation.OSDescription;

    public void SetPollingRate(int pollingRate) { /* reserved for MonitoringBroadcaster cadence */ }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private static HardwareSensor MakeSensor(string id, string name, string type, float value, string units, string parentName, float theoreticalMax = 0f)
    {
        var formatted = type switch
        {
            "Load" or "Level" => $"{value:F1}{units}",
            "Data" or "SmallData" => $"{value:F2} {units}",
            "Clock" or "Frequency" => $"{value:F0} {units}",
            "Temperature" => $"{value:F1} {units}",
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

    private static List<string> ParseGpuModels(string sysprofOutput)
    {
        var models = new List<string>();
        foreach (var line in sysprofOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Chipset Model:", StringComparison.Ordinal))
            {
                var name = trimmed.Substring("Chipset Model:".Length).Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    models.Add(name);
                }
            }
        }
        return models;
    }

    private long GetMemsizeBytes()
    {
        if (_memTotalBytes >= 0)
            return _memTotalBytes;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _memTotalBytes = Platform.Mac.MachStats.GetPhysicalMemory();
        }
        else
        {
            var raw = ShellOut("/usr/sbin/sysctl", "-n", "hw.memsize").Trim();
            _memTotalBytes = long.TryParse(raw, out var bytes) ? bytes : 0;
        }
        return _memTotalBytes;
    }

    private static string FormatGb(double gb) => gb >= 1000 ? $"{gb / 1024.0:F2} TB" : $"{gb:F2} GB";

    private static string ShellOut(string fileName, params string[] args)
        => ShellExecutor.Run(fileName, args);

    private static string ShellOut(string fileName, int timeoutMs, params string[] args)
        => ShellExecutor.Run(fileName, timeoutMs, args);
}
