using System.Text.Json;
using System.Text.Json.Serialization;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>Builds <see cref="DeviceEvent"/>s with payloads in the API's JSON conventions (camelCase, snake_case enums).</summary>
public static class DeviceEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DeviceEvent Make(Guid workspaceId, Guid deviceId, string type, object payload, DateTimeOffset now) =>
        DeviceEvent.Create(workspaceId, deviceId, type, JsonSerializer.Serialize(payload, Json), now);

    /// <summary>A payload that carries raw JSON text as one of its members (a state or a command result).</summary>
    public static DeviceEvent MakeRaw(Guid workspaceId, Guid deviceId, string type, string payloadJson, DateTimeOffset now) =>
        DeviceEvent.Create(workspaceId, deviceId, type, payloadJson, now);

    /// <summary>Lines of a "device.log" event; over <see cref="MaxLogLines"/> the client refetches the log instead.</summary>
    public const int MaxLogLines = 100;
}
