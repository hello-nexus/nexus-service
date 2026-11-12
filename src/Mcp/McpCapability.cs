using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp;

/// <summary>
/// Consent domain an <see cref="IMcpTool"/> belongs to. Each value maps to one
/// Allow* flag on <see cref="AiIntegrationSettings"/>, read live on every call.
/// </summary>
public enum McpCapability
{
    Telemetry,
    Cooling,
    Lighting,
    Profiles,
    History,
    Panels,
}

public static class McpCapabilityExtensions
{
    public static bool IsAllowed(this McpCapability capability, AiIntegrationSettings settings) => capability switch
    {
        McpCapability.Telemetry => settings.AllowTelemetry,
        McpCapability.Cooling => settings.AllowCooling,
        McpCapability.Lighting => settings.AllowLighting,
        McpCapability.Profiles => settings.AllowProfiles,
        McpCapability.History => settings.AllowHistory,
        McpCapability.Panels => settings.AllowPanels,
        _ => false,
    };

    /// <summary>Human-readable toggle name, used in the isError text a consent
    /// refusal returns so the caller knows exactly what to enable.</summary>
    public static string ToggleLabel(this McpCapability capability) => capability switch
    {
        McpCapability.Telemetry => "Allow telemetry",
        McpCapability.Cooling => "Allow cooling control",
        McpCapability.Lighting => "Allow lighting control",
        McpCapability.Profiles => "Allow profile switching",
        McpCapability.History => "Allow history access",
        McpCapability.Panels => "Allow panel editing",
        _ => capability.ToString(),
    };
}
