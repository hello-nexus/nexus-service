using System.Text.Json.Serialization;
using Qos.Service.Devices.Firmware;
using Qos.Service.Models.Profiles;
using Qos.Service.Persistence;
using Qos.Service.QSeries;

namespace Qos.Service.Serialization;

[JsonSerializable(typeof(QosSettings))]
[JsonSerializable(typeof(FirmwareManifest))]
[JsonSerializable(typeof(FirmwareFile))]
[JsonSerializable(typeof(AuthSettings))]
[JsonSerializable(typeof(ObsSettings))]
[JsonSerializable(typeof(SteamSettings))]
[JsonSerializable(typeof(DiscordSettings))]
[JsonSerializable(typeof(PanelPhoneSessionToken))]
[JsonSerializable(typeof(List<PanelPhoneSessionToken>))]
[JsonSerializable(typeof(LedPositionOverride))]
[JsonSerializable(typeof(List<LedPositionOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<LedPositionOverride>>))]
[JsonSerializable(typeof(ProfileManifest))]
[JsonSerializable(typeof(ProfileExport))]
// Shared POCOs nested under QosSettings root — picked up transitively but
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
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class PersistenceJsonContext : JsonSerializerContext;
