using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// Host actions exposed to declarative widgets for display brightness
/// + enumeration. Mirrors the panel-internal /displays endpoints but
/// gates each through the widget manifest's <c>capabilities.dispatch</c>
/// allowlist (handled by the dispatch route).
/// </summary>
public static class DisplayActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("displays.list", async (services, _, _) =>
        {
            var controller = services.GetRequiredService<DisplayBrightnessController>();
            var dto = controller.ListDisplays();
            // Re-serialise into a JsonElement so the dispatch response
            // can pass it through under AOT (source-generated context).
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.DisplayListResponse);
            using var doc = JsonDocument.Parse(json);
            return await System.Threading.Tasks.Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("displays.setBrightness", async (services, args, ct) =>
        {
            if (args is null) return null;
            if (!args.TryGetValue("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
            if (!args.TryGetValue("value", out var valueEl)) return null;
            var id = idEl.GetString() ?? "";
            int brightness = valueEl.ValueKind switch
            {
                JsonValueKind.Number => valueEl.GetInt32(),
                JsonValueKind.String when int.TryParse(valueEl.GetString(), out var parsed) => parsed,
                _ => -1,
            };
            if (string.IsNullOrEmpty(id) || brightness < 0 || brightness > 100) return null;

            var controller = services.GetRequiredService<DisplayBrightnessController>();
            var result = await controller.SetBrightnessAsync(id, brightness, ct);
            var json = JsonSerializer.Serialize(result, AppJsonContext.Default.DisplayBrightnessDto);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        });

        // Which DDC-only HYTE panel is attached, as a Y70DisplayProtocol
        // variant key ("y70-gw" / "y70-ina"), or "" when none is. An app whose
        // content ships with one specific panel gates itself on this rather
        // than enumerating displays itself.
        registry.Register("displays.panelVariant", (services, _, _) =>
        {
            var topology = services.GetRequiredService<DisplayTopologyService>();
            var variant = topology.DdcOnlyY70Variant();
#if DEV_TOOLS
            // Internal builds report the Ina panel when no DDC-only panel is
            // attached, so a panel-gated app can be laid out and photographed
            // on ordinary lab hardware. Never compiled into a release publish.
            if (variant.Length == 0) variant = Y70DisplayProtocol.VariantIna;
#endif
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("variant", variant);
                writer.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return System.Threading.Tasks.Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });
    }

    /// <summary>Convenience list of all action names this module owns -
    /// used by tests and the documented allowlist for the bundled
    /// displays widget.</summary>
    public static IReadOnlyList<string> AllActions => new[]
    {
        "displays.list", "displays.setBrightness", "displays.panelVariant",
    }.ToList();
}
