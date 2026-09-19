using System.Text.Json.Serialization;
using Nexus.Service.Deck;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.QSeries;

namespace Nexus.Service.Serialization;

[JsonSerializable(typeof(NexusSettings))]
[JsonSerializable(typeof(TryxSettings))]
[JsonSerializable(typeof(TryxOverlaySensorItemSettings))]
[JsonSerializable(typeof(List<TryxOverlaySensorItemSettings>))]
[JsonSerializable(typeof(LianLiSettings))]
[JsonSerializable(typeof(LianLiWirelessSettings))]
[JsonSerializable(typeof(LianLiWirelessScreenSettings))]
[JsonSerializable(typeof(Dictionary<string, LianLiWirelessScreenSettings>))]
[JsonSerializable(typeof(CorsairSettings))]
[JsonSerializable(typeof(FirmwareManifest))]
[JsonSerializable(typeof(FirmwareFile))]
[JsonSerializable(typeof(AuthSettings))]
[JsonSerializable(typeof(AiIntegrationSettings))]
[JsonSerializable(typeof(ObsSettings))]
[JsonSerializable(typeof(SteamSettings))]
[JsonSerializable(typeof(DiscordSettings))]
[JsonSerializable(typeof(HomeAssistantSettings))]
[JsonSerializable(typeof(WeatherSettings))]
[JsonSerializable(typeof(WeatherSavedLocation))]
[JsonSerializable(typeof(List<WeatherSavedLocation>))]
[JsonSerializable(typeof(PanelPhoneSessionToken))]
[JsonSerializable(typeof(List<PanelPhoneSessionToken>))]
[JsonSerializable(typeof(CloudAccountRecord))]
[JsonSerializable(typeof(List<CloudAccountRecord>))]
[JsonSerializable(typeof(CloudProfileSyncRecord))]
[JsonSerializable(typeof(Dictionary<string, CloudProfileSyncRecord>))]
[JsonSerializable(typeof(LedPositionOverride))]
[JsonSerializable(typeof(List<LedPositionOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<LedPositionOverride>>))]
[JsonSerializable(typeof(OpenRgbDetectorExclusion))]
[JsonSerializable(typeof(OpenRgbManualDevices))]
[JsonSerializable(typeof(QmkOpenRgbDeviceEntry))]
[JsonSerializable(typeof(E131DeviceEntry))]
[JsonSerializable(typeof(Dictionary<string, OpenRgbDetectorExclusion>))]
// Zones model - device partitions and segment-local LED overrides.
[JsonSerializable(typeof(ZoneDef))]
[JsonSerializable(typeof(List<ZoneDef>))]
[JsonSerializable(typeof(Dictionary<string, List<ZoneDef>>))]
[JsonSerializable(typeof(ZoneSlice))]
[JsonSerializable(typeof(List<ZoneSlice>))]
[JsonSerializable(typeof(ChainEntry))]
[JsonSerializable(typeof(List<ChainEntry>))]
[JsonSerializable(typeof(Dictionary<string, List<ChainEntry>>))]
[JsonSerializable(typeof(SegmentLedOverride))]
[JsonSerializable(typeof(List<SegmentLedOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<SegmentLedOverride>>))]
[JsonSerializable(typeof(Dictionary<string, float>))]
// Per-hub channel composition (mirror / combine rings).
[JsonSerializable(typeof(HubCompositionSettings))]
[JsonSerializable(typeof(Dictionary<string, HubCompositionSettings>))]
[JsonSerializable(typeof(ProfileManifest))]
[JsonSerializable(typeof(ProfileExport))]
// Shared POCOs nested under NexusSettings root - picked up transitively but
// listed explicitly so the source generator emits the proper converters.
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(MonitoringSettings))]
[JsonSerializable(typeof(PanelSettings))]
[JsonSerializable(typeof(OverlaySettings))]
[JsonSerializable(typeof(UnitsSettings))]
[JsonSerializable(typeof(DiagnosticsSettings))]
[JsonSerializable(typeof(DiagnosticsThresholds))]
[JsonSerializable(typeof(DiagnosticsNotifications))]
[JsonSerializable(typeof(DiagnosticsComponents))]
[JsonSerializable(typeof(FeaturesSettings))]
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
// StreamedPanelStore: (device serial → panel record id + profile overrides)
// so a streamed panel keeps its layout/theme across restarts/re-attaches.
[JsonSerializable(typeof(Nexus.Service.Panel.Streams.StreamedPanelRecord))]
[JsonSerializable(typeof(Dictionary<string, Nexus.Service.Panel.Streams.StreamedPanelRecord>))]
// Smart (network) lights - paired Hue / Nanoleaf / WLED / etc. config.
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
[JsonSerializable(typeof(LianLiLightingSettings))]
[JsonSerializable(typeof(StrimerLightingSettings))]
[JsonSerializable(typeof(NollieSettings))]
[JsonSerializable(typeof(NollieStandaloneSettings))]
[JsonSerializable(typeof(Dictionary<string, NollieStandaloneSettings>))]
[JsonSerializable(typeof(Galahad2LightingSettings))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(CoolingPreset))]
[JsonSerializable(typeof(List<CoolingPreset>))]
[JsonSerializable(typeof(LayoutPreset))]
[JsonSerializable(typeof(List<LayoutPreset>))]
[JsonSerializable(typeof(PresetAppBinding))]
[JsonSerializable(typeof(List<PresetAppBinding>))]
// Stream Deck bindings - the shared DeckAction/DeckConfig tree (see
// Nexus.Service.Deck.DeckActionModel) persisted per physical deck serial.
[JsonSerializable(typeof(StreamDeckSettings))]
[JsonSerializable(typeof(PhysicalDeckSettings))]
[JsonSerializable(typeof(Dictionary<string, PhysicalDeckSettings>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DeckPreset))]
[JsonSerializable(typeof(List<DeckPreset>))]
[JsonSerializable(typeof(DeckInstance))]
[JsonSerializable(typeof(Dictionary<string, DeckInstance>))]
[JsonSerializable(typeof(RecentApp))]
[JsonSerializable(typeof(List<RecentApp>))]
[JsonSerializable(typeof(DeckConfig))]
[JsonSerializable(typeof(DeckFolder))]
[JsonSerializable(typeof(DeckSlot))]
[JsonSerializable(typeof(List<DeckSlot>))]
[JsonSerializable(typeof(DeckIcon))]
[JsonSerializable(typeof(DeckTitleStyle))]
[JsonSerializable(typeof(DeckAction))]
[JsonSerializable(typeof(DeckSystemAction))]
[JsonSerializable(typeof(DeckNexusAction))]
[JsonSerializable(typeof(DeckToggleState))]
[JsonSerializable(typeof(DeckSequenceStep))]
[JsonSerializable(typeof(List<DeckSequenceStep>))]
// Metadata-only for the same reason as AppJsonContext: settings writes are
// rare, so the generated fast-path writer is pure AOT size.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
public partial class PersistenceJsonContext : JsonSerializerContext;
