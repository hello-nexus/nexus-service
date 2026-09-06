using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public sealed class WindowsShortcutsProvider : IShortcutsProvider
{
    private const int IconSizePx = 256;
    private static readonly TimeSpan IconCachePruneAge = TimeSpan.FromDays(30);
    private const int IconCachePruneMaxEntries = 500;

    private static readonly TimeSpan AppListCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);
    // An app with no extractable icon (no Start-Menu .lnk match / no UWP logo)
    // otherwise re-runs extraction on every GetIcon, since nothing is cached to
    // short-circuit it - a panel scrolling past icon-less apps keeps the helper
    // busy. Cache the empty result too, but briefly: a miss also covers a
    // transient extraction failure, so the short TTL bounds how long a real
    // icon can be hidden after one load-induced miss while still absorbing a
    // scroll's worth of repeat views.
    private static readonly TimeSpan NegativeIconCacheTtl = TimeSpan.FromMinutes(2);

    private readonly IWindowsIconExtractor _iconExtractor;
    private readonly IconDiskCache _diskCache;

    private List<Shortcut>? _appCache;
    private DateTime _appCacheExpiry;
    private readonly object _appLock = new();

    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();

    // Resolution walks the whole Start-Menu tree for a matching .lnk, so it is
    // cached for the process lifetime. A binding is created once and read on
    // every focus change; an app moving to a different exe between resolutions
    // is not a case worth invalidating for.
    private readonly Dictionary<string, string> _processNameCache = new();
    private readonly object _processNameLock = new();

    public WindowsShortcutsProvider() : this(new WindowsIconExtractor(), new IconDiskCache())
    {
    }

    internal WindowsShortcutsProvider(IWindowsIconExtractor iconExtractor, IconDiskCache diskCache)
    {
        _iconExtractor = iconExtractor;
        _diskCache = diskCache;
        _diskCache.PruneStale(IconCachePruneAge, IconCachePruneMaxEntries);
    }

    public IReadOnlyList<Shortcut> GetAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<Shortcut>();

        lock (_appLock)
        {
            if (_appCache is not null && _appCacheExpiry > DateTime.UtcNow)
                return _appCache;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Get-StartApps | ConvertTo-Json -Compress");

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<Shortcut>();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);

            if (string.IsNullOrWhiteSpace(output))
                return Array.Empty<Shortcut>();

            var apps = ParseStartAppsJson(output);
            FillProcessNames(apps);

            lock (_appLock)
            {
                _appCache = apps;
                _appCacheExpiry = DateTime.UtcNow + AppListCacheTtl;
            }

            return apps;
        }
        catch
        {
            return Array.Empty<Shortcut>();
        }
    }

    /// <summary>Resolves every shortcut's process name from a single walk of
    /// the Start-Menu tree. Calling the per-id resolver in a loop would rewalk
    /// that tree once per app.</summary>
    private void FillProcessNames(List<Shortcut> apps)
    {
        Dictionary<string, string> targetsByLinkName;
        try
        {
            targetsByLinkName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var dir in StartMenuProgramsDirs())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", options))
                {
                    var key = Path.GetFileNameWithoutExtension(lnk);
                    if (!targetsByLinkName.ContainsKey(key)) targetsByLinkName[key] = lnk;
                }
            }
        }
        catch
        {
            return;
        }

        foreach (var app in apps)
        {
            // A UWP AppUserModelID has no .lnk behind it.
            if (app.Id.Contains('!')) continue;
            if (!targetsByLinkName.TryGetValue(app.Name, out var lnkPath)) continue;
            try
            {
                var target = _iconExtractor.ResolveLinkTargetPath(lnkPath);
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    app.ProcessName = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                }
            }
            catch { }
        }

        lock (_processNameLock)
        {
            foreach (var app in apps)
            {
                if (app.ProcessName.Length > 0) _processNameCache[app.Id] = app.ProcessName;
            }
        }
    }

    public Shortcut? GetById(string targetId)
    {
        return GetAll().FirstOrDefault(s =>
            string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, targetId, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] GetIcon(string targetId)
    {
        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(targetId, out var cached) && cached.Expiry > DateTime.UtcNow)
                return cached.Data;
        }

        var shortcut = GetById(targetId);

        byte[] iconBytes;
        try
        {
            iconBytes = shortcut is null
                ? ExtractExecutablePathIcon(targetId)
                : shortcut.Id.Contains('!')
                    ? ExtractUwpIcon(shortcut.Id)
                    : ExtractWin32Icon(shortcut);
        }
        catch
        {
            iconBytes = Array.Empty<byte>();
        }

        // Cache the outcome either way: a hit for the icon's full TTL, a miss
        // (empty bytes, incl. a thrown extraction) briefly so it isn't re-run on
        // every view. A miss on an unknown targetId is NOT cached: the key space
        // there is caller-supplied and the map is never evicted, so a caller
        // could otherwise grow it without bound.
        if (iconBytes.Length > 0 || shortcut is not null)
        {
            lock (_iconLock)
            {
                var ttl = iconBytes.Length > 0 ? IconCacheTtl : NegativeIconCacheTtl;
                _iconCache[targetId] = (iconBytes, DateTime.UtcNow + ttl);
            }
        }

        return iconBytes;
    }

    public string ResolveProcessName(string targetId)
    {
        lock (_processNameLock)
        {
            if (_processNameCache.TryGetValue(targetId, out var cached)) return cached;
        }

        var resolved = ResolveProcessNameCore(targetId);
        lock (_processNameLock)
        {
            _processNameCache[targetId] = resolved;
        }
        return resolved;
    }

    private string ResolveProcessNameCore(string targetId)
    {
        var shortcut = GetById(targetId);
        // A UWP AppUserModelID has no .lnk behind it, and the package's real
        // exe name is not derivable from the id.
        if (shortcut is null || shortcut.Id.Contains('!')) return "";
        if (shortcut.ProcessName.Length > 0) return shortcut.ProcessName;

        try
        {
            var lnkPath = FindShortcutLnk(shortcut.Name);
            if (lnkPath is null) return "";

            var target = _iconExtractor.ResolveLinkTargetPath(lnkPath);
            if (string.IsNullOrEmpty(target)) return "";
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";

            return Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public bool Launch(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"shell:appsFolder\\{shortcut.Path}");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A fully-qualified path on a local drive ("C:\..."), excluding UNC and device paths.</summary>
    private static bool IsLocalRootedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }
        var root = Path.GetPathRoot(path);
        return root is not null && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':';
    }

    /// <summary>Icon for a targetId that is an executable path rather than a Start-Menu id, so a deck key bound to a game's exe shows the game's icon.</summary>
    private byte[] ExtractExecutablePathIcon(string targetId)
    {
        // Extension allowlist, not just File.Exists: GetIcon is panel-reachable,
        // and this is the only branch that takes a caller-supplied path. The
        // local-drive test is what keeps a UNC path out - File.Exists on
        // \\host\share triggers outbound SMB auth and blocks this synchronous
        // call for the SMB timeout.
        var ext = Path.GetExtension(targetId);
        var isExecutable =
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        if (!isExecutable || !IsLocalRootedPath(targetId) || !File.Exists(targetId))
        {
            return Array.Empty<byte>();
        }

        DateTime sourceWriteTimeUtc;
        try
        {
            sourceWriteTimeUtc = File.GetLastWriteTimeUtc(targetId);
        }
        catch
        {
            return Array.Empty<byte>();
        }

        var cached = _diskCache.TryGet(targetId, targetId, sourceWriteTimeUtc);
        if (cached is not null)
        {
            return cached;
        }

        var png = _iconExtractor.ExtractPng(targetId, IconSizePx);
        if (png.Length > 0)
        {
            _diskCache.Store(targetId, targetId, sourceWriteTimeUtc, png);
        }

        return png;
    }

    private byte[] ExtractWin32Icon(Shortcut shortcut)
    {
        var lnkPath = FindShortcutLnk(shortcut.Name);
        if (lnkPath is null)
        {
            return Array.Empty<byte>();
        }

        DateTime sourceWriteTimeUtc;
        try
        {
            sourceWriteTimeUtc = File.GetLastWriteTimeUtc(lnkPath);
        }
        catch
        {
            return Array.Empty<byte>();
        }

        var cached = _diskCache.TryGet(shortcut.Id, lnkPath, sourceWriteTimeUtc);
        if (cached is not null)
        {
            return cached;
        }

        var png = _iconExtractor.ExtractPng(lnkPath, IconSizePx);
        if (png.Length > 0)
        {
            _diskCache.Store(shortcut.Id, lnkPath, sourceWriteTimeUtc, png);
        }

        return png;
    }

    /// <summary>Finds the Start-Menu .lnk whose base name matches, searching the same two roots the old PowerShell path did.</summary>
    private static string? FindShortcutLnk(string name)
    {
        foreach (var dir in StartMenuProgramsDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", options))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(lnk), name, StringComparison.OrdinalIgnoreCase))
                {
                    return lnk;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> StartMenuProgramsDirs()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
    }

    private static byte[] ExtractUwpIcon(string appId)
    {
        var familyName = appId.Split('!')[0].Replace("'", "''");
        var script = $"$pkg = Get-AppxPackage | Where-Object {{ $_.PackageFamilyName -eq '{familyName}' }} | Select-Object -First 1; " +
            "if (-not $pkg) { exit 0 } " +
            "try { $manifest = [xml](Get-Content (Join-Path $pkg.InstallLocation 'AppxManifest.xml')); " +
            "$apps = $manifest.Package.Applications.Application; " +
            "if ($apps -is [array]) { $app = $apps[0] } else { $app = $apps }; " +
            "$logo = $app.VisualElements.Square44x44Logo; " +
            "if (-not $logo) { $logo = $app.VisualElements.Square150x150Logo }; " +
            "if (-not $logo) { exit 0 }; " +
            "$logoFull = Join-Path $pkg.InstallLocation $logo; " +
            "$dir = [IO.Path]::GetDirectoryName($logoFull); " +
            "$base = [IO.Path]::GetFileNameWithoutExtension($logoFull); " +
            "$ext = [IO.Path]::GetExtension($logoFull); " +
            @"$candidates = Get-ChildItem $dir -Filter ""$base*$ext"" -EA SilentlyContinue | " +
            @"Sort-Object { if ($_.Name -match 'scale-(\d+)') { [int]$Matches[1] } else { 0 } } -Descending; " +
            "$target = $null; " +
            "foreach ($c in $candidates) { if (Test-Path $c.FullName) { $target = $c.FullName; break } }; " +
            "if (-not $target -and (Test-Path $logoFull)) { $target = $logoFull }; " +
            "if ($target) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) } " +
            "} catch {}";

        return RunPowerShellBase64(script, 15000);
    }

    private static byte[] RunPowerShellBase64(string script, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<byte>();

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);

            if (string.IsNullOrEmpty(output)) return Array.Empty<byte>();

            return Convert.FromBase64String(output);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static List<Shortcut> ParseStartAppsJson(string json)
    {
        var apps = new List<Shortcut>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get-StartApps returns array when multiple, object when single
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    AddFromElement(apps, item);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddFromElement(apps, root);
            }
        }
        catch { }

        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFromElement(List<Shortcut> apps, JsonElement el)
    {
        var name = el.TryGetProperty("Name", out var n) ? n.GetString() : null;
        var appId = el.TryGetProperty("AppID", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId)) return;

        // Skip uninstallers and system entries
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return;

        apps.Add(new Shortcut
        {
            Id = appId,
            Name = name,
            Path = appId,
        });
    }
}
