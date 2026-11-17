using System.Runtime.CompilerServices;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests;

/// <summary>Suites that drive LightingWriteHealth.Shared or DeviceIncidentLog.Shared must not append incident snapshots to the developer's real logs directory.</summary>
internal static class SharedIncidentLogConfig
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-test-shared-incidents-" + Environment.ProcessId);
        DeviceIncidentLog.SharedDirectoryOverride = () => dir;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        };
    }
}
