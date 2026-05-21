using System.Collections.Generic;
using System.Text.Json.Serialization;
using Qos.Service.Activity;
using Qos.Service.Helper;
using Qos.Service.Models;
using Qos.Service.Models.Activity;
using Qos.Service.Models.Benchmarks;
using Qos.Service.Models.Cooling;
using Qos.Service.Models.Devices;
using Qos.Service.Models.Discord;
using Qos.Service.Models.Lifecycle;
using Qos.Service.Models.Lighting;
using Qos.Service.Models.Obs;
using Qos.Service.Models.Peripherals;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Models.Peripherals.QSeries;
using Qos.Service.Models.Peripherals.Y70;
using Qos.Service.Models.Sensors;
using Qos.Service.Models.Steam;
using Qos.Service.Models.Widgets;
using Qos.Service.Platform;
using Qos.Service.Routes;

namespace Qos.Service.Serialization;

// Ping + Auth
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(PairResponse))]

// System / sensors
[JsonSerializable(typeof(PerformanceSnapshot))]
[JsonSerializable(typeof(HardwareSensor))]
[JsonSerializable(typeof(List<HardwareSensor>))]
[JsonSerializable(typeof(IReadOnlyList<HardwareSensor>))]
[JsonSerializable(typeof(HardwareComponent))]
[JsonSerializable(typeof(List<HardwareComponent>))]
[JsonSerializable(typeof(StorageComponent))]
[JsonSerializable(typeof(Dictionary<string, StorageComponent>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, StorageComponent>))]
[JsonSerializable(typeof(StorageDriveInfo))]
[JsonSerializable(typeof(SensorExtras))]
[JsonSerializable(typeof(GetModelResponse))]
[JsonSerializable(typeof(GetGpuModelsResponse))]
[JsonSerializable(typeof(CpuHealthResponse))]
[JsonSerializable(typeof(ProcessElevationResponse))]
[JsonSerializable(typeof(ProcessElevationRelaunchResponse))]
[JsonSerializable(typeof(GetStoragePartitionsResponse))]
[JsonSerializable(typeof(GetDriveStorageResponse))]
[JsonSerializable(typeof(SetPollingRateBody))]
[JsonSerializable(typeof(GetPollingRateResponse))]

// Cooling
[JsonSerializable(typeof(CoolingComponent))]
[JsonSerializable(typeof(GetAllCoolingResponse))]
[JsonSerializable(typeof(SetCurvesBody))]
[JsonSerializable(typeof(GetCurvesResponse))]
[JsonSerializable(typeof(CoolingWarning))]
[JsonSerializable(typeof(List<CoolingWarning>))]
[JsonSerializable(typeof(GetCoolingWarningsResponse))]

// NP50 device state surface
[JsonSerializable(typeof(Qos.Service.Routes.Np50StateResponse))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50State))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50HubInfo))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50Port))]
[JsonSerializable(typeof(List<Qos.Service.Peripherals.Hyte.Np50.Np50Port>))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50FanDevice))]
[JsonSerializable(typeof(List<Qos.Service.Peripherals.Hyte.Np50.Np50FanDevice>))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50WarningDetail))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Hyte.Np50.Np50PortWarning))]
[JsonSerializable(typeof(Qos.Service.Routes.Np50LightingRequest))]
[JsonSerializable(typeof(Qos.Service.Routes.Np50LedColor))]
[JsonSerializable(typeof(List<Qos.Service.Routes.Np50LedColor>))]
[JsonSerializable(typeof(Qos.Service.Routes.Np50FirmwareResponse))]

