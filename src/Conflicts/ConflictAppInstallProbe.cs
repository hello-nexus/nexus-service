using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Whether a catalog app is installed, as opposed to running. The service
/// starts in session 0 before a vendor app launches at logon, so a
/// running-process check at boot reports absent on boxes that do have it.
/// </summary>
public interface IConflictAppInstallProbe
{
    bool IsInstalled(string appId);

    /// <summary>Every catalog app installed on this box, resolved against one service enumeration.</summary>
    IReadOnlyList<string> InstalledAppIds();
}

/// <summary>
/// Installed = the Service Control Manager lists a service the app owns.
/// Answered once per app per process: install state does not change under a
/// running service, and a restart re-evaluates. False on any failure and
/// everywhere but Windows, so a probe that cannot answer never reports an app.
/// </summary>
public sealed class ConflictAppInstallProbe : IConflictAppInstallProbe
{
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsInstalled(string appId)
    {
        if (_cache.TryGetValue(appId, out var cached))
        {
            return cached;
        }
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        var def = ConflictWatcher.FindById(appId);
        if (def is not null && HasInstallPath(def, Environment.ExpandEnvironmentVariables, File.Exists))
        {
            return _cache.GetOrAdd(appId, true);
        }
        var services = WindowsServiceController.ListServices();
        if (services.Count == 0)
        {
            // Empty reads the same whether the SCM is empty or the enumeration
            // failed, and a cached answer is permanent - so decline to cache one.
            return false;
        }
        return _cache.GetOrAdd(appId, def is not null && HasService(def, services));
    }

    /// <summary>
    /// One service enumeration answers the whole catalog, where per-app
    /// <see cref="IsInstalled"/> walks the full list again for every entry.
    /// Seeds the same cache, so a later single-app check is free, and reports
    /// the cached answer rather than this pass's, so the log cannot disagree
    /// with the value the Nexus Control gate already acted on.
    /// </summary>
    public IReadOnlyList<string> InstalledAppIds()
    {
        var installed = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return installed;
        }
        var services = WindowsServiceController.ListServices();
        foreach (var def in ConflictAppCatalog.All)
        {
            if (HasInstallPath(def, Environment.ExpandEnvironmentVariables, File.Exists))
            {
                _cache[def.Id] = true;
                installed.Add(def.Id);
                continue;
            }
            // The enumeration returns empty on failure as readily as on an empty
            // SCM, and a false seed from it is permanent. Seed nothing then.
            if (services.Count > 0 && _cache.GetOrAdd(def.Id, HasService(def, services)))
            {
                installed.Add(def.Id);
            }
        }
        return installed;
    }

    /// <summary>
    /// Whether one catalog app owns any service in an already-enumerated list.
    /// Exposed for tests, which cannot enumerate a real SCM.
    /// </summary>
    internal static bool HasService(ConflictAppDefinition def, IReadOnlyList<(string Key, string DisplayName)> services)
    {
        // A catalog names services by key and processes by executable basename,
        // and a vendor may register either as the service key, so match both
        // fields of every installed service against both lists.
        var wanted = new List<string>(def.WindowsServiceNames.Length + def.ProcessNames.Length);
        wanted.AddRange(def.WindowsServiceNames);
        wanted.AddRange(def.ProcessNames);
        if (wanted.Count == 0)
        {
            return false;
        }
        foreach (var svc in services)
        {
            if (Matches(wanted, svc.Key) || Matches(wanted, svc.DisplayName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether any of the app's install paths exists. The existence check is injected so tests need no filesystem.</summary>
    internal static bool HasInstallPath(ConflictAppDefinition def, Func<string, string> expand, Func<string, bool> exists)
    {
        foreach (var raw in def.InstallPaths)
        {
            var path = expand(raw);
            if (path.Length > 0 && !path.Contains('%') && exists(path))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a service name matches any wanted name once both are reduced
    /// to their letters and digits, so "Corsair Device Control Service" matches
    /// the catalog's "CorsairDeviceControlService". Exposed for tests.
    /// </summary>
    internal static bool Matches(IReadOnlyList<string> wanted, string serviceName)
    {
        var normalized = Normalize(serviceName);
        if (normalized.Length == 0)
        {
            return false;
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (string.Equals(normalized, Normalize(wanted[i]), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }
        return new string(buffer[..length]);
    }
}
