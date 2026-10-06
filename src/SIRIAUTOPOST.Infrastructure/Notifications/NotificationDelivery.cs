using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>One message waiting to be sent: what happened, where (set and group, when it is about one), the Thai text and, maybe, a picture of the posting window.</summary>
public sealed record NotificationJob(NotifyEvent Event, Guid WorkspaceId, Guid? LinkSetId, Guid? LinkId, string Text, byte[]? Photo = null);

/// <summary>
/// Sends one <see cref="NotificationJob"/>: reads the workspace's settings, applies the rules (group, then set, then
/// workspace) and hands the text to Telegram and/or LINE. Used by the background worker and by the inline dispatcher
/// of the tests. It never throws: a failure is logged (without any token) and the message is dropped.
/// </summary>
public sealed class NotificationDelivery(IServiceScopeFactory scopes, INotificationGateway gateway, ILogger<NotificationDelivery> log)
{
    public async Task DeliverAsync(NotificationJob job, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.Text)) return;
            using var scope = scopes.CreateScope();
            var ws = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().GetByIdAsync(job.WorkspaceId, ct);
            if (ws is null) return;
            // Settings outlive a downgrade, but the feature belongs to Pro and above: below it nothing is sent.
            var owner = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(ws.OwnerId, ct);
            if (owner is null || !owner.HasNotifications) return;

            var settings = ws.Notifications;
            var route = settings.Resolve(job.Event, job.LinkSetId, job.LinkId);
            if (!route.Any) return;

            var sends = new List<Task<(string Service, GatewayResult Result)>>();
            if (route.Telegram)
            {
                // The picture goes along when the "screenshot" event is switched on for this group as well. LINE cannot
                // take one (it only shows a picture by its public address).
                var photo = job.Photo is { Length: > 0 } && settings.Resolve(NotifyEvent.Shot, job.LinkSetId, job.LinkId).Telegram ? job.Photo : null;
                sends.Add(Send("Telegram", SendTelegramAsync(settings.Telegram, job.Text, photo, ct)));
            }
            if (route.Line)
                sends.Add(Send("LINE", gateway.SendLineAsync(settings.Line.Token, settings.Line.To, NotificationText.ToPlain(job.Text), ct)));
            foreach (var (service, result) in await Task.WhenAll(sends))
                if (!result.Ok) log.LogWarning("แจ้งเตือน {Event} ผ่าน {Service} ไม่สำเร็จ: {Error}", job.Event, service, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the message is dropped.
        }
        catch (Exception ex)
        {
            // Only the type: a message may carry an address, and so a token.
            log.LogError("ส่งการแจ้งเตือนไม่สำเร็จ ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// The text, with the picture as its caption when it fits (Telegram allows 1,024 characters there; a picture that
    /// is refused still leaves the text), else the text first and the picture after it.
    /// </summary>
    private async Task<GatewayResult> SendTelegramAsync(TelegramChannel telegram, string text, byte[]? photo, CancellationToken ct)
    {
        if (photo is null) return await gateway.SendTelegramAsync(telegram.Token, telegram.ChatId, text, ct, html: true);
        if (NotificationText.ToPlain(text).Length <= NotificationText.TelegramCaptionMax)
        {
            var withPhoto = await gateway.SendTelegramPhotoAsync(telegram.Token, telegram.ChatId, photo, text, ct, html: true);
            if (withPhoto.Ok) return withPhoto;
            log.LogWarning("ส่งรูปไป Telegram ไม่สำเร็จ ส่งเฉพาะข้อความแทน: {Error}", withPhoto.Error);
            return await gateway.SendTelegramAsync(telegram.Token, telegram.ChatId, text, ct, html: true);
        }
        var sent = await gateway.SendTelegramAsync(telegram.Token, telegram.ChatId, text, ct, html: true);
        if (sent.Ok)
        {
            var picture = await gateway.SendTelegramPhotoAsync(telegram.Token, telegram.ChatId, photo, null, ct);
            if (!picture.Ok) log.LogWarning("ส่งรูปไป Telegram ไม่สำเร็จ: {Error}", picture.Error);
        }
        return sent;
    }

    private static async Task<(string, GatewayResult)> Send(string service, Task<GatewayResult> send) => (service, await send);
}
