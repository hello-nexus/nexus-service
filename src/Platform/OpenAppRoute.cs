namespace Nexus.Service.Platform;

/// <summary>Body of POST /service/open-app; Route is optional.</summary>
public sealed class OpenAppBody
{
    public string? Route { get; set; }
}

/// <summary>
/// Validates the app route a caller asks the dashboard window to open. Stricter than an open-redirect
/// check because the Windows path hands the route to a scheduled task command line: only path characters
/// that need no quoting survive, and anything else means "open the default page".
/// </summary>
public static class OpenAppRoute
{
    private const string Prefix = "/system/";
    public const int MaxLength = 200;

    /// <summary>The route when it is a safe in-app path, else null.</summary>
    public static string? Normalize(string? route)
    {
        if (string.IsNullOrEmpty(route) || route.Length > MaxLength) return null;
        if (!route.StartsWith(Prefix, StringComparison.Ordinal) || route.Length == Prefix.Length) return null;
        if (route.Contains("//", StringComparison.Ordinal) || route.Contains("..", StringComparison.Ordinal)) return null;
        var queries = 0;
        foreach (var c in route)
        {
            if (c == '?' && ++queries > 1) return null;
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '/' or '-' or '_' or '.' or '~' or '?' or '=';
            if (!ok) return null;
        }
        return route;
    }

    /// <summary>The local dashboard URL for a normalized route, or the app root when null.</summary>
    public static string LocalUrl(int servicePort, string? normalizedRoute)
    {
        var root = ServiceLaunchIntent.LocalDashboardUrl(servicePort);
        return normalizedRoute is null ? root : root.TrimEnd('/') + normalizedRoute;
    }
}
