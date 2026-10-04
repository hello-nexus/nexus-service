using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Integrations.HomeAssistant;

// Returned by ReceiveNextAsync; discriminates state events from registry events.
internal readonly struct HaWsMessage
{
    internal bool IsRegistryUpdate { get; init; }
    internal bool IsDashboardUpdate { get; init; }
    // lovelace_updated url_path; "lovelace" for the default dashboard.
    internal string DashboardId { get; init; }
    internal string EntityId { get; init; }
    internal JsonElement NewState { get; init; }
}

// Area-resolution maps built from the three HA config registry lists.
internal sealed class HaRegistryMaps
{
    // area_id -> display name
    internal Dictionary<string, string> AreaById { get; } = new(StringComparer.OrdinalIgnoreCase);
    // entity_id -> entity-level area_id
    internal Dictionary<string, string> EntityAreaById { get; } = new(StringComparer.OrdinalIgnoreCase);
    // entity_id -> device_id
    internal Dictionary<string, string> EntityDeviceById { get; } = new(StringComparer.OrdinalIgnoreCase);
    // device_id -> area_id
    internal Dictionary<string, string> DeviceAreaById { get; } = new(StringComparer.OrdinalIgnoreCase);
    // entity ids the registry hides (hidden_by or entity_category set)
    internal HashSet<string> HiddenIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsHidden(string entityId) => HiddenIds.Contains(entityId);

    internal string Resolve(string entityId)
    {
        string? areaId = null;
        if (EntityAreaById.TryGetValue(entityId, out var eArea) && eArea.Length > 0)
        {
            areaId = eArea;
        }
        else if (EntityDeviceById.TryGetValue(entityId, out var deviceId) && deviceId.Length > 0)
        {
            DeviceAreaById.TryGetValue(deviceId, out areaId);
        }
        return areaId != null && areaId.Length > 0 && AreaById.TryGetValue(areaId, out var name)
            ? name
            : "";
    }
}

// A failed WS command; Code is HA's error.code (empty when absent).
internal sealed class HaCommandException : InvalidOperationException
{
    internal string Code { get; }

    internal HaCommandException(string message, string code) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Manages one Home Assistant WebSocket connection: auth handshake,
/// event subscriptions, registry fetches, and the receive loop.
/// Not thread-safe; one concurrent caller assumed.
/// </summary>
internal sealed class HomeAssistantWebSocket : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;

    private readonly ClientWebSocket _ws = new();
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _nextId = 1;

    // Pending command results keyed by command id; completed by PumpOneMessageAsync.
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    // Events buffered during FetchRegistriesAsync reads; drained by ReceiveNextAsync.
    private readonly Queue<JsonElement> _bufferedEvents = new();

    public async Task ConnectAsync(string wsUrl, string token, CancellationToken ct)
    {
        await AuthenticateAsync(wsUrl, token, ct);
        await SubscribeAsync(ct);
    }

    /// <summary>Connects and completes the auth handshake without subscribing to anything.</summary>
    public async Task AuthenticateAsync(string wsUrl, string token, CancellationToken ct)
    {
        // Dead-peer detection: ping every 30 s; abort if pong not received within 10 s.
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

        await _ws.ConnectAsync(new Uri(wsUrl), ct);

        using (var authReqDoc = await ReadMessageAsync(ct))
        {
            var type = GetString(authReqDoc.RootElement, "type");
            if (type != "auth_required")
            {
                throw new InvalidOperationException($"Expected auth_required, got: {type}");
            }
        }

        await SendRawAsync(BuildAuthMessage(token), ct);

        using (var authResultDoc = await ReadMessageAsync(ct))
        {
            var type = GetString(authResultDoc.RootElement, "type");
            if (type == "auth_invalid")
            {
                throw new InvalidOperationException("Home Assistant rejected the token");
            }
            if (type != "auth_ok")
            {
                throw new InvalidOperationException($"Unexpected auth response: {type}");
            }
        }
    }

