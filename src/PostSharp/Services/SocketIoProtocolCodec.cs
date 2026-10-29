using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostSharp.Services;

/// <summary>One decoded Engine.IO / Socket.IO v4 packet.</summary>
public sealed record SocketIoPacket(
    string Kind,
    string Namespace,
    string AckId,
    string Data,
    string Raw
);

/// <summary>Builds and parses Socket.IO v4 packets carried over an Engine.IO WebSocket.</summary>
public static class SocketIoProtocolCodec
{
    public static string BuildNamespaceConnect(string socketIoNamespace)
    {
        var name = NormalizeNamespace(socketIoNamespace);
        return name == "/" ? "40" : $"40{name},";
    }

    public static string BuildEvent(string socketIoNamespace, string eventName, string payload)
    {
        var name = NormalizeNamespace(socketIoNamespace);
        var prefix = name == "/" ? "42" : $"42{name},";
        var name_ = string.IsNullOrWhiteSpace(eventName) ? "message" : eventName.Trim();
        var values = new JsonArray { name_ };
        if (!string.IsNullOrWhiteSpace(payload))
        {
            values.Add(ParsePayload(payload));
        }

        return prefix + values.ToJsonString();
    }

    public static SocketIoPacket Parse(string packet)
    {
        if (string.IsNullOrEmpty(packet))
        {
            return new SocketIoPacket("empty", "/", string.Empty, string.Empty, packet);
        }

        switch (packet[0])
        {
            case '0':
                return new SocketIoPacket("open", "/", string.Empty, packet[1..], packet);
            case '1':
                return new SocketIoPacket("close", "/", string.Empty, string.Empty, packet);
            case '2':
                return new SocketIoPacket("ping", "/", string.Empty, packet[1..], packet);
            case '3':
                return new SocketIoPacket("pong", "/", string.Empty, packet[1..], packet);
            case '4':
                break;
            default:
                return new SocketIoPacket("raw", "/", string.Empty, packet, packet);
        }

        if (packet.Length < 2)
        {
            return new SocketIoPacket("raw", "/", string.Empty, string.Empty, packet);
        }

        var kind = packet[1] switch
        {
            '0' => "connect",
            '1' => "disconnect",
            '2' => "event",
            '3' => "ack",
            '4' => "error",
            '5' => "binary-event",
            '6' => "binary-ack",
            _ => "raw",
        };
        var index = 2;
        var socketIoNamespace = "/";
        if (index < packet.Length && packet[index] == '/')
        {
            var comma = packet.IndexOf(',', index);
            if (comma < 0)
            {
                socketIoNamespace = packet[index..];
                index = packet.Length;
            }
            else
            {
                socketIoNamespace = packet[index..comma];
                index = comma + 1;
            }
        }

        var ackStart = index;
        while (index < packet.Length && char.IsAsciiDigit(packet[index]))
        {
            index++;
        }

        var ackId = packet[ackStart..index];
        var data = index < packet.Length ? packet[index..] : string.Empty;
        return new SocketIoPacket(kind, socketIoNamespace, ackId, data, packet);
    }

    /// <summary>Formats an event packet as “name  name: payload” for the transcript.</summary>
    public static string DescribeEvent(SocketIoPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.Data))
        {
            return packet.Kind;
        }

        try
        {
            if (JsonNode.Parse(packet.Data) is JsonArray values && values.Count > 0)
            {
                var name = values[0]?.GetValue<string>() ?? "message";
                var payload =
                    values.Count > 1 ? values[1]?.ToJsonString() ?? string.Empty : string.Empty;
                return payload.Length == 0 ? name : $"{name}: {payload}";
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            Log.Debug(exception, "Socket.IO 事件数据不是合法 JSON");
        }

        return packet.Data;
    }

    public static string NormalizeNamespace(string socketIoNamespace)
    {
        var name = (socketIoNamespace ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return "/";
        }

        if (!name.StartsWith('/'))
        {
            name = "/" + name;
        }

        return name.Length > 1 ? name.TrimEnd('/') : name;
    }

    private static JsonNode? ParsePayload(string payload)
    {
        var text = payload.Trim();
        try
        {
            return JsonNode.Parse(text)?.DeepClone();
        }
        catch (JsonException)
        {
            return JsonValue.Create(payload);
        }
    }
}
