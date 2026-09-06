using System;
using Nexus.Service.Integrations.HomeAssistant;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sockets;

/// <summary>
/// Small fan-out helpers for the four panel-related multiplex topics.
/// Routes call these at the end of any mutation; subscribers refetch the
/// canonical resource on receive (no payload diffing).
/// </summary>
public static class PanelTopics
{
    public const string Prefs = "prefs";
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Volume = "volume";
    /// <summary>
    /// Per-app mixer strips. Carries the strips inline rather than a refetch
    /// revision: while a mixer is open this fires at meter rate, and a revision
    /// frame would turn every peak sample into an HTTP GET.
    /// </summary>
    public const string AudioMixer = "audio/mixer";
    /// <summary>Spectrum / beat snapshots for client-rendered audio shaders; subscribing also demands capture (LightingProvider.SetAudioCaptureDemand).</summary>
    public const string Audio = "audio";
    /// <summary>
    /// Gallery sources changed (reference added/removed, upload). Subscribers
    /// refetch GET /gallery/items.
    /// </summary>
    public const string Gallery = "gallery";
    public const string CoolingWarnings = "cooling/warnings";
    public const string PanelDevice = "panel/device";
    /// <summary>
    /// Display topology or monitor-panel assignment changed. Subscribers
    /// refetch GET /displays/topology.
    /// </summary>
    public const string Displays = "displays";
    /// <summary>
    /// Manual pair-code lifecycle. Dashboard subscribes while the Pair
    /// Remote sheet is open; payload kinds are "request" (phone submitted
    /// the active code) and "cancelled" (expired / denied / superseded).
    /// </summary>
    public const string PairCodeRequest = "panel/phone/pair-code/request";
    /// <summary>
    /// Host network address changed (VPN toggle, Wi-Fi↔wired switch, DHCP
    /// renew). A displayed pairing QR embeds the LAN IP picked at mint time, so
    /// subscribers re-fetch the QR for the current address instead of waiting
    /// out its TTL.
    /// </summary>
    public const string PairQrRefresh = "panel/phone/pair-qr/refresh";
    /// <summary>
    /// OS accent colour changed (Linux only - the service watches the XDG
    /// portal and pushes the new accent so the dashboard tracks it live).
    /// </summary>
    public const string SystemAccent = "system/accent";
    /// <summary>
    /// Phone→PC transfer landed (file saved to the inbox / clipboard applied).
    /// Carries the event payload directly - there is no canonical resource to
    /// refetch.
    /// </summary>
    public const string Transfer = "transfer";

    /// <summary>
    /// OTA update status changed (an update became available or finished
    /// staging). Subscribers refetch GET /update/status so the sidebar banner
    /// shows on detection instead of waiting out the dashboard's 60s poll.
    /// </summary>
    public const string Update = "update";

    /// <summary>
    /// The active focus mode changed, or the mode list was edited.
    /// Subscribers refetch GET /api/focus so the top bar chip tracks it
    /// without polling.
    /// </summary>
    public const string Focus = "focus";
    /// <summary>
    /// The active cloud account changed (login, logout, switch, or a recovery
    /// approved by the service's own poll loop while no page was watching).
    /// Subscribers refetch GET /cloud/accounts.
    /// </summary>
    public const string CloudAccounts = "cloud/accounts";