// Fan control
[JsonSerializable(typeof(FanChannel))]
[JsonSerializable(typeof(List<FanChannel>))]
[JsonSerializable(typeof(TemperatureSource))]
[JsonSerializable(typeof(List<TemperatureSource>))]
[JsonSerializable(typeof(GetFanChannelsResponse))]
[JsonSerializable(typeof(GetTemperatureSourcesResponse))]
[JsonSerializable(typeof(SetFanSpeedBody))]
[JsonSerializable(typeof(SetFanNameBody))]
[JsonSerializable(typeof(SetFanSpeedResponse))]
[JsonSerializable(typeof(CurveOutputState))]
[JsonSerializable(typeof(CurveCalculation))]
[JsonSerializable(typeof(CurveCalculationsFrame))]
[JsonSerializable(typeof(FanProfile))]
[JsonSerializable(typeof(List<FanProfile>))]
[JsonSerializable(typeof(GetProfilesResponse))]
[JsonSerializable(typeof(ApplyProfileResponse))]
[JsonSerializable(typeof(StartupModeBody))]
[JsonSerializable(typeof(StartupModeDto))]
[JsonSerializable(typeof(FanCalibration))]
[JsonSerializable(typeof(List<FanCalibration>))]
[JsonSerializable(typeof(FanCalibrationPoint))]
[JsonSerializable(typeof(List<FanCalibrationPoint>))]
[JsonSerializable(typeof(FanCalibrationProgress))]
[JsonSerializable(typeof(StartCalibrationBody))]
[JsonSerializable(typeof(CoolingStatusResponse))]
[JsonSerializable(typeof(LightingStatusResponse))]
[JsonSerializable(typeof(List<float>))]
[JsonSerializable(typeof(Qos.Service.Models.Lighting.MusicReactiveBody))]
[JsonSerializable(typeof(CalibrationStartResponse))]
[JsonSerializable(typeof(GetCalibrationsResponse))]

// Profiles
[JsonSerializable(typeof(Qos.Service.Persistence.ProfileManifest))]
[JsonSerializable(typeof(Qos.Service.Persistence.ProfileEntry))]
[JsonSerializable(typeof(List<Qos.Service.Persistence.ProfileEntry>))]
[JsonSerializable(typeof(Qos.Service.Persistence.UiSettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.UiSettingsPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.ThemeSettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.MonitoringSettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.PanelSettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.OverlaySettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.PanelLayoutsDefaults))]
[JsonSerializable(typeof(Qos.Service.Persistence.PanelLayoutDefault))]
[JsonSerializable(typeof(Qos.Service.Persistence.PanelLayoutWidget))]
[JsonSerializable(typeof(List<Qos.Service.Persistence.PanelLayoutWidget>))]
[JsonSerializable(typeof(Qos.Service.Persistence.Preferences))]
[JsonSerializable(typeof(Qos.Service.Persistence.CoolingPrefs))]
[JsonSerializable(typeof(Qos.Service.Persistence.PreferencesPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.ThemeSettingsPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.PanelSettingsPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.OverlaySettingsPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.MonitoringSettingsPatch))]
[JsonSerializable(typeof(Qos.Service.Persistence.CoolingPrefsPatch))]

// Panel widget engine - per-device records, layouts, control-state push frames.
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelLayoutDto))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPageDto))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelWidgetDto))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDockDto))]
[JsonSerializable(typeof(List<Qos.Service.Models.Panel.PanelPageDto>))]
[JsonSerializable(typeof(List<Qos.Service.Models.Panel.PanelWidgetDto>))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDeviceRecord))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDeviceCapabilities))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDevicePatch))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDeviceCreateBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDeviceListResponse))]
[JsonSerializable(typeof(List<Qos.Service.Models.Panel.PanelDeviceRecord>))]
[JsonSerializable(typeof(Dictionary<string, Qos.Service.Models.Panel.PanelDeviceRecord>))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PrefsChangedFrame))]

// Desktop widgets - floating panel widgets on the Windows desktop.
[JsonSerializable(typeof(Qos.Service.Models.Panel.OverlayWidgetDto))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.OverlayWidgetCreateBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.OverlayWidgetPatch))]
[JsonSerializable(typeof(List<Qos.Service.Models.Panel.OverlayWidgetDto>))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.LightingChangedFrame))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.CoolingChangedFrame))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.CoolingWarningsChangedFrame))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.DevicesChangedFrame))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelDeviceChangedFrame))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairQrResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneClaimBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneClaimResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneServiceInfoResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneSessionNameBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelHostNameBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelHostNameResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneSessionsResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhoneSessionDto))]
[JsonSerializable(typeof(List<Qos.Service.Models.Panel.PanelPhoneSessionDto>))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelStatusResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.RemoteControlStateResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.RemoteControlToggleRequest))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PairBroadcastStateResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PairBroadcastSetRequest))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PairWifiInitiateRequest))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeStartResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeSubmitBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeSubmitResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeConfirmBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeConfirmResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeHostDecisionBody))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeHostDecisionResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.PanelPhonePairCodeRequestFrame))]

