using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nexus.Service.Deck;

/// <summary>
/// Hand-written AOT-safe converter for <see cref="DeckAction"/>. The TS union
/// (nexus-web's <c>deck/types.ts</c>) reuses the JSON key "action" for three
/// different shapes depending on the sibling "type": an object for
/// system/nexus, a plain string for power. System.Text.Json's source
/// generator cannot express "one key, three shapes keyed off a sibling
/// field" as a flat property list, so this converter reads/writes the wire
/// bytes directly while keeping the C# side as three distinct properties
/// (<see cref="DeckAction.SystemAction"/>, <see cref="DeckAction.NexusAction"/>,
/// <see cref="DeckAction.PowerAction"/>). nexus-web's api/streamdeck.ts passes
/// its native DeckConfig straight through with no adapter layer, so this
/// wire shape must match deck/types.ts exactly.
///
/// Every nested type (DeckSystemAction, DeckNexusAction, DeckToggleState,
/// List&lt;DeckSequenceStep&gt;, and DeckAction itself for toggle/sequence
/// recursion) is (de)serialized through the source-generated
/// <see cref="JsonTypeInfo{T}"/> resolved from <c>options</c> at the call
/// site, never a reflection-based overload, so this stays AOT-safe under
/// both AppJsonContext and PersistenceJsonContext.
/// </summary>
public sealed class DeckActionConverter : JsonConverter<DeckAction>
{
    public override DeckAction Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var action = new DeckAction();

        // A malformed action node (a raw string/array/number where an object
        // is expected, whether at the top level or nested via steps[].action
        // / on / off) would throw on the TryGetProperty calls below. Settings
        // persistence has no per-field recovery (JsonConfigStore.Load resets
        // every setting on any deserialization exception), so this returns an
        // empty/unknown action instead of throwing.
        if (root.ValueKind != JsonValueKind.Object)
        {
            return action;
        }

