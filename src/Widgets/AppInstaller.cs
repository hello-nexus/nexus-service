using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Widgets;

/// <summary>
/// Filesystem mover for marketplace widgets. v1 catalogue is the bundled +
/// dev widgets discovered on disk; "install" copies the source bundle
/// into the user widgets dir (creating it if needed), "uninstall" removes
/// that user copy. The registry is refreshed afterwards so subsequent
/// list / read calls see the new state.
/// </summary>
public sealed class AppInstaller
{
    private readonly AppRegistry _registry;

    public AppInstaller(AppRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Catalogue = every widget the registry currently sees, decorated with
    /// whether the user widgets dir holds a copy. Bundled + dev widgets show
    /// up alongside whatever the user has installed.
    /// </summary>
    public AppCatalogResponse Catalogue()
    {
        var resp = new AppCatalogResponse();
        // First pass: dedupe by id but prefer the user copy when present.
        var seen = new Dictionary<string, AppCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in _registry.All())
        {
            var source = entry.Source switch
            {
                AppInstallPaths.Source.Dev => "dev",
                AppInstallPaths.Source.User => "user",
                AppInstallPaths.Source.Bundled => "bundled",
                _ => "unknown",
            };
            var iconUrl = !string.IsNullOrWhiteSpace(entry.Manifest.Icon)
                ? $"/apps-api/installed/{entry.Id}/asset/{entry.Manifest.Icon}"
                : null;
            var card = new AppCatalogEntry
            {
                Id = entry.Id,
                Name = entry.Manifest.Name,
                Version = entry.Manifest.Version,
                Description = entry.Manifest.Description,
                IconUrl = iconUrl,
                Surfaces = new List<string>(entry.Manifest.Surfaces),
                Capabilities = entry.Manifest.Capabilities,
                Source = source,
                Installed = entry.Source == AppInstallPaths.Source.User,
                Preinstalled = entry.Manifest.Preinstalled && entry.Source == AppInstallPaths.Source.Bundled,
            };
            if (!seen.ContainsKey(entry.Id))
            {
                seen[entry.Id] = card;
            }
            else if (entry.Source == AppInstallPaths.Source.User)
            {
                seen[entry.Id] = card;
            }
        }
        resp.Entries.AddRange(seen.Values);
        resp.Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return resp;
    }

    public AppInstallResponse Install(string id)
    {
        if (!AppIds.IsValid(id))
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = "invalid widget id" };
        }
        if (!_registry.TryGet(id, out var entry))
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = "source widget not found" };
        }

        // Compute the user apps dir from the install paths enumerator.
        string? userRoot = null;
        foreach (var root in AppInstallPaths.Enumerate())
        {
            if (root.Source == AppInstallPaths.Source.User)
            {
                userRoot = root.Path;
                break;
            }
        }
        if (userRoot is null)
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = "no user apps directory" };
        }

        try
        {
            Directory.CreateDirectory(userRoot);
            var dest = Path.Combine(userRoot, id);
            // Wipe existing dest so the copy is atomic-ish (only one widget
            // dir per id).
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            CopyDir(entry.RootPath, dest);
        }
        catch (Exception ex)
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = ex.Message };
        }

        _registry.Refresh();
        return new AppInstallResponse { Id = id, Installed = true };
    }

    public AppInstallResponse Uninstall(string id)
    {
        if (!AppIds.IsValid(id))
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = "invalid widget id" };
        }
        string? userRoot = null;
        foreach (var root in AppInstallPaths.Enumerate())
        {
            if (root.Source == AppInstallPaths.Source.User) { userRoot = root.Path; break; }
        }
        if (userRoot is null)
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = "no user apps directory" };
        }
        var path = Path.Combine(userRoot, id);
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            return new AppInstallResponse { Id = id, Installed = false, Error = ex.Message };
        }
        _registry.Refresh();
        // Bundled widgets remain visible after uninstall (the user copy
        // shadowed nothing). Mark Installed=false so the dashboard updates.
        return new AppInstallResponse { Id = id, Installed = false };
    }

    private static void CopyDir(string source, string dest)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        }
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, dest, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
