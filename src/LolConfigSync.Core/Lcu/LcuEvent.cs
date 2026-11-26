using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Lcu;

/// <summary>One change notification from the League client's WebSocket.</summary>
public sealed record LcuEvent(string Uri, string EventType, JsonNode? Data);

/// <summary>The bits of the League client's WAMP 1.0 dialect we use.</summary>
public static class WampMessage
{
    public const string SubscribeAll = """[5,"OnJsonApiEvent"]""";
    private const int EventOpcode = 8;

    /// <summary>Parses <c>[8, "OnJsonApiEvent", {uri, eventType, data}]</c>; anything else returns null.</summary>
    public static LcuEvent? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonArray { Count: 3 } frame) return null;
            if (frame[0]?.GetValueKind() != JsonValueKind.Number || frame[0]!.GetValue<int>() != EventOpcode) return null;
            if (frame[2] is not JsonObject payload) return null;
            if (payload["uri"]?.GetValueKind() != JsonValueKind.String) return null;

            var eventType = payload["eventType"]?.GetValueKind() == JsonValueKind.String
                ? payload["eventType"]!.GetValue<string>()
                : "";
            var data = payload["data"];
            payload.Remove("data"); // detach so the node can be owned by the event
            return new LcuEvent(payload["uri"]!.GetValue<string>(), eventType, data);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