    public static void BroadcastCloudAccounts(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(CloudAccounts))
            return;
        var frame = new Models.Cloud.CloudAccountsChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(CloudAccounts, frame, AppJsonContext.Default.CloudAccountsChangedFrame);
        _ = hub.BroadcastTopicAsync(CloudAccounts, env);
    }

    public static void BroadcastFocus(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Focus))
            return;
        var frame = new Models.Focus.FocusChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Focus, frame, AppJsonContext.Default.FocusChangedFrame);
        _ = hub.BroadcastTopicAsync(Focus, env);
    }

    public static void BroadcastUpdate(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Update))
            return;
        var frame = new Models.Update.UpdateStatusChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Update, frame, AppJsonContext.Default.UpdateStatusChangedFrame);
        _ = hub.BroadcastTopicAsync(Update, env);
    }

    public static void BroadcastPrefs(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Prefs))
            return;
        var frame = new PrefsChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Prefs, frame, AppJsonContext.Default.PrefsChangedFrame);
        _ = hub.BroadcastTopicAsync(Prefs, env);
    }

    public static void BroadcastLighting(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Lighting))
            return;
        var frame = new LightingChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Lighting, frame, AppJsonContext.Default.LightingChangedFrame);
        _ = hub.BroadcastTopicAsync(Lighting, env);
    }

    /// <summary>
    /// Community mapping auto-applied to a first-seen device. Payload rides
    /// the frame directly (toast + one-click undo); a regular lighting
    /// broadcast accompanies it for state refetch.
    /// </summary>
    public const string MappingApplied = "lighting/mapping-applied";

    public static void BroadcastMappingApplied(MultiplexHub hub, MappingAutoAppliedFrame frame)
    {
        if (!hub.TopicHasSubscribers(MappingApplied))
            return;
        frame.Revision = Now();
        var env = WsEnvelope.Build(MappingApplied, frame, AppJsonContext.Default.MappingAutoAppliedFrame);
        _ = hub.BroadcastTopicAsync(MappingApplied, env);
    }

    public static void BroadcastCooling(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Cooling))
            return;
        var frame = new CoolingChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Cooling, frame, AppJsonContext.Default.CoolingChangedFrame);
        _ = hub.BroadcastTopicAsync(Cooling, env);
    }

    public static void BroadcastVolume(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Volume))
            return;
        var frame = new VolumeChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Volume, frame, AppJsonContext.Default.VolumeChangedFrame);
        _ = hub.BroadcastTopicAsync(Volume, env);
    }

    public static void BroadcastGallery(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Gallery))
            return;
        var frame = new GalleryChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Gallery, frame, AppJsonContext.Default.GalleryChangedFrame);
        _ = hub.BroadcastTopicAsync(Gallery, env);
    }

    /// <summary>
    /// The console user's desktop wallpaper changed. Panels rendering the
    /// wallpaper background refetch GET /panel/desktop-wallpaper.
    /// </summary>
    public const string DesktopWallpaper = "desktopWallpaper";

    public static void BroadcastDesktopWallpaper(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(DesktopWallpaper))
            return;
        var frame = new DesktopWallpaperChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(DesktopWallpaper, frame, AppJsonContext.Default.DesktopWallpaperChangedFrame);
        _ = hub.BroadcastTopicAsync(DesktopWallpaper, env);
    }

    /// <summary>
    /// Lighting media library mutated (item imported, committed, or deleted).
    /// Subscribers refetch GET /media/library.
    /// </summary>
    public const string MediaLibrary = "mediaLibrary";

    public static void BroadcastMediaLibrary(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(MediaLibrary))
            return;
        var frame = new MediaLibraryChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(MediaLibrary, frame, AppJsonContext.Default.MediaLibraryChangedFrame);
        _ = hub.BroadcastTopicAsync(MediaLibrary, env);
    }

    /// <summary>
    /// Broadcast a cooling-warnings-changed notification. Callers (the NP50
    /// heartbeat worker and any future warning producers) invoke this when
    /// the active warning set transitions. Subscribers refetch
    /// <c>GET /cooling/warnings</c>; <paramref name="deviceId"/> lets the UI
    /// scope which device's warnings to re-render.
    /// </summary>
    public static void BroadcastCoolingWarnings(MultiplexHub hub, string deviceId)
    {
        if (!hub.TopicHasSubscribers(CoolingWarnings))
            return;
        var frame = new CoolingWarningsChangedFrame { Revision = Now(), DeviceId = deviceId };
        var env = WsEnvelope.Build(CoolingWarnings, frame, AppJsonContext.Default.CoolingWarningsChangedFrame);
        _ = hub.BroadcastTopicAsync(CoolingWarnings, env);
    }

    public static void BroadcastSystemAccent(MultiplexHub hub, string hex)
    {
        if (!hub.TopicHasSubscribers(SystemAccent))
            return;
        var frame = new SystemAccentFrame { Hex = hex };
        var env = WsEnvelope.Build(SystemAccent, frame, AppJsonContext.Default.SystemAccentFrame);
        _ = hub.BroadcastTopicAsync(SystemAccent, env);
    }

    public static void BroadcastTransfer(MultiplexHub hub, Models.Transfer.TransferReceivedFrame frame)
    {
        if (!hub.TopicHasSubscribers(Transfer))
            return;
        frame.Revision = Now();
        var env = WsEnvelope.Build(Transfer, frame, AppJsonContext.Default.TransferReceivedFrame);
        _ = hub.BroadcastTopicAsync(Transfer, env);
    }

    public static void BroadcastPairQrRefresh(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(PairQrRefresh))
            return;
        var frame = new PairQrRefreshFrame { Revision = Now() };
        var env = WsEnvelope.Build(PairQrRefresh, frame, AppJsonContext.Default.PairQrRefreshFrame);
        _ = hub.BroadcastTopicAsync(PairQrRefresh, env);
    }

    public static void BroadcastDisplays(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Displays))
            return;
        var frame = new Models.Displays.DisplaysChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Displays, frame, AppJsonContext.Default.DisplaysChangedFrame);
        _ = hub.BroadcastTopicAsync(Displays, env);
    }

    public static void BroadcastPanelDevice(MultiplexHub hub, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !hub.TopicHasSubscribers(PanelDevice))
            return;
        var frame = new PanelDeviceChangedFrame { Revision = Now(), DeviceId = deviceId };
        var env = WsEnvelope.Build(PanelDevice, frame, AppJsonContext.Default.PanelDeviceChangedFrame);
        _ = hub.BroadcastTopicAsync(PanelDevice, env);
    }

    public static void BroadcastPairCodeRequest(MultiplexHub hub, PanelPhonePairCodeRequestFrame frame)
    {
        if (!hub.TopicHasSubscribers(PairCodeRequest))
            return;
        _ = hub.BroadcastTopicAsync(PairCodeRequest, BuildPairCodeRequestEnvelope(frame));
    }

    /// <summary>
    /// Build the wire envelope for a pair-code/request frame. Shared by the
    /// live broadcast above and the snapshot provider in
    /// <see cref="Nexus.Service.Panel.PanelPhonePairingService"/>, which
    /// replays the currently-pending request to a dashboard that connects
    /// mid-handshake (e.g. one opened from the tray pairing notification
    /// after the one-shot live broadcast already fired).
    /// </summary>
    public static ReadOnlyMemory<byte> BuildPairCodeRequestEnvelope(PanelPhonePairCodeRequestFrame frame)
        => WsEnvelope.Build(PairCodeRequest, frame, AppJsonContext.Default.PanelPhonePairCodeRequestFrame);

    /// <summary>
    /// Home Assistant entity cache changed. Subscribers refetch
    /// GET /home-assistant/entities.
    /// </summary>
    public const string HomeAssistant = "homeAssistant";

    public static void BroadcastHomeAssistant(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(HomeAssistant))
        {
            return;
        }
        var frame = new HomeAssistantChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(HomeAssistant, frame, AppJsonContext.Default.HomeAssistantChangedFrame);
        _ = hub.BroadcastTopicAsync(HomeAssistant, env);
    }

    /// <summary>
    /// Physical Stream Deck state changed. Unlike the bare-revision topics
    /// above, this frame carries a discriminator (<c>Kind</c>: "decks" |
    /// "config" | "nav" | "press") plus whatever fields that kind needs, so a
    /// subscriber can react to nav/press live instead of only refetching.
    /// </summary>
    public const string StreamDeck = "streamdeck";

    public static void BroadcastStreamDeck(MultiplexHub hub, Nexus.Service.Models.Peripherals.StreamDeck.StreamDeckChangedFrame frame)
    {
        if (!hub.TopicHasSubscribers(StreamDeck))
        {
            return;
        }
        frame.Revision = Now();
        var env = WsEnvelope.Build(StreamDeck, frame, AppJsonContext.Default.StreamDeckChangedFrame);
        _ = hub.BroadcastTopicAsync(StreamDeck, env);
    }

    /// <summary>
    /// Live per-key JPEG render for a monitoring/weather Stream Deck tile,
    /// the same pixels pushed to the physical key. The Customize tab's editor
    /// preview subscribes while open; no snapshot provider is registered, so
    /// StreamDeckConnectionWorker re-broadcasts every visible tile itself on
    /// the topic's 0-&gt;1 subscriber transition.
    /// </summary>
    public const string StreamDeckTiles = "streamdeckTiles";

    public static void BroadcastStreamDeckTile(MultiplexHub hub, Nexus.Service.Models.Peripherals.StreamDeck.StreamDeckTileFrame frame)
    {
        if (!hub.TopicHasSubscribers(StreamDeckTiles))
        {
            return;
        }
        var env = WsEnvelope.Build(StreamDeckTiles, frame, AppJsonContext.Default.StreamDeckTileFrame);
        _ = hub.BroadcastTopicAsync(StreamDeckTiles, env);
    }

    /// <summary>
    /// Local AI assistant runtime/model progress (managed Ollama install,
    /// download bytes, active model pull). Subscribers use the frame directly
    /// for live progress bars; GET /ai/assistant/status is the canonical
    /// resource for everything else.
    /// </summary>
    public const string AiAssistant = "aiAssistant";

    public static void BroadcastAiAssistant(MultiplexHub hub, Nexus.Service.Models.Mcp.AssistantProgressFrame frame)
    {
        if (!hub.TopicHasSubscribers(AiAssistant))
            return;
        frame.Revision = Now();
        var env = WsEnvelope.Build(AiAssistant, frame, AppJsonContext.Default.AssistantProgressFrame);
        _ = hub.BroadcastTopicAsync(AiAssistant, env);
    }

    /// <summary>
    /// One recorded 1Hz metrics sample, pushed by MetricsSampler through
    /// MonitoringHistoryTailBroadcaster. Same MetricsHistoryResponse shape as
    /// GET /monitoring/history's tail poll, decimated to one point per series.
    /// </summary>
    public const string MonitoringHistoryTail = "monitoring/history-tail";

    /// <summary>
    /// A monitoring timeline event was appended (USB attach/detach, app-open,
    /// UAC escalation, or a custom POST /monitoring/events entry). Carries one
    /// MonitoringEventDto, the same shape GET /monitoring/events returns.
    /// </summary>
    public const string MonitoringEvents = "monitoring/events";

    /// <summary>
    /// A privacy-capability access session was opened or closed. Carries one
    /// PrivacySessionWire, the same shape an entry in GET /monitoring/privacy's
    /// sessions array has.
    /// </summary>
    public const string MonitoringPrivacy = "monitoring/privacy";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
