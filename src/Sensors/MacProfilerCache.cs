using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors;

/// <summary>
/// system_profiler output per data type, read once per process. The sensor provider's brand lookups and the hardware inventory both
/// need SPMemoryDataType, SPNVMeDataType and SPSerialATADataType, and each call costs seconds on macOS.
/// </summary>
internal static class MacProfilerCache
{
    private static readonly ConcurrentDictionary<string, System.Lazy<string>> Output = new();

    public static string Get(string dataType) =>
        Output.GetOrAdd(dataType, t => new System.Lazy<string>(() => ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, t))).Value;
}
