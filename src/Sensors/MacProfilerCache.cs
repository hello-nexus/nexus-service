using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors;

/// <summary>
/// system_profiler output per data type, read once per process. The sensor provider's brand lookups and the hardware inventory both
/// need SPMemoryDataType, SPNVMeDataType and SPSerialATADataType, and each call costs seconds on macOS. A run that timed out or exited
/// non-zero returns empty and is not cached, so truncated output is never parsed into a partial inventory.
/// </summary>
internal static class MacProfilerCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Output = new();

    public static string Get(string dataType)
    {
        lock (Gate)
        {
            if (Output.TryGetValue(dataType, out var cached))
                return cached;
            var (text, ok) = ShellExecutor.RunChecked("/usr/sbin/system_profiler", 10_000, dataType);
            if (!ok)
                return string.Empty;
            Output[dataType] = text;
            return text;
        }
    }
}
