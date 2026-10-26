using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Benchmarks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Sensors;

/// <summary>
/// Builds a one-line-per-field <see cref="SystemSpecsResponse"/> describing the
/// PC for the Devices → System Specs tab. The values are display strings -
/// callers paste them straight into the UI or a shared rig summary.
///
/// Reads from <see cref="ISensorProvider"/> for the LHM-resident basics
/// (CPU / GPU model, motherboard, memory total) and reads the extras LHM
/// doesn't surface on Windows in-process via <see cref="WindowsSpecsNative"/>:
/// OS build, RAM stick layout, disks, monitor model + resolution, sound card,
/// network adapters.
///
/// Cached for the lifetime of the service; no TTL since hardware specs (CPU,
/// motherboard, monitor model, NIC) don't change while the process is alive.
/// <see cref="SystemSpecsPrewarmService"/> fills the cache once after host
/// start; every subsequent <see cref="Get(bool)"/> returns the same object in
/// microseconds. Explicit `force:true` rebuilds.
/// </summary>
public sealed class SystemSpecsCollector
{
    private readonly ISensorProvider _sensors;
    private readonly object _lock = new();
    private SystemSpecsResponse? _cached;

    public SystemSpecsCollector(ISensorProvider sensors)
    {
        _sensors = sensors;
    }

    /// <summary>
    /// Async accessor that guarantees the LHM background open has finished
    /// before reading hardware-name fields. Once cached, returns the snapshot
    /// in microseconds. The very first call after boot pays the ~1-3 s LHM
    /// open + the Build (native enrichment on Windows).
    /// </summary>
    public async Task<SystemSpecsResponse> GetAsync(CancellationToken ct = default)
    {
        var snapshot = _cached;
        if (snapshot is not null) return snapshot;

        // Wait outside the lock - `ReadyAsync` for the Windows provider is a
        // background `Computer.Open` task that can take seconds; holding the
        // lock would serialise unrelated concurrent callers behind it.
        await _sensors.ReadyAsync(ct).ConfigureAwait(false);

        // Also outside the lock: macOS shells out to system_profiler for this, which takes seconds.
        var inventory = await Task.Run(HardwareInventoryCollector.GetCached, ct).ConfigureAwait(false);

        lock (_lock)
        {
            if (_cached is not null) return _cached;
            _cached = Build(inventory);
            return _cached;
        }
    }

    private SystemSpecsResponse Build(HardwareInventory inventory)
    {
        var cards = _sensors.GetGpuCards();
        var s = new SystemSpecsResponse
        {
            PcName = SafeMachineName(),
            OsBuild = _sensors.GetOsVersion(),
            Processor = _sensors.GetCpuModel(),
            Motherboard = _sensors.GetMotherboardModel(),
            Memory = _sensors.GetMemoryTotalFormatted(),
            GraphicsCard = string.Join(" + ", cards.Select(c => c.Display)),
            GpuCards = cards.ToList(),
            PrimaryGpu = BenchmarkRunner.SelectReportedGpus(_sensors.GetGpus()).FirstOrDefault()
                ?? _sensors.GetGpuModels().FirstOrDefault() ?? "",
        };

        s.HardwareIds = HardwareIdsBuilder.Build(cards, inventory, ReadBoard());

        if (OperatingSystem.IsWindows())
            EnrichWindows(s);
        else if (OperatingSystem.IsMacOS())
            EnrichMac(s);

        // Physical drives only: volume labels and mount points ("C:", "/boot") are never a storage description.
        if (string.IsNullOrWhiteSpace(s.Storage))
        {
            if (inventory.Drives.Count > 0)
                s.Storage = string.Join(" + ", inventory.Drives.Select(DescribeDrive));
            else
                s.Storage = _sensors.GetStorageBrandModel();
        }

        // Non-Windows platforms report only a total; add the module layout when the platform exposes it.
        if (!OperatingSystem.IsWindows() && inventory.RamModules.Count > 0 && !s.Memory.Contains('('))
        {
            var sticks = inventory.RamModules.Where(m => m.CapacityBytes is > 0).Select(m => m.CapacityBytes!.Value).ToList();
            var detail = StickLayout(sticks, PrimaryFirst(inventory.RamModules.Select(m =>
                JoinBrandPart(m.Manufacturer is null ? "" : HardwareInventoryParsers.BrandLabel(m.Manufacturer), m.PartNumber ?? ""))));
            if (detail.Length > 0 && s.Memory.Length > 0) s.Memory = $"{s.Memory} ({detail})";
        }

        return s;
    }

