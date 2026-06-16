using System.Text.Json.Serialization;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.QSeries;

namespace Nexus.Service.Serialization;

[JsonSerializable(typeof(NexusSettings))]
[JsonSerializable(typeof(FirmwareManifest))]
[JsonSerializable(typeof(FirmwareFile))]
[JsonSerializable(typeof(CloudAccountSettings))]
[JsonSerializable(typeof(AuthSettings))]
[JsonSerializable(typeof(ObsSettings))]
[JsonSerializable(typeof(SteamSettings))]
[JsonSerializable(typeof(DiscordSettings))]
[JsonSerializable(typeof(PanelPhoneSessionToken))]
[JsonSerializable(typeof(List<PanelPhoneSessionToken>))]
[JsonSerializable(typeof(LedPositionOverride))]
[JsonSerializable(typeof(List<LedPositionOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<LedPositionOverride>>))]
// Zones model - device partitions and segment-local LED overrides.
[JsonSerializable(typeof(ZoneDef))]
[JsonSerializable(typeof(List<ZoneDef>))]
[JsonSerializable(typeof(Dictionary<string, List<ZoneDef>>))]
[JsonSerializable(typeof(ZoneSlice))]
[JsonSerializable(typeof(List<ZoneSlice>))]
[JsonSerializable(typeof(SegmentLedOverride))]
[JsonSerializable(typeof(List<SegmentLedOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<SegmentLedOverride>>))]
[JsonSerializable(typeof(Dictionary<string, float>))]
[JsonSerializable(typeof(ProfileManifest))]
[JsonSerializable(typeof(ProfileExport))]
// Shared POCOs nested under NexusSettings root — picked up transitively but
// listed explicitly so the source generator emits the proper converters.
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(MonitoringSettings))]
[JsonSerializable(typeof(PanelSettings))]
[JsonSerializable(typeof(OverlaySettings))]
[JsonSerializable(typeof(PanelLayoutsDefaults))]
[JsonSerializable(typeof(PanelLayoutDefault))]
[JsonSerializable(typeof(PanelLayoutWidget))]
[JsonSerializable(typeof(List<PanelLayoutWidget>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
// QSeriesTransportStore: (USB-serial → TCP transport record). Lets the
// service remember which Q-series devices have been promoted to
// adb-over-WiFi so panel transport survives USB-FFS adb wedges across
// service / adb-server restarts.
[JsonSerializable(typeof(QSeriesTransportRecord))]
[JsonSerializable(typeof(Dictionary<string, QSeriesTransportRecord>))]
// Smart (network) lights — paired Hue / Nanoleaf / WLED / etc. config.
[JsonSerializable(typeof(SmartLightsSettings))]
[JsonSerializable(typeof(SmartLightConfig))]
[JsonSerializable(typeof(List<SmartLightConfig>))]
// Community LED mappings - applied artifacts embedded in settings plus the
// per-device group delta layer.
[JsonSerializable(typeof(Nexus.Service.Lighting.Mappings.AppliedMappingRef))]
[JsonSerializable(typeof(Dictionary<string, Nexus.Service.Lighting.Mappings.AppliedMappingRef>))]
[JsonSerializable(typeof(Nexus.Service.Lighting.Mappings.MappingArtifact))]
[JsonSerializable(typeof(Nexus.Service.Lighting.Mappings.MappingGroup))]
[JsonSerializable(typeof(List<Nexus.Service.Lighting.Mappings.MappingGroup>))]
[JsonSerializable(typeof(Dictionary<string, List<Nexus.Service.Lighting.Mappings.MappingGroup>>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class PersistenceJsonContext : JsonSerializerContext;
