using System.Runtime.CompilerServices;
using Nexus.Service.Devices;

namespace Nexus.Service.Tests;

internal static class ExperimentalUnlockConfig
{
    /// <summary>CI builds carry the VERSION file's version, which is stable at release time; pin the unlocked default so the suite does not depend on it. ExperimentalBetaLockTests covers the locked state.</summary>
    [ModuleInitializer]
    internal static void Init() => DeviceControlPolicy.ExperimentalUnlocked = true;
}
