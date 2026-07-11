using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WindUpKey.Protocol;

/// <summary>
/// Versioned envelope. Unknown types and extra JSON fields are ignored by receivers.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("v")]
    public int V { get; set; } = ProtocolVersion.Current;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonObject? Payload { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static Envelope Create(string type, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions) as JsonObject
                   ?? new JsonObject();
        return new Envelope
        {
            V = ProtocolVersion.Current,
            Type = type,
            Payload = node,
        };
    }

    public T? GetPayload<T>()
    {
        if (Payload is null)
            return default;
        return Payload.Deserialize<T>(JsonOptions);
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static Envelope? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
