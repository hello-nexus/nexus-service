using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Persistence;
using Nexus.Service.Security;
using Nexus.Service.Sockets;

namespace Nexus.Service.Integrations.HomeAssistant;

public sealed class HomeAssistantHub : BackgroundService
{
    // Network-reconnect backoff bounds. Clean WS close uses BackoffMin to prevent storm.
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);

    // Trailing throttle window for state_changed-driven broadcasts.
    private static readonly TimeSpan BroadcastWindow = TimeSpan.FromMilliseconds(300);
    // Bound for the short-lived dashboard sockets.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private const int MaxDashboardIdLength = 128;

    private static readonly HashSet<string> SupportedDomains = new(StringComparer.Ordinal)
    {
        "light", "switch", "input_boolean", "fan", "automation", "scene", "script",
        "button", "input_button", "cover", "lock", "sensor", "binary_sensor",
    };

    private static readonly HashSet<string> ToggleDomains = new(StringComparer.Ordinal)
    {
        "light", "switch", "input_boolean", "fan", "automation", "binary_sensor",
    };

    private readonly IConfigStore _store;
    private readonly HomeAssistantClient _client;
    private readonly MultiplexHub _hub;
    private readonly ILogger<HomeAssistantHub> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, HaEntityDto> _cache = new(StringComparer.OrdinalIgnoreCase);
    private HaRegistryMaps _maps = new(); // guarded by _lock
    private bool _connected;
    private string _error = "";

    // Entities referenced by fetched dashboards; cached beyond the light/switch set.
    private readonly HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase); // guarded by _lock
    // Raw Lovelace configs by dashboard id; guarded by _lock.
    private readonly Dictionary<string, JsonElement> _dashCache = new(StringComparer.Ordinal);
    // Bumped whenever cached dashboards are invalidated, so an in-flight fetch does not store stale data.
    private long _cacheEpoch; // guarded by _lock
    // Bumped when the URL/token change, so a fetch begun under the old config adds nothing.
    private long _configGen; // guarded by _lock
    // Shared in-flight dashboard fetches by id, so concurrent misses open one HA socket.
    // Tagged with the cache epoch they started under: a fetch begun before an
    // invalidation is never joined, since its result may predate the edit.
    private readonly Dictionary<string, (long Epoch, Task<JsonElement> Task)> _inflight = new(StringComparer.Ordinal); // guarded by _lock
    private long _dashboardsRevision; // Interlocked

    private readonly object _bcLock = new();
    private readonly Timer _bcTimer;
    private bool _bcArmed; // guarded by _bcLock
    private bool _bcRoomsPending; // guarded by _bcLock

    // Replaced atomically in SignalReconfigure; old CTS is cancelled to wake any
    // Task.Delay or RunConnectionAsync linked to it via iterCts.
    private volatile CancellationTokenSource _reconfigureCts = new();

    public HomeAssistantHub(
        IConfigStore store,
        HomeAssistantClient client,
        MultiplexHub hub,
        ILogger<HomeAssistantHub> logger)
    {
        _store = store;
        _client = client;
        _hub = hub;
        _logger = logger;
        _bcTimer = new Timer(_ => FlushBroadcast(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public override void Dispose()
    {
        _bcTimer.Dispose();
        base.Dispose();
    }

    // roomsChanged: the room-view entity set (or its registry data) changed. Any pending
    // coalesced room change rides along, since subscribers refetch everything anyway.
    private void Broadcast(bool roomsChanged)
    {
        lock (_bcLock)
        {
            roomsChanged |= _bcRoomsPending;
            _bcRoomsPending = false;
        }
        PanelTopics.BroadcastHomeAssistant(_hub, Interlocked.Read(ref _dashboardsRevision), roomsChanged);
    }

    // Trailing throttle: the first call arms the window, later calls inside it are absorbed,
    // and the window's end emits one broadcast covering the burst.
    private void ScheduleBroadcast(bool roomsChanged)
    {
        lock (_bcLock)
        {
            _bcRoomsPending |= roomsChanged;
            if (_bcArmed)
            {
                return;
            }
            _bcArmed = true;
            try
            {
                _bcTimer.Change(BroadcastWindow, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                _bcArmed = false;
            }
        }
    }

    private void FlushBroadcast()
    {
        // Runs on a timer thread, where an escaped exception would end the process.
        try
        {
            bool rooms;
            lock (_bcLock)
            {
                _bcArmed = false;
                rooms = _bcRoomsPending;
                _bcRoomsPending = false;
            }
            PanelTopics.BroadcastHomeAssistant(_hub, Interlocked.Read(ref _dashboardsRevision), rooms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home Assistant broadcast flush failed: {Msg}", ex.Message);
        }
    }

    private static bool IsRoomDomain(string domain) => domain == "light" || domain == "switch";

    public HaConfigResponse GetConfigResponse()
    {
        var settings = _store.Load().HomeAssistant;
        bool configured = HasToken(settings);
        bool connected;
        string error;
        lock (_lock)
        {
            connected = _connected;
            error = _error;
        }
        return new HaConfigResponse
        {
            Url = settings.Url,
            Configured = configured,
            Connected = connected,
            Error = error,
        };
    }

    public async Task<HaConfigSetResponse> SetConfigAsync(string url, string token, CancellationToken ct)
    {
        url = url.Trim().TrimEnd('/');
        token = token.Trim();

        if (url.Length == 0 || token.Length == 0)
        {
            return new HaConfigSetResponse { Ok = false, Error = "url and token are required" };
        }

        var validateError = await _client.ValidateAsync(url, token, ct);
        if (validateError is not null)
        {
            return new HaConfigSetResponse { Ok = false, Error = validateError };
        }

        _store.Update(s =>
        {
            s.HomeAssistant.Url = url;
            s.HomeAssistant.Token = SecretProtector.Protect(token);
            s.HomeAssistant.Enabled = true;
        });

        lock (_lock)
        {
            _watched.Clear();
            _dashCache.Clear();
            _cacheEpoch++;
            _configGen++;
        }

        // Wake the background loop immediately regardless of whether it is in
        // the unconfigured poll or an error backoff.
        SignalReconfigure();

        // REST validation passed, so the URL+token are known-good.
        return new HaConfigSetResponse { Ok = true, Connected = true };
    }

    public HaEntitiesResponse GetEntitiesResponse()
    {
        var settings = _store.Load().HomeAssistant;
        bool configured = HasToken(settings);
        bool connected;
        string error;
        List<HaEntityDto> entities;
        lock (_lock)
        {
            connected = _connected;
            error = _error;
            entities = new List<HaEntityDto>(_cache.Values);
        }
        return new HaEntitiesResponse
        {
            Configured = configured,
            Connected = connected,
            Error = error,
            Entities = entities,
        };
    }

    /// <summary>
    /// Calls the HA service, reads back the updated state, refreshes the cache,
    /// and broadcasts. Returns null when the hub is not configured, the entity is
    /// unknown, or the HA call fails.
    /// </summary>
    public async Task<HaEntityDto?> SetEntityAsync(HaSetEntityBody body, CancellationToken ct)
    {
        var cfg = ResolveRuntimeConfig();
        if (!cfg.IsConfigured)
        {
            return null;
        }

        var entityId = body.EntityId.Trim();
        HaEntityDto? entity;
        lock (_lock)
        {
            _cache.TryGetValue(entityId, out entity);
        }
        if (entity is null)
        {
            return null;
        }

        // Clamp inputs to protocol-valid ranges.
        var brightnessPct = body.BrightnessPct.HasValue
            ? (int?)Math.Clamp(body.BrightnessPct.Value, 0, 100)
            : null;
        var rgb = body.Rgb;
        if (rgb is not null && rgb.Length == 3)
        {
            rgb = new int[]
            {
                Math.Clamp(rgb[0], 0, 255),
                Math.Clamp(rgb[1], 0, 255),
                Math.Clamp(rgb[2], 0, 255),
            };
        }
        var colorTempK = body.ColorTempK.HasValue
            ? (int?)Math.Clamp(body.ColorTempK.Value, 1000, 10000)
            : null;

        var domain = entity.Domain;
        var (service, ok) = ResolveService(domain, body, entity.CodeRequired);
        if (!ok)
        {
            return null;
        }
        var bodyJson = domain == "light" && service == "turn_on"
            ? BuildServiceBody(entityId, brightnessPct, rgb, colorTempK)
            : BuildServiceBody(entityId, null, null, null);

        try
        {
            await _client.CallServiceAsync(cfg.Url, cfg.Token, domain, service, bodyJson, ct);

            var stateEl = await _client.GetStateAsync(cfg.Url, cfg.Token, entityId, ct);
            if (stateEl is null)
            {
                return null;
            }

            var updated = NormalizeEntity(stateEl.Value);
            if (updated is null)
            {
                return null;
            }

            lock (_lock)
            {
                ApplyRegistry(updated);
                _cache[entityId] = updated;
            }
            Broadcast(IsRoomDomain(domain));
            return updated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home Assistant SetEntity {Id} failed: {Msg}", entityId, ex.Message);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var backoff = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            // iterCts links both the host stop token and the current reconfigure
            // signal so any Task.Delay or WS loop wakes on either.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _reconfigureCts.Token);
            var ct = iterCts.Token;

            var cfg = ResolveRuntimeConfig();
            if (!cfg.IsConfigured)
            {
                SetState(connected: false, error: "");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reconfigure signal woke the poll.
                }
                continue;
            }

            try
            {
                await RunConnectionAsync(cfg.Url, cfg.Token, ct);
                // Clean close: apply minimum backoff to avoid reconnect storm on a
                // flapping HA server.
                backoff = BackoffMin;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Reconfigure triggered; reconnect immediately with new config.
                backoff = TimeSpan.Zero;
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Home Assistant connection error: {Msg}", ex.Message);
                SetState(connected: false, error: ex.Message);
                // Network-reconnect backoff: exponential, bounded.
                backoff = backoff == TimeSpan.Zero
                    ? BackoffMin
                    : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, BackoffMax.TotalSeconds));
            }

            if (!stoppingToken.IsCancellationRequested && backoff > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reconfigure woke the backoff; reconnect immediately.
                    backoff = TimeSpan.Zero;
                }
            }
        }

        SetState(connected: false, error: "");
    }

    private async Task RunConnectionAsync(string url, string token, CancellationToken ct)
    {
        var wsUrl = DeriveWsUrl(url);

        await using var ws = new HomeAssistantWebSocket();
        await ws.ConnectAsync(wsUrl, token, ct);

        var maps = await ws.FetchRegistriesAsync(ct);

        // REST snapshot after WS subscription + registry fetch so no state events
        // are missed in the gap between subscribe and the REST call.
        var states = await _client.GetStatesAsync(url, token, ct);
        lock (_lock)
        {
            _maps = maps;
            _cache.Clear();
            _dashCache.Clear();
            _cacheEpoch++;
            foreach (var el in states)
            {
                var dto = NormalizeEntity(el);
                if (dto is not null && IsTracked(dto.Domain, dto.Id))
                {
                    ApplyRegistry(dto);
                    _cache[dto.Id] = dto;
                }
            }
            _connected = true;
            _error = "";
        }
        Broadcast(true);

        while (!ct.IsCancellationRequested)
        {
            var msg = await ws.ReceiveNextAsync(ct);
            if (msg is null)
            {
                break;
            }

            if (msg.Value.IsRegistryUpdate)
            {
                var newMaps = await ws.FetchRegistriesAsync(ct);
                UpdateMapsAndRebuildAreas(newMaps);
                Broadcast(true);
                continue;
            }

            if (msg.Value.IsDashboardUpdate)
            {
                lock (_lock)
                {
                    _dashCache.Remove(msg.Value.DashboardId);
                    _cacheEpoch++;
                }
                Interlocked.Increment(ref _dashboardsRevision);
                ScheduleBroadcast(false);
                continue;
            }

            var entityId = msg.Value.EntityId;
            var dot = entityId.IndexOf('.');
            if (dot < 0)
            {
                continue;
            }
            var domain = entityId.Substring(0, dot);
            if (!IsTracked(domain, entityId))
            {
                continue;
            }

            if (msg.Value.NewState.ValueKind != JsonValueKind.Object)
            {
                // Entity removed from HA.
                bool removed;
                lock (_lock)
                {
                    removed = _cache.Remove(entityId);
                }
                if (removed)
                {
                    ScheduleBroadcast(IsRoomDomain(domain));
                }
                continue;
            }

            var updated = NormalizeEntity(msg.Value.NewState);
            if (updated is null)
            {
                continue;
            }

            lock (_lock)
            {
                ApplyRegistry(updated);
                _cache[entityId] = updated;
            }
            ScheduleBroadcast(IsRoomDomain(domain));
        }

        SetState(connected: false, error: "");
    }

    private void UpdateMapsAndRebuildAreas(HaRegistryMaps maps)
    {
        lock (_lock)
        {
            _maps = maps;
            foreach (var dto in _cache.Values)
            {
                ApplyRegistry(dto);
            }
        }
    }

    // Caller holds _lock.
    private void ApplyRegistry(HaEntityDto dto)
    {
        dto.Area = _maps.Resolve(dto.Id);
        dto.Hidden = _maps.IsHidden(dto.Id);
    }

    // Room-view domains are always tracked; the rest only when a fetched dashboard references them.
    private bool IsTracked(string domain, string entityId)
    {
        if (domain == "light" || domain == "switch")
        {
            return true;
        }
        if (!SupportedDomains.Contains(domain))
        {
            return false;
        }
        lock (_lock)
        {
            return _watched.Contains(entityId);
        }
    }

    public async Task<HaDashboardsResponse> GetDashboardsAsync(CancellationToken ct)
    {
        var cfg = ResolveRuntimeConfig();
        bool connected;
        lock (_lock)
        {
            connected = _connected;
        }
        if (!cfg.IsConfigured || !connected)
        {
            return new HaDashboardsResponse { Connected = false };
        }

        var result = new HaDashboardsResponse { Connected = true };
        result.Dashboards.Add(new HaDashboardDto { Id = "lovelace", Title = "" });
        try
        {
            var list = await RunEphemeralCommandAsync(cfg, "lovelace/dashboards/list", null, ct);
            var extra = new List<HaDashboardDto>();
            if (list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("url_path", out var pathEl) ||
                        pathEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var path = pathEl.GetString() ?? "";
                    if (path.Length == 0)
                    {
                        continue;
                    }
                    var title = item.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() ?? ""
                        : "";
                    extra.Add(new HaDashboardDto { Id = path, Title = title });
                }
            }
            extra.Sort((x, y) => string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase));
            result.Dashboards.AddRange(extra);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home Assistant dashboard list failed: {Msg}", ex.Message);
            result.Error = ex.Message;
        }
        return result;
    }

    public async Task<HaDashboardResponse> GetDashboardAsync(string id, CancellationToken ct)
    {
        // Captured before any lookup, so an update landing mid-fetch still reads as newer to the caller.
        var revision = Interlocked.Read(ref _dashboardsRevision);

        if (!IsValidDashboardId(id))
        {
            return new HaDashboardResponse { Id = id, Error = "not_found", Revision = revision };
        }

        var cfg = ResolveRuntimeConfig();
        bool connected;
        JsonElement cached = default;
        bool hit;
        long gen;
        Task<JsonElement>? fetch = null;
        lock (_lock)
        {
            connected = _connected;
            gen = _configGen;
            hit = _dashCache.TryGetValue(id, out cached);
            if (connected && cfg.IsConfigured && !hit)
            {
                if (_inflight.TryGetValue(id, out var running) && running.Epoch == _cacheEpoch)
                {
                    fetch = running.Task;
                }
                else
                {
                    fetch = StartDashboardFetch(cfg, id);
                    _inflight[id] = (_cacheEpoch, fetch);
                }
            }
        }
        if (!cfg.IsConfigured || !connected)
        {
            return new HaDashboardResponse { Id = id, Error = "not_connected", Revision = revision };
        }

        JsonElement config;
        if (hit)
        {
            config = cached;
        }
        else
        {
            try
            {
                // The caller's cancellation abandons the wait only; the shared fetch keeps running.
                config = await fetch!.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HaCommandException ex) when (ex.Code == "config_not_found")
            {
                return new HaDashboardResponse { Id = id, Error = "not_found", Revision = revision };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Home Assistant dashboard {Id} fetch failed: {Msg}", id, ex.Message);
                return new HaDashboardResponse { Id = id, Error = "failed", Revision = revision };
            }
        }

        try
        {
            await WatchReferencedEntitiesAsync(cfg, config, gen, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home Assistant dashboard {Id} entity load failed: {Msg}", id, ex.Message);
            return new HaDashboardResponse { Id = id, Error = "failed", Revision = revision };
        }

        return new HaDashboardResponse { Id = id, Config = config, Revision = revision };
    }

    // Caller holds _lock. Runs on its own timeout-bounded token, detached from any one request.
    private Task<JsonElement> StartDashboardFetch(RuntimeConfig cfg, string id)
    {
        var epoch = _cacheEpoch;
        return Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(CommandTimeout);
                var config = await RunEphemeralCommandAsync(
                    cfg, "lovelace/config", id == "lovelace" ? null : id, cts.Token);
                lock (_lock)
                {
                    if (_cacheEpoch == epoch)
                    {
                        _dashCache[id] = config;
                    }
                }
                return config;
            }
            finally
            {
                lock (_lock)
                {
                    // A newer fetch for the same id may have replaced this entry.
                    if (_inflight.TryGetValue(id, out var entry) && entry.Epoch == epoch)
                    {
                        _inflight.Remove(id);
                    }
                }
            }
        });
    }

    // Adds the config's entities to the watched set and loads their states before returning,
    // so the next entities fetch already contains them. A changed config generation means the
    // fetch began under settings that no longer apply, so nothing is added.
    private async Task WatchReferencedEntitiesAsync(
        RuntimeConfig cfg, JsonElement config, long gen, CancellationToken ct)
    {
        var ids = ExtractEntityIds(config);
        var added = new List<string>();
        lock (_lock)
        {
            if (_configGen != gen)
            {
                return;
            }
            foreach (var entityId in ids)
            {
                if (_watched.Add(entityId))
                {
                    added.Add(entityId);
                }
            }
        }
        if (added.Count == 0)
        {
            return;
        }

        try
        {
            JsonElement[] states;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(CommandTimeout);
                states = await _client.GetStatesAsync(cfg.Url, cfg.Token, timeout.Token);
            }
            var wanted = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                if (_configGen != gen)
                {
                    return;
                }
                foreach (var el in states)
                {
                    var dto = NormalizeEntity(el);
                    // An event may have cached a fresher state since the watch was added.
                    if (dto is not null && wanted.Contains(dto.Id) && !_cache.ContainsKey(dto.Id))
                    {
                        ApplyRegistry(dto);
                        _cache[dto.Id] = dto;
                    }
                }
            }
        }
        catch
        {
            // Unwatch so the next fetch of this dashboard retries the load, and drop any
            // non-room entity cached meanwhile, since nothing keeps it fresh once unwatched.
            lock (_lock)
            {
                foreach (var entityId in added)
                {
                    _watched.Remove(entityId);
                    var dot = entityId.IndexOf('.');
                    if (dot > 0 && !IsRoomDomain(entityId.Substring(0, dot)))
                    {
                        _cache.Remove(entityId);
                    }
                }
            }
            throw;
        }
        Broadcast(false);
    }

    private static async Task<JsonElement> RunEphemeralCommandAsync(
        RuntimeConfig cfg, string commandType, string? urlPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        await using var ws = new HomeAssistantWebSocket();
        await ws.AuthenticateAsync(DeriveWsUrl(cfg.Url), cfg.Token, timeout.Token);
        return await ws.RunCommandAsync(commandType, urlPath, timeout.Token);
    }

    private static bool IsValidDashboardId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxDashboardIdLength)
        {
            return false;
        }
        foreach (var c in id)
        {
            if (char.IsWhiteSpace(c) || c == '/' || c == '\\')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Collects supported-domain entity ids from a raw Lovelace config: every string
    /// "entity" property and every string or {entity} item of an "entities" or "badges" array.
    /// </summary>
    internal static HashSet<string> ExtractEntityIds(JsonElement config)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        CollectEntityIds(config, found);
        return found;
    }

    private static void CollectEntityIds(JsonElement el, HashSet<string> found)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name == "entity" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    AddIfEntityId(prop.Value.GetString(), found);
                }
                else if ((prop.Name == "entities" || prop.Name == "badges") &&
                         prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AddIfEntityId(item.GetString(), found);
                        }
                    }
                }
                // Condition and visibility-rule entities are never rendered, so they are not worth tracking.
                if (prop.Name != "conditions" && prop.Name != "visibility")
                {
                    CollectEntityIds(prop.Value, found);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                CollectEntityIds(item, found);
            }
        }
    }

    private static void AddIfEntityId(string? candidate, HashSet<string> found)
    {
        if (candidate is null)
        {
            return;
        }
        var dot = candidate.IndexOf('.');
        if (dot <= 0 || dot == candidate.Length - 1)
        {
            return;
        }
        if (!SupportedDomains.Contains(candidate.Substring(0, dot)))
        {
            return;
        }
        for (int i = dot + 1; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return;
            }
        }
        found.Add(candidate);
    }

    /// <summary>
    /// Maps a command to the HA service for the entity's domain. Ok is false for any
    /// domain/action pair Nexus does not allow.
    /// </summary>
    internal static (string Service, bool Ok) ResolveService(string domain, HaSetEntityBody body, bool codeRequired)
    {
        const string refused = "";
        switch (domain)
        {
            case "light":
            case "switch":
                return (body.On == false ? "turn_off" : "turn_on", true);
            case "input_boolean":
            case "fan":
            case "automation":
                return body.On.HasValue ? (body.On.Value ? "turn_on" : "turn_off", true) : (refused, false);
            case "scene":
            case "script":
                return body.Action == "run" ? ("turn_on", true) : (refused, false);
            case "button":
            case "input_button":
                return body.Action == "run" ? ("press", true) : (refused, false);
            case "cover":
                return body.Action switch
                {
                    "open" => ("open_cover", true),
                    "close" => ("close_cover", true),
                    "stop" => ("stop_cover", true),
                    _ => (refused, false),
                };
            case "lock":
                if (codeRequired)
                {
                    return (refused, false);
                }
                return body.Action switch
                {
                    "lock" => ("lock", true),
                    "unlock" => ("unlock", true),
                    _ => (refused, false),
                };
            default:
                return (refused, false);
        }
    }

    private void SignalReconfigure()
    {
        try
        {
            Interlocked.Exchange(ref _reconfigureCts, new CancellationTokenSource()).Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private void SetState(bool connected, string error)
    {
        lock (_lock)
        {
            _connected = connected;
            _error = error;
        }
    }

    private RuntimeConfig ResolveRuntimeConfig()
    {
        var settings = _store.Load().HomeAssistant;
        var url = settings.Url.Trim().TrimEnd('/');
        var token = SecretProtector.Unprotect(settings.Token).Trim();
        var configured = settings.Enabled && url.Length > 0 && token.Length > 0;
        return new RuntimeConfig(url, token, configured);
    }

    private static bool HasToken(HomeAssistantSettings settings)
    {
        return SecretProtector.Unprotect(settings.Token).Trim().Length > 0;
    }

    private static string DeriveWsUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl.TrimEnd('/'));
        var scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{uri.Authority}/api/websocket";
    }

    internal static HaEntityDto? NormalizeEntity(JsonElement stateEl)
    {
        if (!stateEl.TryGetProperty("entity_id", out var idEl))
        {
            return null;
        }
        var entityId = idEl.GetString() ?? "";
        var dot = entityId.IndexOf('.');
        if (dot < 0)
        {
            return null;
        }
        var domain = entityId.Substring(0, dot);
        if (!SupportedDomains.Contains(domain))
        {
            return null;
        }

        var state = stateEl.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "" : "";
        var name = entityId;
        int brightnessPct = 0;
        bool supportsBrightness = false, supportsColor = false, supportsColorTemp = false;
        int[]? rgb = null;
        int colorTempK = 0;
        string unit = "", deviceClass = "";
        int positionPct = -1;
        bool codeRequired = false;

        if (stateEl.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.TryGetProperty("friendly_name", out var fnEl) &&
                fnEl.ValueKind == JsonValueKind.String)
            {
                name = fnEl.GetString() ?? entityId;
            }

            if (attrs.TryGetProperty("unit_of_measurement", out var unitEl) &&
                unitEl.ValueKind == JsonValueKind.String)
            {
                unit = unitEl.GetString() ?? "";
            }
            if (attrs.TryGetProperty("device_class", out var dcEl) &&
                dcEl.ValueKind == JsonValueKind.String)
            {
                deviceClass = dcEl.GetString() ?? "";
            }
            if (domain == "cover" &&
                attrs.TryGetProperty("current_position", out var posEl) &&
                posEl.ValueKind == JsonValueKind.Number)
            {
                positionPct = (int)Math.Round(posEl.GetDouble());
            }
            if (domain == "lock" &&
                attrs.TryGetProperty("code_format", out var codeEl) &&
                codeEl.ValueKind != JsonValueKind.Null)
            {
                codeRequired = true;
            }

            if (domain == "light")
            {
                if (attrs.TryGetProperty("brightness", out var bEl) &&
                    bEl.ValueKind == JsonValueKind.Number)
                {
                    brightnessPct = (int)Math.Round(bEl.GetDouble() / 255.0 * 100.0);
                }

                if (attrs.TryGetProperty("supported_color_modes", out var modesEl) &&
                    modesEl.ValueKind == JsonValueKind.Array)
                {
                    var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in modesEl.EnumerateArray())
                    {
                        var s = m.GetString();
                        if (s is not null)
                        {
                            modes.Add(s);
                        }
                    }

                    supportsColor = modes.Contains("hs") || modes.Contains("rgb") ||
                                    modes.Contains("rgbw") || modes.Contains("rgbww") ||
                                    modes.Contains("xy");
                    supportsColorTemp = modes.Contains("color_temp");
                    var onlyOnOff = modes.Count == 1 && modes.Contains("onoff");
                    supportsBrightness = modes.Count > 0 && !onlyOnOff;
                }

                if (attrs.TryGetProperty("rgb_color", out var rgbEl) &&
                    rgbEl.ValueKind == JsonValueKind.Array)
                {
                    var vals = new int[3];
                    int i = 0;
                    foreach (var c in rgbEl.EnumerateArray())
                    {
                        if (i >= 3)
                        {
                            break;
                        }
                        if (c.ValueKind != JsonValueKind.Number)
                        {
                            break;
                        }
                        vals[i++] = (int)Math.Round(c.GetDouble());
                    }
                    if (i == 3)
                    {
                        rgb = vals;
                    }
                }

                if (attrs.TryGetProperty("color_temp_kelvin", out var ctEl) &&
                    ctEl.ValueKind == JsonValueKind.Number)
                {
                    colorTempK = (int)Math.Round(ctEl.GetDouble());
                }
            }
        }

        return new HaEntityDto
        {
            Id = entityId,
            Name = name,
            Domain = domain,
            State = state,
            On = ToggleDomains.Contains(domain) && state == "on",
            Reachable = state != "unavailable",
            BrightnessPct = brightnessPct,
            SupportsBrightness = supportsBrightness,
            SupportsColor = supportsColor,
            SupportsColorTemp = supportsColorTemp,
            Rgb = rgb,
            ColorTempK = colorTempK,
            Area = "",
            Unit = unit,
            DeviceClass = deviceClass,
            PositionPct = positionPct,
            CodeRequired = codeRequired,
        };
    }

    private static string BuildServiceBody(string entityId, int? brightnessPct, int[]? rgb, int? colorTempK)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("entity_id", entityId);
        if (brightnessPct.HasValue)
        {
            w.WriteNumber("brightness_pct", brightnessPct.Value);
        }
        if (rgb is not null && rgb.Length == 3)
        {
            w.WritePropertyName("rgb_color");
            w.WriteStartArray();
            w.WriteNumberValue(rgb[0]);
            w.WriteNumberValue(rgb[1]);
            w.WriteNumberValue(rgb[2]);
            w.WriteEndArray();
        }
        if (colorTempK.HasValue)
        {
            w.WriteNumber("color_temp_kelvin", colorTempK.Value);
        }
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed record RuntimeConfig(string Url, string Token, bool IsConfigured);
}