// Displays (system monitors: brightness + DDC/CI VCP)
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayDto))]
[JsonSerializable(typeof(List<Qos.Service.Models.Displays.DisplayDto>))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayCapabilitiesDto))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayBrightnessControlDto))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayBrightnessWritePolicy))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayBrightnessDto))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayBrightnessParams))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayVcpDto))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayVcpParams))]
[JsonSerializable(typeof(Qos.Service.Models.Displays.DisplayListResponse))]

// Weather
[JsonSerializable(typeof(Qos.Service.Models.Weather.WeatherSnapshot))]
[JsonSerializable(typeof(Qos.Service.Models.Weather.WeatherHourlyForecast))]
[JsonSerializable(typeof(List<Qos.Service.Models.Weather.WeatherHourlyForecast>))]
[JsonSerializable(typeof(Qos.Service.Models.Weather.WeatherDailyForecast))]
[JsonSerializable(typeof(List<Qos.Service.Models.Weather.WeatherDailyForecast>))]
[JsonSerializable(typeof(Qos.Service.Platform.Weather.IpLocation))]
[JsonSerializable(typeof(Qos.Service.Platform.Weather.OpenMeteoResponse))]
[JsonSerializable(typeof(Qos.Service.Platform.Weather.OpenMeteoCurrent))]
[JsonSerializable(typeof(Qos.Service.Platform.Weather.OpenMeteoHourly))]
[JsonSerializable(typeof(Qos.Service.Platform.Weather.OpenMeteoDaily))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.ListProfilesResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.ProfileResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.SwitchProfileResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.CreateProfileBody))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.RenameProfileBody))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.ProfileExport))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.SharingResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.SetPrimaryBody))]
[JsonSerializable(typeof(Qos.Service.Models.Profiles.SetCategorySharedBody))]

// Media library
[JsonSerializable(typeof(Qos.Service.Models.Media.MediaItem))]
[JsonSerializable(typeof(List<Qos.Service.Models.Media.MediaItem>))]
[JsonSerializable(typeof(Qos.Service.Models.Media.MediaLibraryResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Media.MediaImportResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Media.MediaPlayResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Media.MediaCurrentResponse))]

// Lighting
[JsonSerializable(typeof(AudioStateSnapshot))]
[JsonSerializable(typeof(ShaderSourceResponse))]
[JsonSerializable(typeof(CurrentSyncResponse))]
[JsonSerializable(typeof(SetFrameRateBody))]
[JsonSerializable(typeof(SetScaleRatioBody))]
[JsonSerializable(typeof(BrightnessScale))]
[JsonSerializable(typeof(Qos.Service.Models.Lighting.GlobalBrightnessBody))]
[JsonSerializable(typeof(SpeedScale))]
[JsonSerializable(typeof(StaticHeadlessStart))]
[JsonSerializable(typeof(AnimateHeadlessStart))]
[JsonSerializable(typeof(SetAnimateTemplatesBody))]
[JsonSerializable(typeof(ShaderParam))]
[JsonSerializable(typeof(List<ShaderParam>))]
[JsonSerializable(typeof(Qos.Service.Persistence.AnimateSettings))]
[JsonSerializable(typeof(Qos.Service.Persistence.AnimateEffectState))]
[JsonSerializable(typeof(Qos.Service.Persistence.AnimateEffectTemplates))]
[JsonSerializable(typeof(Qos.Service.Persistence.StaticColorSettings))]
[JsonSerializable(typeof(Dictionary<string, Qos.Service.Persistence.AnimateEffectState>))]
[JsonSerializable(typeof(Dictionary<string, Qos.Service.Persistence.AnimateEffectTemplates>))]
[JsonSerializable(typeof(List<Qos.Service.Persistence.AnimateEffectState>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, float>>))]
[JsonSerializable(typeof(MusicHeadlessStart))]
[JsonSerializable(typeof(ScreenHeadlessStart))]
[JsonSerializable(typeof(ScreenSyncOptions))]
[JsonSerializable(typeof(ScreenSyncMonitor))]
[JsonSerializable(typeof(List<ScreenSyncMonitor>))]
[JsonSerializable(typeof(GifHeadlessStart))]
[JsonSerializable(typeof(SetHeadlessStreaming))]
[JsonSerializable(typeof(Qos.Service.Models.Lighting.PostProcessBody))]
[JsonSerializable(typeof(Qos.Service.Persistence.PostProcessSettings))]

