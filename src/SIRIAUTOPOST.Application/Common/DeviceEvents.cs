using System.Text.Json;
using System.Text.Json.Serialization;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

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

    /// <summary>A "post" event: the post's status changed (queued to posting, failed, skipped...).</summary>
    public static DeviceEvent PostChanged(Guid workspaceId, Guid deviceId, Post post, DateTimeOffset now) =>
        Make(workspaceId, deviceId, DeviceEventType.Post, new { postId = post.Id, status = post.Status, failureCode = post.FailureCode }, now);

    /// <summary>A "links.changed" event: a link was switched off by the engine, switched on again, or went pending.</summary>
    public static DeviceEvent LinksChanged(Guid workspaceId, Guid deviceId, Guid linkSetId, Guid linkId, LinkHealth health, DateTimeOffset now) =>
        Make(workspaceId, deviceId, DeviceEventType.Links, new { linkSetId, linkId, health }, now);

    /// <summary>A payload that carries raw JSON text as one of its members (a state or a command result).</summary>
    public static DeviceEvent MakeRaw(Guid workspaceId, Guid deviceId, string type, string payloadJson, DateTimeOffset now) =>
        DeviceEvent.Create(workspaceId, deviceId, type, payloadJson, now);

    /// <summary>A state bigger than this is not carried by its "device.state" event (the client fetches it).</summary>
    public const int MaxStateChars = 300_000;

    /// <summary>Lines of a "device.log" event; over <see cref="MaxLogLines"/> the client refetches the log instead.</summary>
    public const int MaxLogLines = 100;
}
