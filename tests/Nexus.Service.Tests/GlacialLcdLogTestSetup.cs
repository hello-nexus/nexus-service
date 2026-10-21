using System.IO;
using System.Runtime.CompilerServices;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Tests;

internal static class GlacialLcdLogTestSetup
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "nexus-tests-glacial-log");

    [ModuleInitializer]
    internal static void Init() => Restore();

    internal static void Restore() => GlacialLcdLog.UseDirectory(Dir);
}
