using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Real macOS shortcuts provider.
/// - GetAll() enumerates /Applications, /System/Applications, and
///   ~/Applications for .app bundles
/// - GetIcon() renders the bundle icon through the shared MacAppIconExtractor
///   (NSWorkspace, so Assets.car-only bundles resolve too)
/// - Launch() shells out to `open -a`
/// </summary>
public sealed class MacShortcutsProvider : IShortcutsProvider
{
    // GetById, GetIcon, and ResolveProcessName all route through GetAll, and a
    // rebuild spawns one plutil per installed .app. Same TTL the Windows
    // provider uses for the same reason.
    private static readonly TimeSpan AppListCacheTtl = TimeSpan.FromMinutes(5);

    private List<Shortcut>? _appCache;
    private DateTime _appCacheExpiry;
    private readonly object _appLock = new();

    private const int ShortcutIconSizePts = 128;
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);
    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();
    private readonly MacAppIconExtractor _iconExtractor;

    public MacShortcutsProvider(MacAppIconExtractor iconExtractor)
    {
        _iconExtractor = iconExtractor;
    }

    public IReadOnlyList<Shortcut> GetAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Array.Empty<Shortcut>();
        }

        lock (_appLock)
        {
            if (_appCache is not null && _appCacheExpiry > DateTime.UtcNow)
            {
                return _appCache;
            }
        }

        var apps = new List<Shortcut>();
        var searchDirs = new[]
        {
            "/Applications",
            "/System/Applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var appPath in Directory.GetDirectories(dir, "*.app", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(appPath);
                    var info = ReadInfoPlist(appPath);
                    apps.Add(new Shortcut
                    {
                        Id = info.BundleId ?? name,
                        Name = name,
                        Path = appPath,
                        ProcessName = info.DisplayName ?? name,
                    });
                }
            }
            catch { /* access denied, etc. */ }
        }

        var ordered = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        lock (_appLock)
        {
            _appCache = ordered;
            _appCacheExpiry = DateTime.UtcNow + AppListCacheTtl;
        }
        return ordered;
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
            {
                return cached.Data;
            }
        }

        // A targetId that is not a known shortcut may still be a bundle path a
        // deck key was bound to directly; nothing else accepts a caller path,
        // so it must be a fully-qualified .app that exists.
        var shortcut = GetById(targetId);
        var path = shortcut?.Path;
        if (path is null)
        {
            var isBundle = Path.GetExtension(targetId).Equals(".app", StringComparison.OrdinalIgnoreCase);
            if (!isBundle || !Path.IsPathFullyQualified(targetId) || !Directory.Exists(targetId))
            {
                return Array.Empty<byte>();
            }
            path = targetId;
        }

        // A null (extractor timeout) is not cached, so a transient stall does
        // not pin an empty icon for the whole TTL. The smaller proposed size
        // keeps the TTL cache and the physical deck's per-key downscale at
        // list-icon weight rather than full app-icon reps.
        var extracted = _iconExtractor.ExtractPng(path, ShortcutIconSizePts);
        if (extracted is { Length: > 0 })
        {
            lock (_iconLock)
            { _iconCache[targetId] = (extracted, DateTime.UtcNow + IconCacheTtl); }
            return extracted;
        }
        return Array.Empty<byte>();
    }

    // MacScreenTimeProvider reports LSDisplayName, which resolves to
    // CFBundleDisplayName ?? CFBundleName ?? the .app file name - "Visual
    // Studio Code.app" reports "Code", so the file name alone is wrong.
    public string ResolveProcessName(string targetId) => GetById(targetId)?.ProcessName ?? "";

    public bool Launch(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(shortcut.Path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Bundle id and the name LSDisplayName reports, from one plutil
    /// run - a spawn per key would be three per app across the whole list.</summary>
    private static (string? BundleId, string? DisplayName) ReadInfoPlist(string appPath)
    {
        try
        {
            var plistPath = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plistPath))
            {
                return (null, null);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/plutil",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-convert");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add(plistPath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (null, null);
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (output.Length == 0)
            {
                return (null, null);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            return (ReadString(root, "CFBundleIdentifier"),
                ReadString(root, "CFBundleDisplayName") ?? ReadString(root, "CFBundleName"));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadString(System.Text.Json.JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