    /// <summary>Subscribes to state_changed plus the registry and dashboard update events.</summary>
    public async Task SubscribeAsync(CancellationToken ct)
    {
        // Subscribe to state_changed.
        int subId = _nextId++;
        await SendRawAsync(BuildSubscribeMessage(subId, "state_changed"), ct);
        using (var subResultDoc = await ReadMessageAsync(ct))
        {
            var subRoot = subResultDoc.RootElement;
            if (GetString(subRoot, "type") != "result")
            {
                throw new InvalidOperationException(
                    $"Expected result after subscribe, got: {GetString(subRoot, "type")}");
            }
            if (!subRoot.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
            {
                throw new InvalidOperationException("Home Assistant rejected state_changed subscription");
            }
        }

        // Subscribe to registry update events so room reassignments reflect without a restart,
        // and lovelace_updated so dashboard edits invalidate the cached config.
        // Failure of an individual subscription degrades live-refresh for that event type; it
        // does not affect state_changed or the initial registry fetch.
        foreach (var evType in new[] { "area_registry_updated", "device_registry_updated", "entity_registry_updated", "lovelace_updated" })
        {
            int regId = _nextId++;
            await SendRawAsync(BuildSubscribeMessage(regId, evType), ct);
            using var regSubDoc = await ReadMessageAsync(ct);
            var regRoot = regSubDoc.RootElement;
            if (GetString(regRoot, "type") != "result")
            {
                throw new InvalidOperationException(
                    $"Expected result after {evType} subscribe, got: {GetString(regRoot, "type")}");
            }
            // success=false means HA denied this subscription (e.g. insufficient permissions);
            // continue without live refresh for this event type rather than aborting the connection.
        }
    }

    /// <summary>
    /// Sends the three registry list commands and returns parsed area-resolution maps.
    /// State events arriving during the reads are buffered for ReceiveNextAsync.
    /// </summary>
    public async Task<HaRegistryMaps> FetchRegistriesAsync(CancellationToken ct)
    {
        int areaCmd = _nextId++;
        int deviceCmd = _nextId++;
        int entityCmd = _nextId++;

        var areaTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entityTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[areaCmd] = areaTcs;
        _pending[deviceCmd] = deviceTcs;
        _pending[entityCmd] = entityTcs;

        await SendRawAsync(BuildCommandMessage(areaCmd, "config/area_registry/list"), ct);
        await SendRawAsync(BuildCommandMessage(deviceCmd, "config/device_registry/list"), ct);
        await SendRawAsync(BuildCommandMessage(entityCmd, "config/entity_registry/list"), ct);

        while (_pending.Count > 0)
        {
            await PumpOneMessageAsync(ct);
        }

        return BuildMaps(await areaTcs.Task, await deviceTcs.Task, await entityTcs.Task);
    }

    /// <summary>
    /// Sends one command and pumps until its result arrives. For short-lived sockets
    /// that never subscribed; not for the long-lived event socket. A failed result
    /// throws HaCommandException carrying HA's error code.
    /// </summary>
    public async Task<JsonElement> RunCommandAsync(string commandType, string? urlPath, CancellationToken ct)
    {
        int id = _nextId++;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteString("type", commandType);
                if (urlPath is not null)
                {
                    w.WriteString("url_path", urlPath);
                }
                w.WriteEndObject();
            }
            await SendRawAsync(Encoding.UTF8.GetString(ms.ToArray()), ct);
        }

