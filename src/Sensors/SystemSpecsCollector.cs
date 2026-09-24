using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Benchmarks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors;

/// <summary>
/// Builds a one-line-per-field <see cref="SystemSpecsResponse"/> describing the
/// PC for the Devices → System Specs tab. The values are display strings -
/// callers paste them straight into the UI or a shared rig summary.
///
/// Reads from <see cref="ISensorProvider"/> for the LHM-resident basics
/// (CPU / GPU model, motherboard, memory total) and runs a single PowerShell
/// session on Windows that emits one JSON blob with all the extras LHM
/// doesn't surface: hostname, OS build, RAM stick layout, monitor model +
/// resolution, sound card, network adapters. One process spawn keeps the cold
/// first call under ~500 ms.
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
    /// open + ~400 ms Build (PowerShell enrichment on Windows).
    /// </summary>
    public async Task<SystemSpecsResponse> GetAsync(CancellationToken ct = default)
    {
        var snapshot = _cached;
        if (snapshot is not null) return snapshot;

        // Wait outside the lock - `ReadyAsync` for the Windows provider is a
        // background `Computer.Open` task that can take seconds; holding the
        // lock would serialise unrelated concurrent callers behind it.
        await _sensors.ReadyAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            if (_cached is not null) return _cached;
            _cached = Build();
            return _cached;
        }
    }

    private SystemSpecsResponse Build()
    {
        var s = new SystemSpecsResponse
        {
            PcName = SafeMachineName(),
            OsBuild = _sensors.GetOsVersion(),
            Processor = _sensors.GetCpuModel(),
            Motherboard = _sensors.GetMotherboardModel(),
            Memory = _sensors.GetMemoryTotalFormatted(),
            GraphicsCard = string.Join(" + ", _sensors.GetGpuModels()),
            PrimaryGpu = BenchmarkRunner.SelectReportedGpus(_sensors.GetGpus()).FirstOrDefault()
                ?? _sensors.GetGpuModels().FirstOrDefault() ?? "",
        };

        if (OperatingSystem.IsWindows())
            EnrichWindows(s);
        else if (OperatingSystem.IsMacOS())
            EnrichMac(s);

        // Storage fallback for paths the platform-specific enrichment didn't
        // fill in (today: macOS - Windows always populates Storage via the
        // single PS script below).
        if (string.IsNullOrWhiteSpace(s.Storage))
        {
            var brand = _sensors.GetStorageBrandModel();
            var drives = _sensors.GetStorageInfo();
            if (drives.Count > 0)
                s.Storage = string.Join(", ", drives.Select(d => DescribeDrive(d, brand)));
            else if (!string.IsNullOrWhiteSpace(brand))
                s.Storage = brand;
        }

        return s;
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }

    // ── Windows enrichment (single PowerShell, JSON output) ───────────────

    // One process spawn covers OS, RAM DIMMs, physical disks, EDID monitor
    // names, video controller mode, sound endpoints, and physical network
    // adapters. PowerShell start-up dominates at ~200-500 ms; the queries
    // themselves are sub-50 ms each.
    //
    // `@(...)` wrappers force array form even for single-row results so the
    // C# parser doesn't have to branch on JsonObject vs JsonArray.
    private const string WindowsSpecsScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$os = Get-CimInstance -ClassName Win32_OperatingSystem | Select-Object Caption,Version
$ram = @(Get-CimInstance -ClassName Win32_PhysicalMemory | Select-Object Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber,SMBIOSMemoryType)
$disks = @(Get-PhysicalDisk | Where-Object { $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' } | Sort-Object Size -Descending | Select-Object FriendlyName,MediaType,BusType,Size)
$mon = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID | ForEach-Object {
  [PSCustomObject]@{
    Name = (($_.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join '')
    Mfg  = (($_.ManufacturerName  | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join '')
  }
})
$vc = @(Get-CimInstance -ClassName Win32_VideoController | Select-Object CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
$sound = @(Get-CimInstance -ClassName Win32_SoundDevice | Where-Object { $_.Status -eq 'OK' } | Select-Object Name)
$net = @(Get-NetAdapter -Physical | Select-Object InterfaceDescription,LinkSpeed,Status)
$oa3 = (Get-CimInstance -ClassName SoftwareLicensingService).OA3xOriginalProductKey
@{ os = $os; ram = $ram; disks = $disks; monitors = $mon; video = $vc; sound = $sound; net = $net; oa3 = $oa3 } | ConvertTo-Json -Depth 4 -Compress
";

    private static void EnrichWindows(SystemSpecsResponse s)
    {
        // -EncodedCommand takes a Base64 UTF-16LE blob and is the only fully
        // reliable way to ship a multi-line script into powershell.exe from
        // a service-context Process.Start. -Command "-" (stdin) silently
        // returns nothing under LocalSystem on PS 5.1; the ArgumentList
        // escape rules also choke on the script's curly braces. EncodedCommand
        // sidesteps both. 10 s timeout is above the observed ~400 ms warm path.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(WindowsSpecsScript));
        var json = ShellExecutor.Run(
            "powershell.exe", 10_000,
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", encoded);

        if (string.IsNullOrWhiteSpace(json)) return;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return; }
        var obj = root?.AsObject();
        if (obj is null) return;

        ApplyOs(s, obj["os"]);
        ApplyRam(s, obj["ram"]);
        ApplyDisks(s, obj["disks"]);
        ApplyMonitors(s, obj["monitors"], obj["video"]);
        ApplySound(s, obj["sound"]);
        ApplyNet(s, obj["net"]);
        s.Oa3ProductKey = JsonString(obj["oa3"]).Trim();
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
        string mfg = "";
        string part = "";
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
            if (string.IsNullOrEmpty(mfg)) mfg = Clean(JsonString(row["Manufacturer"]));
            if (string.IsNullOrEmpty(part)) part = Clean(JsonString(row["PartNumber"]));
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

        var detail = StickLayout(perStick, mfg, part);
        s.Memory = string.IsNullOrEmpty(detail) ? head : $"{head} ({detail})";
    }

    private static string StickLayout(List<long> sticks, string mfg, string part)
    {
        if (sticks.Count == 0) return "";
        var groups = sticks.GroupBy(b => b).OrderByDescending(g => g.Key).ToList();
        var layout = groups.Count == 1
            ? $"{sticks.Count} × {FormatGb(groups[0].Key / 1024.0 / 1024.0 / 1024.0)}"
            : $"{sticks.Count} sticks";
        var brand = JoinBrandPart(mfg, part);
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
        24 => "DDR3",
        26 => "DDR4",
        34 or 35 => "DDR5",
        _ => "",
    };

    private static void ApplyDisks(SystemSpecsResponse s, JsonNode? node)
    {
        var rows = node?.AsArray();
        if (rows is null || rows.Count == 0) return;
        var parts = new List<string>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var name = Clean(JsonString(row["FriendlyName"]));
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

        var nameJoined = string.Join(" + ", names);
        s.Monitor = !string.IsNullOrEmpty(nameJoined) && !string.IsNullOrEmpty(resolution)
            ? $"{nameJoined} ({resolution})"
            : !string.IsNullOrEmpty(nameJoined) ? nameJoined : resolution;
    }

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

    private static string DescribeDrive(StorageDriveInfo info, string brandFallback)
    {
        var name = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : brandFallback;
        var capacity = info.Capacity?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return capacity;
        return string.IsNullOrWhiteSpace(capacity) ? name : $"{capacity} {name}";
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
