using System.Runtime.CompilerServices;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Tests.CorsairLink;

internal static class CorsairLinkHubTestDefaults
{
    [ModuleInitializer]
    internal static void Init() => CorsairLinkHub.DefaultModeCycleWaitMs = 0;
}