// OBS
[JsonSerializable(typeof(ObsConfigResponse))]
[JsonSerializable(typeof(ObsConfigBody))]
[JsonSerializable(typeof(ObsStatusResponse))]
[JsonSerializable(typeof(ObsScene))]
[JsonSerializable(typeof(List<ObsScene>))]
[JsonSerializable(typeof(ObsSetSceneBody))]

// Steam
[JsonSerializable(typeof(SteamConfigResponse))]
[JsonSerializable(typeof(SteamConfigBody))]
[JsonSerializable(typeof(SteamStatusResponse))]
[JsonSerializable(typeof(SteamProfileResponse))]
[JsonSerializable(typeof(SteamPlayerSummary))]
[JsonSerializable(typeof(SteamRecentGame))]
[JsonSerializable(typeof(List<SteamRecentGame>))]
[JsonSerializable(typeof(SteamOwnedGame))]
[JsonSerializable(typeof(List<SteamOwnedGame>))]
[JsonSerializable(typeof(SteamFriendSummary))]
[JsonSerializable(typeof(List<SteamFriendSummary>))]
[JsonSerializable(typeof(SteamAchievement))]
[JsonSerializable(typeof(List<SteamAchievement>))]

// Discord
[JsonSerializable(typeof(DiscordConfigResponse))]
[JsonSerializable(typeof(DiscordConfigBody))]
[JsonSerializable(typeof(DiscordStatusResponse))]
[JsonSerializable(typeof(DiscordUser))]
[JsonSerializable(typeof(DiscordGuild))]
[JsonSerializable(typeof(List<DiscordGuild>))]
[JsonSerializable(typeof(DiscordVoiceState))]
[JsonSerializable(typeof(DiscordVoiceParticipant))]
[JsonSerializable(typeof(List<DiscordVoiceParticipant>))]
[JsonSerializable(typeof(DiscordNotification))]
[JsonSerializable(typeof(List<DiscordNotification>))]
[JsonSerializable(typeof(DiscordOpenBody))]
[JsonSerializable(typeof(DiscordVoiceToggleBody))]

// Devices
[JsonSerializable(typeof(Qos.Service.Persistence.DeviceLayout))]
[JsonSerializable(typeof(SaveDeviceLayoutBody))]
[JsonSerializable(typeof(DeviceListItem))]
[JsonSerializable(typeof(List<DeviceListItem>))]
[JsonSerializable(typeof(UsbDeviceDetail))]
[JsonSerializable(typeof(List<UsbDeviceDetail>))]

