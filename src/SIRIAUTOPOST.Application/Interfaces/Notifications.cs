using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.Interfaces;

/// <summary>
/// Hands an event to the notification system. The implementation decides who gets it (the workspace's rules, a
/// set's, a group's) and sends in the background: a failure never reaches the request that raised the event.
/// </summary>
public interface INotificationDispatcher
{
    /// <param name="linkSetId">The link set the event is about, when it is about one.</param>
    /// <param name="linkId">The group link the event is about, when it is about one.</param>
    /// <param name="text">The message, in Thai.</param>
    Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default);
}

public sealed record GatewayResult(bool Ok, string? Error)
{
    public static readonly GatewayResult Success = new(true, null);

    public static GatewayResult Failure(string error) => new(false, error);
}

/// <param name="Id">Telegram's chat id (negative for groups).</param>
public sealed record TelegramChat(string Id, string Title);

/// <param name="Error">Why the lookup failed (a short Thai message with no token in it); null when Telegram answered.</param>
public sealed record TelegramChatsResult(IReadOnlyList<TelegramChat> Chats, string? Error)
{
    public bool Ok => Error is null;

    public static TelegramChatsResult Failure(string error) => new([], error);
}

/// <summary>The outside world's side of notifications. Implementations log failures instead of throwing them.</summary>
public interface INotificationGateway
{
    Task<GatewayResult> SendTelegramAsync(string token, string chatId, string text, CancellationToken ct = default);

    Task<GatewayResult> SendLineAsync(string token, string to, string text, CancellationToken ct = default);

    /// <summary>Chats that wrote to the bot lately (Telegram getUpdates), newest first; an error when Telegram refuses the token.</summary>
    Task<TelegramChatsResult> FindTelegramChatsAsync(string token, CancellationToken ct = default);
}