        while (!tcs.Task.IsCompleted)
        {
            await PumpOneMessageAsync(ct);
        }
        return await tcs.Task;
    }

    /// <summary>
    /// Returns the next event message, or null when the connection closes.
    /// Drains events buffered during FetchRegistriesAsync before reading from the socket.
    /// </summary>
    public async Task<HaWsMessage?> ReceiveNextAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_bufferedEvents.Count > 0)
            {
                var buffered = _bufferedEvents.Dequeue();
                var parsed = ParseEventElement(buffered);
                if (parsed.HasValue)
                {
                    return parsed;
                }
                continue;
            }

            try
            {
                await PumpOneMessageAsync(ct);
            }
            catch (WebSocketException)
            {
                return null;
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        // Abort before Dispose so any in-flight ReceiveAsync unblocks immediately.
        _ws.Abort();
        _ws.Dispose();
        return default;
    }

    // Reads one WS message; routes result messages to pending TCS and event messages to _bufferedEvents.
    private async Task PumpOneMessageAsync(CancellationToken ct)
    {
        using var doc = await ReadMessageAsync(ct);
        var root = doc.RootElement;
        var type = GetString(root, "type");

        if (type == "result")
        {
            if (root.TryGetProperty("id", out var idEl) &&
                idEl.TryGetInt32(out var id) &&
                _pending.TryGetValue(id, out var tcs))
            {
                _pending.Remove(id);
                if (root.TryGetProperty("success", out var ok) &&
                    ok.GetBoolean() &&
                    root.TryGetProperty("result", out var resultEl))
                {
                    tcs.TrySetResult(resultEl.Clone());
                }
                else
                {
                    var code = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object
                        ? GetStringProp(errEl, "code")
                        : "";
                    tcs.TrySetException(new HaCommandException($"Command {id} failed", code));
                }
            }
            return;
        }

        if (type == "event")
        {
            _bufferedEvents.Enqueue(root.Clone());
        }
    }

    private static HaWsMessage? ParseEventElement(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventEl))
        {
            return null;
        }
        var eventType = GetString(eventEl, "event_type");

        if (eventType == "area_registry_updated" ||
            eventType == "device_registry_updated" ||
            eventType == "entity_registry_updated")
        {
            return new HaWsMessage { IsRegistryUpdate = true };
        }

        if (eventType == "lovelace_updated")
        {
            var path = "lovelace";
            if (eventEl.TryGetProperty("data", out var lovData) &&
                lovData.ValueKind == JsonValueKind.Object &&
                lovData.TryGetProperty("url_path", out var pathEl) &&
                pathEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(pathEl.GetString()))
            {
                path = pathEl.GetString()!;
            }
            return new HaWsMessage { IsDashboardUpdate = true, DashboardId = path };
        }

        if (eventType != "state_changed")
        {
            return null;
        }

        if (!eventEl.TryGetProperty("data", out var dataEl))
        {
            return null;
        }
        if (!dataEl.TryGetProperty("entity_id", out var idEl))
        {
            return null;
        }
        if (!dataEl.TryGetProperty("new_state", out var newStateEl))
        {
            return null;
        }
        var entityId = idEl.GetString() ?? "";
        if (entityId.Length == 0)
        {
            return null;
        }
        return new HaWsMessage { EntityId = entityId, NewState = newStateEl.Clone() };
    }

    internal static HaRegistryMaps BuildMaps(JsonElement areas, JsonElement devices, JsonElement entities)
    {
        var maps = new HaRegistryMaps();

        if (areas.ValueKind == JsonValueKind.Array)
        {
            foreach (var area in areas.EnumerateArray())
            {
                var id = GetStringProp(area, "area_id");
                var name = GetStringProp(area, "name");
                if (id.Length > 0 && name.Length > 0)
                {
                    maps.AreaById[id] = name;
                }
            }
        }

        if (devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var dev in devices.EnumerateArray())
            {
                var id = GetStringProp(dev, "id");
                var areaId = GetStringProp(dev, "area_id");
                if (id.Length > 0)
                {
                    maps.DeviceAreaById[id] = areaId;
                }
            }
        }

        if (entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var ent in entities.EnumerateArray())
            {
                var id = GetStringProp(ent, "entity_id");
                var devId = GetStringProp(ent, "device_id");
                var areaId = GetStringProp(ent, "area_id");
                if (id.Length > 0)
                {
                    if (IsNonNull(ent, "hidden_by") || IsNonNull(ent, "entity_category"))
                    {
                        maps.HiddenIds.Add(id);
                    }
                    if (areaId.Length > 0)
                    {
                        maps.EntityAreaById[id] = areaId;
                    }
                    if (devId.Length > 0)
                    {
                        maps.EntityDeviceById[id] = devId;
                    }
                }
            }
        }

        return maps;
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(_buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Connection closed by remote");
            }
            ms.Write(_buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct);
    }

    private Task SendRawAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static string BuildAuthMessage(string token)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("type", "auth");
        w.WriteString("access_token", token);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildSubscribeMessage(int id, string eventType)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("type", "subscribe_events");
        w.WriteString("event_type", eventType);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildCommandMessage(int id, string commandType)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("type", commandType);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool IsNonNull(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var v) &&
               v.ValueKind != JsonValueKind.Null &&
               v.ValueKind != JsonValueKind.Undefined;
    }

    private static string GetString(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var v) ? v.GetString() ?? "" : "";
    }

    private static string GetStringProp(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }
}