// Peripherals (third-party mice/keyboards/headsets with protocol support)
[JsonSerializable(typeof(PeripheralDto))]
[JsonSerializable(typeof(List<PeripheralDto>))]
[JsonSerializable(typeof(GetPeripheralsResponse))]
[JsonSerializable(typeof(SupportedDeviceDto))]
[JsonSerializable(typeof(List<SupportedDeviceDto>))]
[JsonSerializable(typeof(GetSupportedDevicesResponse))]
[JsonSerializable(typeof(Qos.Service.Peripherals.OpenRgbSupportedDevicesFile))]
[JsonSerializable(typeof(Qos.Service.Peripherals.OpenRgbSupportedDeviceEntry))]
[JsonSerializable(typeof(List<Qos.Service.Peripherals.OpenRgbSupportedDeviceEntry>))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Protocols.Razer.RazerMouseSpec))]
[JsonSerializable(typeof(Qos.Service.Peripherals.Protocols.Razer.RazerMouseSpecEntry))]
[JsonSerializable(typeof(List<Qos.Service.Peripherals.Protocols.Razer.RazerMouseSpecEntry>))]
[JsonSerializable(typeof(DpiState))]
[JsonSerializable(typeof(PollingState))]
[JsonSerializable(typeof(BatteryState))]
[JsonSerializable(typeof(SleepState))]
[JsonSerializable(typeof(ToggleState))]
[JsonSerializable(typeof(SetDpiBody))]
[JsonSerializable(typeof(SetPollingBody))]
[JsonSerializable(typeof(SetSleepBody))]
[JsonSerializable(typeof(SetToggleBody))]
[JsonSerializable(typeof(IsDeviceConnectedResponse))]
[JsonSerializable(typeof(FirmwareVersionResponse))]
[JsonSerializable(typeof(GetCnvsSettingsResponse))]
[JsonSerializable(typeof(SetCnvsSettingsBody))]
[JsonSerializable(typeof(UpdateBody))]
[JsonSerializable(typeof(CheckForUpdateResponse))]
[JsonSerializable(typeof(UpdateResponse))]
[JsonSerializable(typeof(GetMotherboardLEDsResponse))]
[JsonSerializable(typeof(SetMotherboardLEDsBody))]
[JsonSerializable(typeof(CheckFirmwareFunctionBody))]
[JsonSerializable(typeof(CheckFirmwareFunctionResponse))]
[JsonSerializable(typeof(GetLightingDevicesResponse))]
[JsonSerializable(typeof(SetDisabledLedsBody))]
[JsonSerializable(typeof(SetLightingDevicePowerBody))]
[JsonSerializable(typeof(SetLightingDeviceBrightness))]
[JsonSerializable(typeof(SetLightingDeviceHue))]
[JsonSerializable(typeof(SetLightingDeviceSaturation))]
[JsonSerializable(typeof(SetZoneLedCountBody))]
[JsonSerializable(typeof(IdentifyLightingDeviceBody))]
[JsonSerializable(typeof(LedMapResponse))]
[JsonSerializable(typeof(LedMapEntry))]
[JsonSerializable(typeof(List<LedMapEntry>))]
[JsonSerializable(typeof(SaveLedMapBody))]
[JsonSerializable(typeof(Qos.Service.Persistence.LedPositionOverride))]
[JsonSerializable(typeof(List<Qos.Service.Persistence.LedPositionOverride>))]
[JsonSerializable(typeof(LedHighlightBody))]
[JsonSerializable(typeof(LedTestPatternBody))]

// Keeb
[JsonSerializable(typeof(KeyboardState))]
[JsonSerializable(typeof(GetKeebSettingsResponse))]
[JsonSerializable(typeof(GetRotaryFunctionsResponse))]
[JsonSerializable(typeof(SetRotaryWheelsBody))]
[JsonSerializable(typeof(SetRotarySensitivityBody))]
[JsonSerializable(typeof(SetFirmwareLightingBody))]
[JsonSerializable(typeof(SetPassiveLightingBody))]
[JsonSerializable(typeof(SetGameModeBody))]
[JsonSerializable(typeof(SetLayerKeyBody))]
[JsonSerializable(typeof(GetMacroResponse))]
[JsonSerializable(typeof(SetMacroBody))]
[JsonSerializable(typeof(InputterBody))]

// Displays
[JsonSerializable(typeof(Y70StatusResponse))]
[JsonSerializable(typeof(Y70RotationParams))]
[JsonSerializable(typeof(Y70BrightnessResponse))]
[JsonSerializable(typeof(Y70BrightnessParams))]
[JsonSerializable(typeof(Y70ToggleScreenResponse))]
[JsonSerializable(typeof(Y70ToggleScreenParams))]
[JsonSerializable(typeof(Y70IsRotatedResponse))]
[JsonSerializable(typeof(GetSerialNumberResponse))]
[JsonSerializable(typeof(GetQSeriesTimeResponse))]