        if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            action.Type = typeEl.GetString() ?? "";
        }
        if (root.TryGetProperty("appId", out var appIdEl) && appIdEl.ValueKind == JsonValueKind.String)
        {
            action.AppId = appIdEl.GetString();
        }
        if (root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
        {
            action.Path = pathEl.GetString();
        }
        if (root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            action.Url = urlEl.GetString();
        }
        if (root.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.String)
        {
            action.Keys = keysEl.GetString();
        }
        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            action.Text = textEl.GetString();
        }
        // A legacy "paste" key (pre-simplification text actions always
        // pasted-or-typed based on this flag) is intentionally never read -
        // the text action now always pastes, so old configs load fine with
        // the key silently dropped.
        if (root.TryGetProperty("deviceId", out var deviceIdEl) && deviceIdEl.ValueKind == JsonValueKind.String)
        {
            action.DeviceId = deviceIdEl.GetString();
        }
        if (root.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String)
        {
            action.Op = opEl.GetString();
        }
        if (root.TryGetProperty("target", out var targetEl) && targetEl.ValueKind == JsonValueKind.Number)
        {
            action.Target = targetEl.GetInt32();
        }
        if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Number)
        {
            action.Value = valueEl.GetInt32();
        }
        if (root.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number)
        {
            action.Step = stepEl.GetInt32();
        }
        if (root.TryGetProperty("keysA", out var keysAEl) && keysAEl.ValueKind == JsonValueKind.String)
        {
            action.KeysA = keysAEl.GetString();
        }
        if (root.TryGetProperty("keysB", out var keysBEl) && keysBEl.ValueKind == JsonValueKind.String)
        {
            action.KeysB = keysBEl.GetString();
        }
        if (root.TryGetProperty("category", out var categoryEl) && categoryEl.ValueKind == JsonValueKind.String)
        {
            action.Category = categoryEl.GetString();
        }
        if (root.TryGetProperty("sensor", out var sensorEl) && sensorEl.ValueKind == JsonValueKind.String)
        {
            action.Sensor = sensorEl.GetString();
        }
        if (root.TryGetProperty("style", out var styleEl) && styleEl.ValueKind == JsonValueKind.String)
        {
            action.Style = styleEl.GetString();
        }
        if (root.TryGetProperty("color", out var colorEl) && colorEl.ValueKind == JsonValueKind.String)
        {
            action.Color = colorEl.GetString();
        }
        if (root.TryGetProperty("showName", out var showNameEl) &&
            (showNameEl.ValueKind == JsonValueKind.True || showNameEl.ValueKind == JsonValueKind.False))
        {
            action.ShowName = showNameEl.GetBoolean();
        }
        if (root.TryGetProperty("press", out var pressEl) && pressEl.ValueKind == JsonValueKind.String)
        {
            action.Press = pressEl.GetString();
        }
        if (root.TryGetProperty("labelText", out var labelTextEl) && labelTextEl.ValueKind == JsonValueKind.String)
        {
            action.LabelText = labelTextEl.GetString();
        }
        if (root.TryGetProperty("scale", out var scaleEl) && scaleEl.ValueKind == JsonValueKind.String)
        {
            action.Scale = scaleEl.GetString();
        }
        if (root.TryGetProperty("min", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
        {
            action.Min = minEl.GetDouble();
        }
        if (root.TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
        {
            action.Max = maxEl.GetDouble();
        }
        if (root.TryGetProperty("lat", out var latEl) && latEl.ValueKind == JsonValueKind.Number)
        {
            action.Lat = latEl.GetDouble();
        }
        if (root.TryGetProperty("lon", out var lonEl) && lonEl.ValueKind == JsonValueKind.Number)
        {
            action.Lon = lonEl.GetDouble();
        }
        if (root.TryGetProperty("city", out var cityEl) && cityEl.ValueKind == JsonValueKind.String)
        {
            action.City = cityEl.GetString();
        }
        if (root.TryGetProperty("cc", out var ccEl) && ccEl.ValueKind == JsonValueKind.String)
        {
            action.Cc = ccEl.GetString();
        }
        if (root.TryGetProperty("units", out var unitsEl) && unitsEl.ValueKind == JsonValueKind.String)
        {
            action.Units = unitsEl.GetString();
        }
        if (root.TryGetProperty("volume", out var volumeEl) && volumeEl.ValueKind == JsonValueKind.Number)
        {
            action.Volume = volumeEl.GetInt32();
        }

        if (root.TryGetProperty("action", out var actionEl))
        {
            switch (action.Type)
            {
                case "system" when actionEl.ValueKind == JsonValueKind.Object:
                    action.SystemAction = actionEl.Deserialize(GetTypeInfo<DeckSystemAction>(options));
                    break;
                case "nexus" when actionEl.ValueKind == JsonValueKind.Object:
                    action.NexusAction = actionEl.Deserialize(GetTypeInfo<DeckNexusAction>(options));
                    break;
                case "power" when actionEl.ValueKind == JsonValueKind.String:
                    action.PowerAction = actionEl.GetString();
                    break;
            }
        }

        if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
        {
            action.Steps = stepsEl.Deserialize(GetTypeInfo<List<DeckSequenceStep>>(options));
        }
        if (root.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.Object)
        {
            action.On = onEl.Deserialize(GetTypeInfo<DeckAction>(options));
        }
        if (root.TryGetProperty("off", out var offEl) && offEl.ValueKind == JsonValueKind.Object)
        {
            action.Off = offEl.Deserialize(GetTypeInfo<DeckAction>(options));
        }
        if (root.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.Object)
        {
            action.State = stateEl.Deserialize(GetTypeInfo<DeckToggleState>(options));
        }

        return action;
    }

    public override void Write(Utf8JsonWriter writer, DeckAction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);

        if (value.AppId is not null)
        {
            writer.WriteString("appId", value.AppId);
        }
        if (value.Path is not null)
        {
            writer.WriteString("path", value.Path);
        }
        if (value.Url is not null)
        {
            writer.WriteString("url", value.Url);
        }
        if (value.Keys is not null)
        {
            writer.WriteString("keys", value.Keys);
        }
        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }
        if (value.DeviceId is not null)
        {
            writer.WriteString("deviceId", value.DeviceId);
        }
        if (value.Op is not null)
        {
            writer.WriteString("op", value.Op);
        }
        if (value.Target is not null)
        {
            writer.WriteNumber("target", value.Target.Value);
        }
        if (value.Value is not null)
        {
            writer.WriteNumber("value", value.Value.Value);
        }
        if (value.Step is not null)
        {
            writer.WriteNumber("step", value.Step.Value);
        }
        if (value.KeysA is not null)
        {
            writer.WriteString("keysA", value.KeysA);
        }
        if (value.KeysB is not null)
        {
            writer.WriteString("keysB", value.KeysB);
        }
        if (value.Category is not null)
        {
            writer.WriteString("category", value.Category);
        }
        if (value.Sensor is not null)
        {
            writer.WriteString("sensor", value.Sensor);
        }
        if (value.Style is not null)
        {
            writer.WriteString("style", value.Style);
        }
        if (value.Color is not null)
        {
            writer.WriteString("color", value.Color);
        }
        if (value.ShowName is not null)
        {
            writer.WriteBoolean("showName", value.ShowName.Value);
        }
        if (value.Press is not null)
        {
            writer.WriteString("press", value.Press);
        }
        if (value.LabelText is not null)
        {
            writer.WriteString("labelText", value.LabelText);
        }
        if (value.Scale is not null)
        {
            writer.WriteString("scale", value.Scale);
        }
        if (value.Min is not null)
        {
            writer.WriteNumber("min", value.Min.Value);
        }
        if (value.Max is not null)
        {
            writer.WriteNumber("max", value.Max.Value);
        }
        if (value.Lat is not null)
        {
            writer.WriteNumber("lat", value.Lat.Value);
        }
        if (value.Lon is not null)
        {
            writer.WriteNumber("lon", value.Lon.Value);
        }
        if (value.City is not null)
        {
            writer.WriteString("city", value.City);
        }
        if (value.Cc is not null)
        {
            writer.WriteString("cc", value.Cc);
        }
        if (value.Units is not null)
        {
            writer.WriteString("units", value.Units);
        }
        if (value.Volume is not null)
        {
            writer.WriteNumber("volume", value.Volume.Value);
        }

        switch (value.Type)
        {
            case "system" when value.SystemAction is not null:
                writer.WritePropertyName("action");
                JsonSerializer.Serialize(writer, value.SystemAction, GetTypeInfo<DeckSystemAction>(options));
                break;
            case "nexus" when value.NexusAction is not null:
                writer.WritePropertyName("action");
                JsonSerializer.Serialize(writer, value.NexusAction, GetTypeInfo<DeckNexusAction>(options));
                break;
            case "power" when value.PowerAction is not null:
                writer.WriteString("action", value.PowerAction);
                break;
        }

        if (value.Steps is not null)
        {
            writer.WritePropertyName("steps");
            JsonSerializer.Serialize(writer, value.Steps, GetTypeInfo<List<DeckSequenceStep>>(options));
        }
        if (value.On is not null)
        {
            writer.WritePropertyName("on");
            JsonSerializer.Serialize(writer, value.On, GetTypeInfo<DeckAction>(options));
        }
        if (value.Off is not null)
        {
            writer.WritePropertyName("off");
            JsonSerializer.Serialize(writer, value.Off, GetTypeInfo<DeckAction>(options));
        }
        if (value.State is not null)
        {
            writer.WritePropertyName("state");
            JsonSerializer.Serialize(writer, value.State, GetTypeInfo<DeckToggleState>(options));
        }

        writer.WriteEndObject();
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
