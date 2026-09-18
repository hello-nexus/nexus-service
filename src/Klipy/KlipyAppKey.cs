using System;
using System.IO;
using Nexus.Service.Persistence;

namespace Nexus.Service.Klipy;

/// <summary>
/// Resolves the api.klipy.com app key at runtime - NEXUS_KLIPY_KEY, then
/// klipy-key.txt in the Nexus data directory - so no literal sits in the AOT
/// binary for `strings` to read. Absent both, only the GIF picker is affected.
/// </summary>
internal static class KlipyAppKey
{
    public const string EnvVar = "NEXUS_KLIPY_KEY";
    public const string FileName = "klipy-key.txt";

    private static readonly object Lock = new();
    private static string? _cached;
    private static bool _resolved;

    public static string? Resolve()
    {
        lock (Lock)
        {
            if (_resolved)
            {
                return _cached;
            }
            _resolved = true;
            _cached = ReadEnv() ?? ReadFile();
            return _cached;
        }
    }

    /// <summary>Test seam: drops the memoized key so a changed env var is re-read.</summary>
    internal static void Invalidate()
    {
        lock (Lock)
        {
            _resolved = false;
            _cached = null;
        }
    }

    private static string? ReadEnv()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadFile()
    {
        try
        {
            var path = Path.Combine(JsonConfigStore.ResolveDataDirectory(), FileName);
            if (!File.Exists(path))
            {
                return null;
            }
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[klipy] key file unreadable: {ex.Message}");
            return null;
        }
    }
}