    // Windows reads the SMBIOS baseboard record, Linux the world-readable DMI board files. macOS has no baseboard identity.
    private static (string? Manufacturer, string? Product)? ReadBoard()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadWindowsBoard();
            if (OperatingSystem.IsLinux())
                return (ReadDmi("board_vendor"), ReadDmi("board_name"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[system-specs] {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static (string? Manufacturer, string? Product)? ReadWindowsBoard() => WindowsSpecsNative.ReadBaseboard();

    private static string? ReadDmi(string file)
    {
        try
        {
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(LinuxPciSubsystem.DmiRoot, file)).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }

    // ── Windows enrichment (in-process native reads) ──────────────────────

    [SupportedOSPlatform("windows")]
    private static void EnrichWindows(SystemSpecsResponse s)
    {
        var obj = WindowsSpecsNative.Collect();

        // Each field applies on its own so one failed read cannot blank the rest.
        TryApply(() => ApplyOs(s, obj["os"]));
        TryApply(() => ApplyRam(s, obj["ram"]));
        TryApply(() => ApplyDisks(s, obj["disks"]));
        TryApply(() => ApplyMonitors(s, obj["monitors"], obj["video"]));
        TryApply(() => ApplySound(s, obj["sound"]));
        TryApply(() => ApplyNet(s, obj["net"]));
        TryApply(() => s.Oa3ProductKey = JsonString(obj["oa3"]).Trim());
    }

    private static void TryApply(Action apply)
    {
        try { apply(); }
        catch (Exception ex) { Console.Error.WriteLine($"[system-specs] {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void ApplyOs(SystemSpecsResponse s, JsonNode? node)
    {
        var os = node?.AsObject();
        if (os is null) return;
        // Caption is "Microsoft Windows 11 Pro"; Version is "10.0.22631"
        // (already includes the build). Drop the "Microsoft " prefix.
        var caption = JsonString(os["Caption"]).Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase);
        var version = JsonString(os["Version"]);
        if (!string.IsNullOrWhiteSpace(caption))
            s.OsBuild = string.IsNullOrEmpty(version) ? caption : $"{caption} ({version})";
    }

    private static void ApplyRam(SystemSpecsResponse s, JsonNode? node)
    {
        var rows = node?.AsArray();
        if (rows is null || rows.Count == 0) return;

        long totalBytes = 0;
        var perStick = new List<long>();
        int speed = 0;
        var brands = new List<string>();
        int memType = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            var cap = JsonLong(row["Capacity"]);
            if (cap > 0) { totalBytes += cap; perStick.Add(cap); }
            if (speed == 0)
            {
                var configured = JsonInt(row["ConfiguredClockSpeed"]);
                var rated = JsonInt(row["Speed"]);
                speed = configured > 0 ? configured : rated;
            }
            var brand = BrandOf(JsonString(row["Manufacturer"]), JsonString(row["PartNumber"]));
            if (!string.IsNullOrEmpty(brand)) brands.Add(brand);
            if (memType == 0) memType = JsonInt(row["SMBIOSMemoryType"]);
        }

        if (totalBytes <= 0) return;
        var generation = MemoryGeneration(memType);
        var totalLabel = FormatGb(totalBytes / 1024.0 / 1024.0 / 1024.0);
        var head = totalLabel;
        if (!string.IsNullOrEmpty(generation))
            head += speed > 0 ? $" {generation}-{speed}" : $" {generation}";
        else if (speed > 0)
            head += $" @ {speed} MT/s";

        var detail = StickLayout(perStick, PrimaryFirst(brands));
        s.Memory = string.IsNullOrEmpty(detail) ? head : $"{head} ({detail})";
    }

    // Every distinct module brand, not just the first stick: mixed kits and per-stick vendors would otherwise read as one brand.
    private static string BrandOf(string manufacturer, string partNumber)
    {
        var (name, _) = HardwareInventoryParsers.ResolveManufacturer(Clean(manufacturer), null);
        return JoinBrandPart(name is null ? "" : HardwareInventoryParsers.BrandLabel(name), HardwareInventoryParsers.CleanPartNumber(Clean(partNumber)) ?? "");
    }

    // Most common kit first (ties by first slot): the catalog matcher reads only the text before the first "+" as the kit's brand and part number.
    internal static string PrimaryFirst(IEnumerable<string> brands)
    {
        var all = brands.Where(b => b.Length > 0).ToList();
        return string.Join(" + ", all.Distinct().OrderByDescending(b => all.Count(x => x == b)));
    }

    private static string StickLayout(List<long> sticks, string brand)
    {
        if (sticks.Count == 0) return "";
        var groups = sticks.GroupBy(b => b).OrderByDescending(g => g.Key).ToList();
        var layout = groups.Count == 1
            ? $"{sticks.Count} × {FormatGb(groups[0].Key / 1024.0 / 1024.0 / 1024.0)}"
            : $"{sticks.Count} sticks";
        return string.IsNullOrEmpty(brand) ? layout : $"{layout} {brand}";
    }

    private static string JoinBrandPart(string mfg, string part)
    {
        if (string.IsNullOrEmpty(mfg) && string.IsNullOrEmpty(part)) return "";
        if (string.IsNullOrEmpty(mfg)) return part;
        if (string.IsNullOrEmpty(part)) return mfg;
        if (part.StartsWith(mfg, StringComparison.OrdinalIgnoreCase)) return part;
        return $"{mfg} {part}";
    }

    private static string MemoryGeneration(int smbiosMemoryType) => smbiosMemoryType switch
    {
        20 => "DDR",
        21 => "DDR2",
        _ => HardwareInventoryParsers.MemoryGeneration(smbiosMemoryType) ?? "",
    };

    private static void ApplyDisks(SystemSpecsResponse s, JsonNode? node)
    {
        var rows = node?.AsArray();
        if (rows is null || rows.Count == 0) return;
        var parts = new List<string>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var name = Clean(JsonString(row["FriendlyName"]));
            if (HardwareInventoryParsers.IsVirtualDrive(name, null)) continue;
            var media = JsonString(row["MediaType"]);
            var bus = JsonString(row["BusType"]);
            var size = JsonLong(row["Size"]);
            var sizeLabel = size > 0 ? FormatGb(size / 1024.0 / 1024.0 / 1024.0) : "";

            var tag = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                : media.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD"
                : media.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                : "";

            var bits = new List<string>();
            if (!string.IsNullOrEmpty(sizeLabel)) bits.Add(sizeLabel);
            if (!string.IsNullOrEmpty(name)) bits.Add(name);
            if (!string.IsNullOrEmpty(tag) && !name.Contains(tag, StringComparison.OrdinalIgnoreCase))
                bits.Add(tag);
            if (bits.Count > 0) parts.Add(string.Join(" ", bits));
        }
        s.Storage = string.Join(" + ", parts);
    }

    private static void ApplyMonitors(SystemSpecsResponse s, JsonNode? mon, JsonNode? video)
    {
        var names = new List<string>();
        foreach (var row in mon?.AsArray()?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var n = Clean(JsonString(row["Name"]));
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }

        // Walk the video controllers and pick the first row with non-zero
        // current resolution - laptops with dGPU+iGPU report both, only one
        // is actually driving pixels.
        string resolution = "";
        foreach (var row in video?.AsArray()?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var w = JsonInt(row["CurrentHorizontalResolution"]);
            var h = JsonInt(row["CurrentVerticalResolution"]);
            var hz = JsonInt(row["CurrentRefreshRate"]);
            if (w > 0 && h > 0)
            {
                resolution = hz > 0 ? $"{w}×{h} @ {hz} Hz" : $"{w}×{h}";
                break;
            }
        }

        s.HardwareIds.Monitors = ParseMonitorIds(mon);

        var nameJoined = string.Join(" + ", names);
        s.Monitor = !string.IsNullOrEmpty(nameJoined) && !string.IsNullOrEmpty(resolution)
            ? $"{nameJoined} ({resolution})"
            : !string.IsNullOrEmpty(nameJoined) ? nameJoined : resolution;
    }

    // Model ids only: name, PnP manufacturer and EDID product code. The EDID serial is never read into the row.
    internal static List<MonitorIdsDto>? ParseMonitorIds(JsonNode? mon)
    {
        var ids = new List<MonitorIdsDto>();
        foreach (var row in mon?.AsArray()?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var manufacturer = JsonString(row["Mfg"]).Trim();
            var productCode = JsonString(row["ProductCode"]).Trim();
            var id = new MonitorIdsDto
            {
                // A PnP id is three letters A-Z and an unprogrammed product code is zero; either is a junk EDID, not a model.
                Manufacturer = manufacturer.Length == 3 && manufacturer.All(c => c is >= 'A' and <= 'Z') ? manufacturer : null,
                ProductCode = productCode.Length == 0 || productCode == "0000" ? null : productCode,
                Name = NullIfEmpty(Clean(JsonString(row["Name"]))),
            };
            if (id.Manufacturer is not null || id.ProductCode is not null || id.Name is not null)
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static void ApplySound(SystemSpecsResponse s, JsonNode? node)
    {
        var names = new List<string>();
        foreach (var row in node?.AsArray()?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var n = Clean(JsonString(row["Name"]));
            if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
        }
        s.SoundCard = string.Join(" + ", names);
    }

    private static void ApplyNet(SystemSpecsResponse s, JsonNode? node)
    {
        var rows = node?.AsArray()?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        // Prefer adapters with Status='Up'; if nothing is up, fall back to
        // listing all physical adapters so CI / unplugged boxes still show
        // their NICs.
        var up = rows.Where(r => string.Equals(JsonString(r["Status"]), "Up", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = up.Count > 0 ? up : rows;

        var parts = new List<string>();
        foreach (var row in chosen)
        {
            var name = Clean(JsonString(row["InterfaceDescription"]));
            var speed = Clean(JsonString(row["LinkSpeed"]));
            if (string.IsNullOrEmpty(name)) continue;
            parts.Add(up.Count > 0 && !string.IsNullOrEmpty(speed) ? $"{name} ({speed})" : name);
        }
        s.NetworkCard = string.Join(" + ", parts);
    }

    private static string DescribeDrive(FleetEventDrive drive)
    {
        var name = drive.Model ?? drive.Vendor ?? "";
        var capacity = drive.CapacityBytes is > 0 ? FormatGb(drive.CapacityBytes.Value / 1024.0 / 1024.0 / 1024.0) : "";
        if (name.Length == 0) return capacity;
        return capacity.Length == 0 ? name : $"{capacity} {name}";
    }

    // ── macOS enrichment ─────────────────────────────────────────────────

    private static void EnrichMac(SystemSpecsResponse s)
    {
        var product = ShellExecutor.Run("/usr/bin/sw_vers", "-productName").Trim();
        var version = ShellExecutor.Run("/usr/bin/sw_vers", "-productVersion").Trim();
        var build = ShellExecutor.Run("/usr/bin/sw_vers", "-buildVersion").Trim();
        if (!string.IsNullOrEmpty(product) || !string.IsNullOrEmpty(version))
        {
            var pretty = string.IsNullOrEmpty(product) ? "macOS" : product;
            if (!string.IsNullOrEmpty(version)) pretty += $" {version}";
            if (!string.IsNullOrEmpty(build)) pretty += $" ({build})";
            s.OsBuild = pretty;
        }

        var hostname = ShellExecutor.Run("/usr/sbin/scutil", "--get", "ComputerName").Trim();
        if (!string.IsNullOrEmpty(hostname)) s.PcName = hostname;

        var displays = ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPDisplaysDataType");
        s.Monitor = ParseMacDisplays(displays);

        var audio = ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPAudioDataType");
        s.SoundCard = ParseMacAudio(audio);

        var net = ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPNetworkDataType");
        s.NetworkCard = ParseMacNetwork(net);
    }

    private static string ParseMacDisplays(string profile)
    {
        var monitors = new List<string>();
        string? current = null;
        foreach (var raw in profile.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (raw.StartsWith("        ") && !raw.StartsWith("          ") && line.EndsWith(":"))
            {
                current = line.Trim().TrimEnd(':');
            }
            else if (line.TrimStart().StartsWith("Resolution:") && current is not null)
            {
                var res = line.Split("Resolution:", 2)[1].Trim();
                monitors.Add($"{current} ({res})");
                current = null;
            }
        }
        return string.Join(" + ", monitors);
    }

    private static string ParseMacAudio(string profile)
    {
        var names = new List<string>();
        foreach (var raw in profile.Split('\n'))
        {
            if (raw.StartsWith("        ") && !raw.StartsWith("          "))
            {
                var name = raw.Trim().TrimEnd(':');
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
            }
        }
        return string.Join(" + ", names);
    }

    private static string ParseMacNetwork(string profile)
    {
        var lines = profile.Split('\n');
        var services = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("    ") && !line.StartsWith("      ") && line.TrimEnd().EndsWith(":"))
            {
                var name = line.Trim().TrimEnd(':');
                bool isHw = false;
                for (int j = i + 1; j < Math.Min(i + 12, lines.Length); j++)
                {
                    if (lines[j].Contains("BSD Device Name")) { isHw = true; break; }
                    if (lines[j].StartsWith("    ") && !lines[j].StartsWith("      ")) break;
                }
                if (isHw && !services.Contains(name)) services.Add(name);
            }
        }
        return string.Join(" + ", services);
    }

    // ── JsonNode coercion helpers ────────────────────────────────────────

    private static string JsonString(JsonNode? n)
    {
        if (n is null) return "";
        try { return n.GetValue<string>() ?? ""; }
        catch
        {
            // Numeric or bool nodes - fall back to the JSON literal.
            try { return n.ToJsonString().Trim('"'); }
            catch { return ""; }
        }
    }

    private static int JsonInt(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<int>(); } catch { }
        try { return (int)n.GetValue<long>(); } catch { }
        try
        {
            var raw = n.ToJsonString().Trim('"');
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static long JsonLong(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<long>(); } catch { }
        try
        {
            var raw = n.ToJsonString().Trim('"');
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static string Clean(string v)
    {
        var s = v?.Trim() ?? "";
        if (s.Length == 0) return "";
        if (s.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)) return "";
        return s;
    }

    private static string FormatGb(double gb)
    {
        if (gb >= 1000) return $"{gb / 1024.0:0.##} TB";
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{gb * 1024:0} MB";
    }
}
