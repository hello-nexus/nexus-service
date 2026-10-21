using System.IO;
using System.Runtime.CompilerServices;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Tests;

internal static class GlacialLcdLogTestSetup
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "nexus-tests-glacial-log");

    [ModuleInitializer]
    internal static void Init()
    {
        GlacialLcdLog.ForceOn = true;
        GlacialLcdLog.SetDefaultDirectory(Dir);
    }

    /// <summary>Ends a test's own log directory; its lines are on disk first.</summary>
    internal static void Restore()
    {
        GlacialLcdLog.Flush();
        GlacialLcdLog.UseDirectory(null);
    }
}