// Activity
[JsonSerializable(typeof(FocusSession))]
[JsonSerializable(typeof(AppUsage))]
[JsonSerializable(typeof(List<AppUsage>))]
[JsonSerializable(typeof(IReadOnlyList<AppUsage>))]
[JsonSerializable(typeof(DayBreakdown))]
[JsonSerializable(typeof(DayTotal))]
[JsonSerializable(typeof(List<DayTotal>))]
[JsonSerializable(typeof(IReadOnlyList<DayTotal>))]
[JsonSerializable(typeof(AppHistory))]
[JsonSerializable(typeof(List<long>))]
[JsonSerializable(typeof(DeleteResponse))]
[JsonSerializable(typeof(TrackingStatus))]
[JsonSerializable(typeof(SetTrackingBody))]
[JsonSerializable(typeof(NetworkProcessInfo))]
[JsonSerializable(typeof(List<NetworkProcessInfo>))]
[JsonSerializable(typeof(IEnumerable<NetworkProcessInfo>))]
[JsonSerializable(typeof(List<Detected>))]
[JsonSerializable(typeof(KillAppResponse))]
[JsonSerializable(typeof(Dictionary<string, MediaSession>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, MediaSession>))]
[JsonSerializable(typeof(MediaControlBody))]
[JsonSerializable(typeof(VolumeState))]
[JsonSerializable(typeof(SetVolumeBody))]
[JsonSerializable(typeof(SetMutedBody))]
[JsonSerializable(typeof(GetAllShortcutsResponse))]
[JsonSerializable(typeof(GetShortcutResponse))]
[JsonSerializable(typeof(MusicResult))]

// Lifecycle
[JsonSerializable(typeof(SetWillStartParams))]
[JsonSerializable(typeof(WillStartResponse))]
[JsonSerializable(typeof(PawnIoStatus))]

// Processes
[JsonSerializable(typeof(ProcessInfo))]
[JsonSerializable(typeof(List<ProcessInfo>))]
[JsonSerializable(typeof(IReadOnlyList<ProcessInfo>))]
[JsonSerializable(typeof(IEnumerable<ProcessInfo>))]

// Activity broadcaster (WebSocket push frames)
[JsonSerializable(typeof(ProcessFrame))]
[JsonSerializable(typeof(NetworkFrame))]
[JsonSerializable(typeof(ScreenTimeFrame))]

// Monitoring (multiplexed WebSocket composite frame)
[JsonSerializable(typeof(Qos.Service.Models.Monitoring.MonitoringFrame))]

// Benchmarks
[JsonSerializable(typeof(BenchmarkSubScore))]
[JsonSerializable(typeof(List<BenchmarkSubScore>))]
[JsonSerializable(typeof(BenchmarkPhaseProgress))]
[JsonSerializable(typeof(BenchmarkProgressFrame))]
[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(HardwareIdentity))]
[JsonSerializable(typeof(StartBenchmarkResponse))]
[JsonSerializable(typeof(StartBenchmarkBody))]

[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(List<string>))]

// Helper IPC transport + per-domain payloads. Grouped by domain for
// readability. Types themselves live under src/Helper/ and
// src/Helper/Domains/; this file is the single AOT JSON-registration
// surface (the JsonSourceGenerator does not deal well with partial
// declarations of JsonSerializerContext spread across many files).
[JsonSerializable(typeof(HelperEnvelope))]
[JsonSerializable(typeof(HelperResult))]
[JsonSerializable(typeof(HelperHello))]
#if WINDOWS
// Helper IPC payload types live under `#if WINDOWS` so they only register
// where the helper actually runs. Same gate as the per-domain files under
// src/Helper/Domains/; without this the non-Windows TFM does not compile
// because Qos.Service.Helper.Domains is absent there.
// Lifecycle
[JsonSerializable(typeof(Qos.Service.Helper.Domains.HelperShutdownPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.OverlayPrefsChangedPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ServiceRequestStopPayload))]
// Tray
[JsonSerializable(typeof(Qos.Service.Helper.Domains.TraySetVisiblePayload))]
// Screen-time
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ScreenTimeSessionPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ScreenTimeFocusPayload))]
// Media
[JsonSerializable(typeof(Qos.Service.Helper.Domains.MediaSnapshotPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.MediaControlPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.AlbumArtRequest))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.AlbumArtResult))]
// Brightness
[JsonSerializable(typeof(Qos.Service.Helper.Domains.DisplayBrightnessRequest))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.StringResult))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.NullableIntResult))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.DisplayVcpResult))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.BoolResult))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.DisplayListResult))]
// Orientation
[JsonSerializable(typeof(Qos.Service.Helper.Domains.DisplayOrientationRequest))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.DisplayOrientationResult))]
// Monitors
[JsonSerializable(typeof(Qos.Service.Helper.Domains.MonitorEnumerateRequest))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.MonitorListResult))]
// Screen mirror
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ScreenMirrorStartPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ScreenMirrorStopPayload))]
[JsonSerializable(typeof(Qos.Service.Helper.Domains.ScreenMirrorFramePayload))]
#endif

