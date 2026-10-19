using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Activity;
using Nexus.Service.Platform.Windows;

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

    // Unknown URI ids are caller-supplied and their misses are not icon-cached,
    // so the URL -> .url file map is built once per TTL instead of per request.
    private static readonly TimeSpan UrlIndexTtl = TimeSpan.FromMinutes(1);
    private Dictionary<string, string>? _urlIndex;
    private DateTime _urlIndexExpiry;
    private readonly object _urlIndexLock = new();

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
            var apps = ReadStartApps();
            if (apps.Count == 0)
                return Array.Empty<Shortcut>();

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

            Nexus.Service.Platform.Windows.LaunchFocus.Arm();
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
    private static bool IsLocalRootedPath(string path) => ShortcutTargets.IsLocalDrivePath(path);

    /// <summary>Icon for a targetId that is a file path or URI rather than a Start-Menu id, so a deck key bound to a game's exe, a .url or a steam:// link shows the right icon.</summary>
    private byte[] ExtractExecutablePathIcon(string targetId)
    {
        var path = ShortcutTargets.Normalize(targetId);
        if (ShortcutTargets.IsLaunchUri(path))
        {
            var urlFile = FindUrlShortcutByUri(path);
            return urlFile is null ? Array.Empty<byte>() : ExtractIconForFile(targetId, urlFile);
        }

        // Extension allowlist, not just File.Exists: GetIcon is panel-reachable,
        // and this is the only branch that takes a caller-supplied path. The
        // local-drive test is what keeps a UNC path out - File.Exists on
        // \\host\share triggers outbound SMB auth and blocks this synchronous
        // call for the SMB timeout.
        var ext = Path.GetExtension(path);
        var isIconSource =
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        if (!isIconSource || !IsLocalRootedPath(path) || !File.Exists(path))
        {
            return Array.Empty<byte>();
        }

        return ExtractIconForFile(targetId, path);
    }

    private byte[] ExtractWin32Icon(Shortcut shortcut)
    {
        var shortcutFile = FindShortcutFile(shortcut.Name);
        if (shortcutFile is not null)
        {
            var png = ExtractIconForFile(shortcut.Id, shortcutFile);
            if (png.Length > 0)
            {
                return png;
            }
        }

        // The apps folder also lists bare exe paths, some behind a known-folder GUID prefix.
        return ExtractExecutablePathIcon(shortcut.Id);
    }

    /// <summary>Disk-cached icon of a .lnk, .url or direct icon-bearing file.</summary>
    private byte[] ExtractIconForFile(string cacheId, string sourcePath)
    {
        DateTime sourceWriteTimeUtc;
        try
        {
            sourceWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);
        }
        catch
        {
            return Array.Empty<byte>();
        }

        var cached = _diskCache.TryGet(cacheId, sourcePath, sourceWriteTimeUtc);
        if (cached is not null)
        {
            return cached;
        }

        var png = sourcePath.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
            ? ExtractInternetShortcutPng(sourcePath)
            : _iconExtractor.ExtractPng(sourcePath, IconSizePx);
        if (png.Length > 0)
        {
            _diskCache.Store(cacheId, sourcePath, sourceWriteTimeUtc, png);
        }

        return png;
    }

    /// <summary>A .url carries its icon as IconFile/IconIndex (Steam points these at its own .ico files). Empty when absent or unreadable.</summary>
    private byte[] ExtractInternetShortcutPng(string urlPath)
    {
        try
        {
            if (new FileInfo(urlPath).Length > ShortcutTargets.MaxInternetShortcutBytes)
            {
                return Array.Empty<byte>();
            }

            var (_, iconFile, iconIndex) = ShortcutTargets.ParseInternetShortcut(ShortcutTargets.ReadInternetShortcutText(urlPath));
            if (iconFile is null)
            {
                return Array.Empty<byte>();
            }

            var iconPath = ShortcutTargets.Normalize(iconFile);
            return IsLocalRootedPath(iconPath) && File.Exists(iconPath)
                ? _iconExtractor.ExtractPng(iconPath, IconSizePx, iconIndex)
                : Array.Empty<byte>();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Finds the Start-Menu .lnk whose base name matches, searching the same two roots the old PowerShell path did.</summary>
    private static string? FindShortcutLnk(string name) => FindStartMenuFile(name, "*.lnk");

    /// <summary>Like <see cref="FindShortcutLnk"/> but also matches .url internet shortcuts, which is what Steam games are.</summary>
    private static string? FindShortcutFile(string name) =>
        FindStartMenuFile(name, "*.lnk") ?? FindStartMenuFile(name, "*.url");

    private static string? FindStartMenuFile(string name, string pattern)
    {
        foreach (var dir in StartMenuProgramsDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(dir, pattern, options))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>The Start-Menu .url whose URL equals the given URI, for ids like steam://rungameid/730 that name no file.</summary>
    private string? FindUrlShortcutByUri(string uri)
    {
        lock (_urlIndexLock)
        {
            if (_urlIndex is null || _urlIndexExpiry <= DateTime.UtcNow)
            {
                _urlIndex = BuildUrlIndex();
                _urlIndexExpiry = DateTime.UtcNow + UrlIndexTtl;
            }
            return _urlIndex.TryGetValue(uri, out var file) ? file : null;
        }
    }

    private static Dictionary<string, string> BuildUrlIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var dir in StartMenuProgramsDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.url", options))
            {
                try
                {
                    if (new FileInfo(file).Length > ShortcutTargets.MaxInternetShortcutBytes)
                    {
                        continue;
                    }
                    var url = ShortcutTargets.ParseInternetShortcut(ShortcutTargets.ReadInternetShortcutText(file)).Url;
                    if (!string.IsNullOrEmpty(url))
                    {
                        index.TryAdd(url, file);
                    }
                }
                catch { }
            }
        }

        return index;
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
        try
        {
            var installPath = WindowsAppsFolder.FindPackageInstallPath(appId.Split('!')[0]);
            if (installPath is null) return Array.Empty<byte>();

            var logo = UwpLogoResolver.ManifestLogo(File.ReadAllText(Path.Combine(installPath, "AppxManifest.xml")));
            if (string.IsNullOrEmpty(logo)) return Array.Empty<byte>();

            var logoFull = Path.Combine(installPath, logo);
            var dir = Path.GetDirectoryName(logoFull) ?? installPath;
            string? target = null;
            if (Directory.Exists(dir))
            {
                var best = UwpLogoResolver.BestScaledFile(Path.GetFileName(logoFull),
                    Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>());
                if (best is not null) target = Path.Combine(dir, best);
            }
            if (target is null && File.Exists(logoFull)) target = logoFull;
            return target is null ? Array.Empty<byte>() : File.ReadAllBytes(target);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>The Start apps as the shell lists them, minus uninstallers, ordered and de-duplicated by name.</summary>
    private static List<Shortcut> ReadStartApps()
    {
        var apps = new List<Shortcut>();
        foreach (var (name, appId) in WindowsAppsFolder.EnumerateStartApps(15000))
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId)) continue;
            if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
            apps.Add(new Shortcut { Id = appId, Name = name, Path = appId });
        }

        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
