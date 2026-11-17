using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// Host action surfacing SMBIOS identity plus the System Specs strings to
/// declarative widgets, for a system-info page rendered by an SDK app.
/// </summary>
public static class SystemSpecsActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("system.specs", async (services, _, ct) =>
        {
            var oemInfo = services.GetRequiredService<OemInfo>();
            var collector = services.GetRequiredService<SystemSpecsCollector>();
            var specs = await collector.GetAsync(ct);

            var dto = new SystemInfoResponse
            {
                Manufacturer = oemInfo.Manufacturer ?? "",
                Model = oemInfo.Model ?? "",
                Family = oemInfo.Family ?? "",
                Serial = oemInfo.Serial ?? "",
                WindowsProductKey = specs.Oa3ProductKey,
                WindowsActivated = WindowsActivation.Get(),
                PcName = specs.PcName,
                OsBuild = specs.OsBuild,
                Processor = specs.Processor,
                Motherboard = specs.Motherboard,
                Memory = specs.Memory,
                Storage = specs.Storage,
                GraphicsCard = specs.GraphicsCard,
                Monitor = specs.Monitor,
                SoundCard = specs.SoundCard,
                NetworkCard = specs.NetworkCard,
                NexusVersion = BuildInfo.Version.TrimStart('v', 'V'),
            };

            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.SystemInfoResponse);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "system.specs" };
}
