using System.Collections.Concurrent;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Api.IntegrationTests.Notifications;

/// <summary>
/// Telegram and LINE for the integration tests: nothing leaves the process, every message is recorded. All tests share
/// one instance, so a test picks its own messages out by the (unique) chat id or recipient it set up.
/// </summary>
public sealed class FakeNotificationGateway : INotificationGateway
{
    /// <param name="Channel">"tg" or "line".</param>
    public sealed record Sent(string Channel, string Token, string Target, string Text, bool Html = false)
    {
        /// <summary>The picture of a "tg-photo" message.</summary>
        public byte[]? Photo { get; init; }
    }

    private readonly ConcurrentQueue<Sent> sent = new();

    /// <summary>What the next sends answer (tests that change them must put them back).</summary>
    public GatewayResult TelegramResult { get; set; } = GatewayResult.Success;
    public GatewayResult LineResult { get; set; } = GatewayResult.Success;
    public GatewayResult PhotoResult { get; set; } = GatewayResult.Success;
    /// <summary>When set, every send throws it (a gateway must never do that; the delivery must survive it).</summary>
    public Exception? Throw { get; set; }
    public Func<string, TelegramChatsResult> Chats { get; set; } = _ => new TelegramChatsResult([], null);

    /// <summary>The tokens chat lookups were made with.</summary>
    public ConcurrentQueue<string> Lookups { get; } = new();

    public IReadOnlyList<Sent> All => sent.ToArray();

    /// <summary>The messages that went to one chat id or recipient.</summary>
    public IReadOnlyList<Sent> To(string target) => sent.Where(s => s.Target == target).ToList();

    public Task<GatewayResult> SendTelegramAsync(string token, string chatId, string text, CancellationToken ct = default, bool html = false)
    {
        sent.Enqueue(new Sent("tg", token, chatId, text, html));
        if (Throw is not null) throw Throw;
        return Task.FromResult(TelegramResult);
    }

    public Task<GatewayResult> SendTelegramPhotoAsync(string token, string chatId, byte[] photo, string? caption, CancellationToken ct = default, bool html = false)
    {
        sent.Enqueue(new Sent("tg-photo", token, chatId, caption ?? "", html) { Photo = photo });
        if (Throw is not null) throw Throw;
        return Task.FromResult(PhotoResult);
    }

    public Task<GatewayResult> SendLineAsync(string token, string to, string text, CancellationToken ct = default)
    {
        sent.Enqueue(new Sent("line", token, to, text));
        if (Throw is not null) throw Throw;
        return Task.FromResult(LineResult);
    }

    public Task<TelegramChatsResult> FindTelegramChatsAsync(string token, CancellationToken ct = default)
    {
        Lookups.Enqueue(token);
        return Task.FromResult(Chats(token));
    }
}
