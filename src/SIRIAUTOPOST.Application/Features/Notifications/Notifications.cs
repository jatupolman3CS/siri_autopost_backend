using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Notifications;

// Telegram and LINE notifications of a workspace (a Pro feature). Viewers read, admins edit. The tokens are secrets:
// no response carries one (the DTO says whether one is stored), and a save without a token keeps the stored one.

public sealed record GetNotificationsQuery(Guid WorkspaceId) : IQuery<NotificationSettingsDto>;

/// <summary>Works on every plan: below Pro a workspace simply has the defaults.</summary>
public sealed class GetNotificationsQueryHandler(IWorkspaceRepository workspaces, ICurrentUser current)
    : IQueryHandler<GetNotificationsQuery, NotificationSettingsDto>
{
    public async Task<NotificationSettingsDto> HandleAsync(GetNotificationsQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return NotificationSettingsDto.From(ws.Notifications);
    }
}

public sealed record UpdateNotificationsCommand(Guid WorkspaceId, NotificationSettingsDto Settings) : ICommand<NotificationSettingsDto>;

/// <summary>
/// Saves the settings. Rules about a link set or a group that no longer exists are dropped, so deleted links do not
/// pile up in the document.
/// </summary>
public sealed class UpdateNotificationsCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, ILinkSetRepository linkSets, ISetLinkRepository links, ICurrentUser current,
    IUnitOfWork uow)
    : ICommandHandler<UpdateNotificationsCommand, NotificationSettingsDto>
{
    public async Task<NotificationSettingsDto> HandleAsync(UpdateNotificationsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireNotificationsAsync(ws, ct);

        var settings = c.Settings.ToSettings(ws.Notifications);
        // "Default" means "follow the parent": a set or a group may say it, the workspace has no parent.
        if (settings.Channel == NotifyChannel.Default) throw new DomainException("ช่องทางเริ่มต้นของเวิร์กสเปซต้องเป็น tg, line, both หรือ off");
        var setIds = (await linkSets.ListAsync(ws.Id, ct)).Select(s => s.Id).ToHashSet();
        var linksOfSet = (await links.ListAsync(ws.Id, ct)).GroupBy(l => l.LinkSetId).ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToHashSet());
        foreach (var setId in settings.Sets.Keys.ToList())
        {
            if (!setIds.Contains(setId))
            {
                settings.Sets.Remove(setId);
                continue;
            }
            var rule = settings.Sets[setId];
            var existing = linksOfSet.GetValueOrDefault(setId) ?? [];
            foreach (var linkId in rule.Groups.Keys.Where(id => !existing.Contains(id)).ToList()) rule.Groups.Remove(linkId);
        }

        ws.UpdateNotifications(settings);
        await uow.SaveChangesAsync(ct);
        return NotificationSettingsDto.From(ws.Notifications);
    }
}

/// <param name="Channel">"tg" or "line".</param>
public sealed record SendTestNotificationCommand(Guid WorkspaceId, string Channel) : ICommand<NotifyTestResultDto>;

/// <summary>
/// Sends one short message with the stored settings and says how it went. It goes straight to the gateway (not through
/// the queue), so the answer is the real outcome. The channel need not be switched on yet: testing comes before that.
/// </summary>
public sealed class SendTestNotificationCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, INotificationGateway gateway, ICurrentUser current)
    : ICommandHandler<SendTestNotificationCommand, NotifyTestResultDto>
{
    public const string Tg = "tg";
    public const string Line = "line";

    public async Task<NotifyTestResultDto> HandleAsync(SendTestNotificationCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireNotificationsAsync(ws, ct);

        var n = ws.Notifications;
        var text = $"ทดสอบการแจ้งเตือนจาก AutoPost (เวิร์กสเปซ {ws.Name}) ถ้าเห็นข้อความนี้แสดงว่าตั้งค่าถูกต้อง";
        GatewayResult result;
        string service;
        if (c.Channel == Tg)
        {
            service = "Telegram";
            if (n.Telegram.Token.Length == 0 || n.Telegram.ChatId.Length == 0)
                return new NotifyTestResultDto(false, "ยังไม่ได้ตั้งค่า Telegram: ต้องมีทั้งโทเคนของบอทและรหัสแชท (กดบันทึกก่อนทดสอบ)");
            result = await gateway.SendTelegramAsync(n.Telegram.Token, n.Telegram.ChatId, text, ct);
        }
        else
        {
            service = "LINE";
            if (n.Line.Token.Length == 0 || n.Line.To.Length == 0)
                return new NotifyTestResultDto(false, "ยังไม่ได้ตั้งค่า LINE: ต้องมีทั้งโทเคนและผู้รับ (กดบันทึกก่อนทดสอบ)");
            result = await gateway.SendLineAsync(n.Line.Token, n.Line.To, text, ct);
        }
        return result.Ok
            ? new NotifyTestResultDto(true, $"ส่งข้อความทดสอบไปยัง {service} แล้ว")
            : new NotifyTestResultDto(false, result.Error);
    }
}

/// <param name="Token">The bot token to look chats up with; null or empty = the stored one.</param>
public sealed record FindTelegramChatsCommand(Guid WorkspaceId, string? Token) : ICommand<TelegramChatsDto>;

/// <summary>
/// The chats that wrote to the bot lately, so the person can pick one instead of looking up a chat id. Telegram
/// refusing the token is a 422 with its reason; no chats yet is an empty list.
/// </summary>
public sealed class FindTelegramChatsCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, INotificationGateway gateway, ICurrentUser current)
    : ICommandHandler<FindTelegramChatsCommand, TelegramChatsDto>
{
    public async Task<TelegramChatsDto> HandleAsync(FindTelegramChatsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireNotificationsAsync(ws, ct);

        var token = string.IsNullOrWhiteSpace(c.Token) ? ws.Notifications.Telegram.Token : c.Token.Trim();
        if (token.Length == 0) throw new DomainException("ใส่โทเคนของบอท Telegram ก่อน แล้วค้นหาแชท");
        var found = await gateway.FindTelegramChatsAsync(token, ct);
        if (!found.Ok) throw new DomainException(found.Error!);
        return new TelegramChatsDto(found.Chats.Select(chat => new TelegramChatDto(chat.Id, chat.Title)).ToList());
    }
}
