using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Sensors;

namespace Nexus.Service.Widgets;

/// <summary>
/// Filesystem mover for marketplace widgets. v1 catalogue is the bundled
/// widgets discovered on disk; "install" copies the source bundle
/// into the user widgets dir (creating it if needed), "uninstall" removes
/// that user copy. The registry is refreshed afterwards so subsequent
/// list / read calls see the new state.
/// </summary>
public sealed class AppInstaller
{
    private readonly AppRegistry _registry;
    private readonly OemInfo _oemInfo;

    public AppInstaller(AppRegistry registry, OemInfo oemInfo)
    {
        _registry = registry;
        _oemInfo = oemInfo;
    }

    /// <summary>
    /// Catalogue = every widget the registry currently sees, decorated with
    /// whether the user widgets dir holds a copy. Bundled widgets show
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
                AppInstallPaths.Source.User => "user",
                AppInstallPaths.Source.Bundled => "bundled",
                _ => "unknown",
            };
            var iconUrl = !string.IsNullOrWhiteSpace(entry.Manifest.Icon)
                ? $"/apps-api/installed/{entry.Id}/asset/{entry.Manifest.Icon}"
                : null;
            // No oem block means no gate; an oem block gates preinstall on the
            // detected SMBIOS manufacturer matching one of the listed names.
            var oemMatch = entry.Manifest.Oem?.Manufacturer is not { Count: > 0 } manufacturers
                || _oemInfo.Matches(manufacturers);
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
                Preinstalled = entry.Manifest.Preinstalled && entry.Source == AppInstallPaths.Source.Bundled && oemMatch,
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

        // Compute the user widgets dir from the install paths enumerator;
        // it's the entry whose Source is User. (Windows: %ProgramData%\Nexus\apps;
        // the per-user data dir on macOS/Linux.)
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
            return new AppInstallResponse { Id = id, Installed = false, Error = "no user widgets directory" };
        }

        try
        {
            // Lock the root ACL before writing into it (prod / LocalSystem only).
            AppInstallPaths.SecureUserRoots();
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
            return new AppInstallResponse { Id = id, Installed = false, Error = "no user widgets directory" };
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
