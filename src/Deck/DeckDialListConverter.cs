using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nexus.Service.Deck;

/// <summary>
/// Reads a dial list entry by entry: dials are positional, and one malformed
/// entry must not fail the whole settings load (JsonConfigStore resets every
/// setting on any deserialization exception). A bad entry becomes an empty
/// dial; a non-array value reads as no dials.
/// </summary>
public sealed class DeckDialListConverter : JsonConverter<List<DeckDial>>
{
    public override List<DeckDial>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? ReadList(doc.RootElement, options) : null;
    }

    public static List<DeckDial> ReadList(JsonElement array, JsonSerializerOptions options)
    {
        var typeInfo = (JsonTypeInfo<DeckDial>)options.GetTypeInfo(typeof(DeckDial));
        var dials = new List<DeckDial>();
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                dials.Add(element.ValueKind == JsonValueKind.Object ? element.Deserialize(typeInfo) ?? new DeckDial() : new DeckDial());
            }
            catch (JsonException)
            {
                dials.Add(new DeckDial());
            }
        }
        return dials;
    }

    public override void Write(Utf8JsonWriter writer, List<DeckDial> value, JsonSerializerOptions options)
    {
        var typeInfo = (JsonTypeInfo<DeckDial>)options.GetTypeInfo(typeof(DeckDial));
        writer.WriteStartArray();
        foreach (var dial in value)
        {
            JsonSerializer.Serialize(writer, dial, typeInfo);
        }
        writer.WriteEndArray();
    }
}
