using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Notifications;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Telegram and LINE notifications of a workspace (Pro and above). Viewers read, admins edit.
// No response carries a token: the settings say whether one is stored (hasToken).
[ApiController]
[Route("api/workspaces/{wsId:guid}/notifications")]
[Produces("application/json")]
public sealed class NotificationsController : ControllerBase
{
    [HttpGet]
    public Task<NotificationSettingsDto> Get(
        Guid wsId, [FromServices] IQueryHandler<GetNotificationsQuery, NotificationSettingsDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetNotificationsQuery(wsId), ct);

    /// <summary>
    /// Saves the settings (403 below Pro). In a request a null token keeps the stored one and "" clears it; rules about
    /// link sets or groups that no longer exist are dropped.
    /// </summary>
    [HttpPut]
    public Task<NotificationSettingsDto> Update(
        Guid wsId, NotificationSettingsDto settings, [FromServices] ICommandHandler<UpdateNotificationsCommand, NotificationSettingsDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new UpdateNotificationsCommand(wsId, settings), ct);

    /// <summary>Sends a short test message to "tg" or "line" with the stored settings; says whether it arrived.</summary>
    [HttpPost("test")]
    public Task<NotifyTestResultDto> Test(
        Guid wsId, NotifyTestRequest r, [FromServices] ICommandHandler<SendTestNotificationCommand, NotifyTestResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SendTestNotificationCommand(wsId, r.Channel), ct);

    /// <summary>The chats that wrote to the bot lately, to pick a chat id from. Without a token in the body the stored one is used.</summary>
    [HttpPost("telegram/chats")]
    public Task<TelegramChatsDto> FindTelegramChats(
        Guid wsId, [FromServices] ICommandHandler<FindTelegramChatsCommand, TelegramChatsDto> handler, CancellationToken ct,
        [FromBody] FindTelegramChatsRequest? r = null) =>
        handler.HandleAsync(new FindTelegramChatsCommand(wsId, r?.Token), ct);
}
