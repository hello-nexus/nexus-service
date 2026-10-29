using System;
using System.IO;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// The dashboard route a plain restart asks the next launch to open on. It rides a file the restarted
/// process reads once: localStorage written just before the restart never reaches disk, so the hint lives service-side.
/// </summary>
internal static class ReopenRoute
{
    /// <summary>A stored route older than this is ignored.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>The route when it is a safe in-app path, else null; the rules are <see cref="OpenAppRoute.Normalize"/>'s.</summary>
    public static string? Validate(string? route) => OpenAppRoute.Normalize(route);

    /// <summary>False when a route was requested but is not acceptable; an absent request is fine and yields no route.</summary>
    public static bool TryResolveRequest(string? requested, out string? route)
    {
        route = null;
        if (requested is null)
        {
            return true;
        }
        route = Validate(requested);
        return route is not null;
    }

    /// <summary>Stored file content as a route, or null when empty, invalid or older than <see cref="MaxAge"/>.</summary>
    public static string? Parse(string? content, DateTime writtenUtc, DateTime nowUtc)
    {
        if (nowUtc - writtenUtc > MaxAge)
        {
            return null;
        }
        return Validate(content?.Trim());
    }

    /// <summary>Route file for platforms with no shared reopen flag (macOS); the Windows flag file carries the route itself.</summary>
    public static string FilePath => Path.Combine(NexusDataPaths.NexusRoot(), "reopen-route");

    public static void WriteFile(string route, string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, route);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[reopen-route] write failed: {ex.Message}");
        }
    }

    /// <summary>Reads and deletes the route file; null when absent, stale or invalid.</summary>
    public static string? ConsumeFile(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var route = Parse(File.ReadAllText(path), File.GetLastWriteTimeUtc(path), DateTime.UtcNow);
            File.Delete(path);
            return route;
        }
        catch
        {
            return null;
        }
    }
}