// Widgets - declarative runtime (qos.widget/2 schema)
[JsonSerializable(typeof(WidgetManifest))]
[JsonSerializable(typeof(WidgetManifestAuthor))]
[JsonSerializable(typeof(WidgetManifestViewport))]
[JsonSerializable(typeof(WidgetManifestCapabilities))]
[JsonSerializable(typeof(WidgetManifestSettingEntry))]
[JsonSerializable(typeof(List<WidgetManifestSettingEntry>))]
[JsonSerializable(typeof(WidgetManifestDataSource))]
[JsonSerializable(typeof(Dictionary<string, WidgetManifestDataSource>))]
[JsonSerializable(typeof(WidgetClockSource))]
[JsonSerializable(typeof(WidgetHostSource))]
[JsonSerializable(typeof(WidgetActionAckDto))]
[JsonSerializable(typeof(ScreentimeHistoryEntryDto))]
[JsonSerializable(typeof(ScreentimeFocusDto))]
[JsonSerializable(typeof(ScreentimeTodayDto))]
[JsonSerializable(typeof(List<ScreentimeHistoryEntryDto>))]
[JsonSerializable(typeof(WidgetManifestFont))]
[JsonSerializable(typeof(List<WidgetManifestFont>))]
[JsonSerializable(typeof(WidgetInstalledListing))]
[JsonSerializable(typeof(WidgetInstalledListingResponse))]
[JsonSerializable(typeof(List<WidgetInstalledListing>))]
[JsonSerializable(typeof(WidgetCatalogEntry))]
[JsonSerializable(typeof(WidgetCatalogResponse))]
[JsonSerializable(typeof(List<WidgetCatalogEntry>))]
[JsonSerializable(typeof(WidgetInstallRequest))]
[JsonSerializable(typeof(WidgetInstallResponse))]
[JsonSerializable(typeof(WidgetCodeSessionResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.OpenUrlRequest))]
[JsonSerializable(typeof(Qos.Service.Models.Panel.ShortcutRequest))]
// Widgets - settings
[JsonSerializable(typeof(WidgetSettingsDocument))]
[JsonSerializable(typeof(WidgetSettingsPatch))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
// Widgets - proxy
[JsonSerializable(typeof(WidgetProxyRequest))]
[JsonSerializable(typeof(WidgetProxyResponse))]
[JsonSerializable(typeof(WidgetDispatchRequest))]
[JsonSerializable(typeof(WidgetDispatchResponse))]

// Conflict warning system - sidebar alarm for competing third-party apps.
[JsonSerializable(typeof(Qos.Service.Models.Conflicts.DetectedConflict))]
[JsonSerializable(typeof(List<Qos.Service.Models.Conflicts.DetectedConflict>))]
[JsonSerializable(typeof(Qos.Service.Models.Conflicts.GetConflictsResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Conflicts.KillConflictBody))]
[JsonSerializable(typeof(Qos.Service.Models.Conflicts.KillConflictResponse))]
[JsonSerializable(typeof(Qos.Service.Models.Conflicts.ConflictsFrame))]

// Install-time defaults table - read once on startup from the embedded
// data/install-defaults.json resource. Nested POCOs are picked up
// transitively by the source generator.
[JsonSerializable(typeof(Qos.Service.Defaults.InstallDefaultsDocument))]

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class AppJsonContext : JsonSerializerContext;
