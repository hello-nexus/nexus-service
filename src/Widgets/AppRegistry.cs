using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets;

/// <summary>
/// Discovers and caches installed widgets. Scanning is lazy on first read +
/// explicit <see cref="Refresh"/>; Phase 0 does not watch the filesystem.
/// </summary>
/// <remarks>
/// The registry is a singleton. Lookups are <see cref="StringComparer.Ordinal"/>
/// to match URL path semantics.
/// </remarks>
public sealed class AppRegistry
{
    private readonly Func<IReadOnlyList<AppInstallPaths.Root>> _rootsProvider;
    private readonly ConcurrentDictionary<string, AppEntry> _entries =
        new(StringComparer.Ordinal);
    private int _loaded;

    public AppRegistry() : this(() => AppInstallPaths.Enumerate()) { }

    /// <summary>
    /// Test seam: lets unit tests point the registry at a fixture directory.
    /// </summary>
    public AppRegistry(Func<IReadOnlyList<AppInstallPaths.Root>> rootsProvider)
    {
        _rootsProvider = rootsProvider;
    }

    public IReadOnlyCollection<AppEntry> All()
    {
        EnsureLoaded();
        return (IReadOnlyCollection<AppEntry>)_entries.Values;
    }

    public bool TryGet(string id, out AppEntry entry)
    {
        EnsureLoaded();
        return _entries.TryGetValue(id, out entry!);
    }

    public void Refresh()
    {
        _entries.Clear();
        Volatile.Write(ref _loaded, 0);
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        lock (_entries)
        {
            if (Volatile.Read(ref _loaded) == 1) return;
            foreach (var root in _rootsProvider())
            {
                LoadRoot(root);
            }
            Volatile.Write(ref _loaded, 1);
        }
    }

    private void LoadRoot(AppInstallPaths.Root root)
    {
        if (!Directory.Exists(root.Path)) return;
        foreach (var dir in Directory.EnumerateDirectories(root.Path))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppManifest);
                if (manifest is null) continue;
                if (!AppIds.IsValid(manifest.Id)) continue;
                // Bundle id must match folder name; refusing the mismatch
                // prevents one bundle from impersonating another via folder
                // rename (and keeps URL → disk-path resolution unambiguous).
                var folderName = Path.GetFileName(dir);
                if (!string.Equals(folderName, manifest.Id, StringComparison.Ordinal)) continue;
                // nexus.app/1 is the current schema; nexus.widget/2 is the legacy
                // id accepted for back-compat with bundles published before the rename.
                if (!string.Equals(manifest.Schema, "nexus.app/1", StringComparison.Ordinal)
                    && !string.Equals(manifest.Schema, "nexus.widget/2", StringComparison.Ordinal))
                {
                    continue;
                }
                // Every app's widget facet is an SDK (sandboxed remote-component) bundle
                // rendered from widget.mjs in the sandboxed host. The legacy declarative
                // view-tree runtime has been removed; reject anything that isn't SDK.
                if (!string.Equals(manifest.Runtime, "sdk", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(dir, "widget.mjs"))) continue;
                // Apply default size if author omitted it.
                if (string.IsNullOrEmpty(manifest.DefaultSize) && manifest.Sizes.Count > 0)
                {
                    manifest.DefaultSize = manifest.Sizes[0];
                }

                // A `driver` block - which lets the host fetch and run a native
                // executable - is honored only for a bundled app (one shipped in
                // the trusted build / OEM image). A user-installed app
                // cannot grant itself a host-run driver; the widget facet still
                // loads, only the driver block is dropped. (When app signing lands
                // this becomes a cert-grant check.)
                if (manifest.Driver is not null && root.Source != AppInstallPaths.Source.Bundled)
                {
                    Nexus.Service.Platform.ServiceLog.Warn(
                        $"[apps] {manifest.Id} declares a driver block but is not a bundled app; ignoring it.");
                    manifest.Driver = null;
                }

                var entry = new AppEntry
                {
                    Id = manifest.Id,
                    RootPath = dir,
                    Manifest = manifest,
                    Source = root.Source,
                };
                // First write wins. Roots are enumerated in shadowing order
                // (user, then bundled), so later roots cannot overwrite
                // an entry from a higher-precedence root.
                _entries.TryAdd(manifest.Id, entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Malformed or unreadable manifests are skipped rather than
                // failing service startup.
                Console.Error.WriteLine($"[widgets] skipping {dir}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
