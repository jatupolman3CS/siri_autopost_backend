using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// <see cref="INotificationGateway"/> on Telegram's Bot API and LINE's Messaging API. A call never throws: every failure
/// (a wrong token, a refusal, a timeout, no network) comes back as a short Thai message.
/// <para>
/// Telegram puts the bot token in the URL path, so nothing here may log a request address, and every text that leaves
/// this class (results and logs) is run through <see cref="Redact"/>. Do not move the client to <c>IHttpClientFactory</c>
/// without switching off its request logging: it would write the token to the log.
/// </para>
/// </summary>
public sealed partial class HttpNotificationGateway(HttpClient http, IOptions<NotificationOptions> options, ILogger<HttpNotificationGateway> log)
    : INotificationGateway
{
    public const int TelegramMaxText = 4096;
    public const int LineMaxText = 5000;
    private const int MaxErrorLength = 200;
    private const int UpdatesLimit = 100;

    private readonly NotificationOptions options = options.Value;

    // What a token may look like. Anything else would change the address (Telegram) or the header (LINE).
    [GeneratedRegex(@"^[A-Za-z0-9:_\-]{1,200}$")]
    private static partial Regex TelegramTokenShape();

    [GeneratedRegex(@"^[\x21-\x7E]{1,200}$")]
    private static partial Regex LineTokenShape();

    [GeneratedRegex(@"bot\d+:[A-Za-z0-9_\-]+")]
    private static partial Regex TelegramTokenInText();

    public async Task<GatewayResult> SendTelegramAsync(string token, string chatId, string text, CancellationToken ct = default)
    {
        if (token is null || !TelegramTokenShape().IsMatch(token)) return GatewayResult.Failure("โทเคนของบอท Telegram ไม่ถูกต้อง");
        if (string.IsNullOrWhiteSpace(chatId)) return GatewayResult.Failure("ยังไม่ได้ใส่รหัสแชท Telegram");
        var (response, error) = await CallAsync("Telegram", token, ct, () =>
            new HttpRequestMessage(HttpMethod.Post, TelegramUri(token, "sendMessage"))
            {
                Content = JsonContent.Create(new { chat_id = chatId.Trim(), text = Cut(text, TelegramMaxText) }),
            });
        if (response is null) return GatewayResult.Failure(error!);
        var failure = await TelegramFailureAsync(response, token, ct);
        return failure is null ? GatewayResult.Success : GatewayResult.Failure(failure);
    }

    public async Task<GatewayResult> SendLineAsync(string token, string to, string text, CancellationToken ct = default)
    {
        if (token is null || !LineTokenShape().IsMatch(token)) return GatewayResult.Failure("โทเคนของ LINE ไม่ถูกต้อง");
        if (string.IsNullOrWhiteSpace(to)) return GatewayResult.Failure("ยังไม่ได้ใส่ผู้รับ LINE");
        var (response, error) = await CallAsync("LINE", token, ct, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{options.LineBaseUrl.TrimEnd('/')}/v2/bot/message/push"))
            {
                Content = JsonContent.Create(new { to = to.Trim(), messages = new[] { new { type = "text", text = Cut(text, LineMaxText) } } }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return request;
        });
        if (response is null) return GatewayResult.Failure(error!);
        using (response)
        {
            if (response.IsSuccessStatusCode) return GatewayResult.Success;
            var description = await DescriptionAsync(response, "message", token, ct);
            return GatewayResult.Failure(Failed("LINE", (int)response.StatusCode, description));
        }
    }

    public async Task<TelegramChatsResult> FindTelegramChatsAsync(string token, CancellationToken ct = default)
    {
        if (token is null || !TelegramTokenShape().IsMatch(token)) return TelegramChatsResult.Failure("โทเคนของบอท Telegram ไม่ถูกต้อง");
        var (response, error) = await CallAsync("Telegram", token, ct, () =>
            new HttpRequestMessage(HttpMethod.Get, TelegramUri(
                token, "getUpdates", $"limit={UpdatesLimit}&allowed_updates={Uri.EscapeDataString("[\"message\",\"my_chat_member\",\"channel_post\"]")}")));
        if (response is null) return TelegramChatsResult.Failure(error!);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return TelegramChatsResult.Failure(Failed("Telegram", (int)response.StatusCode, await DescriptionAsync(response, "description", token, ct)));
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                    !doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                    return TelegramChatsResult.Failure("Telegram ตอบกลับในรูปแบบที่ไม่รู้จัก");
                return new TelegramChatsResult(ChatsOf(result), null);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                return TelegramChatsResult.Failure("Telegram ตอบกลับในรูปแบบที่ไม่รู้จัก");
            }
        }
    }

    /// <summary>
    /// The distinct chats of a getUpdates answer, newest first. A chat is named by its title, else its @username, else
    /// the first and last name of the person, else its id.
    /// </summary>
    public static IReadOnlyList<TelegramChat> ChatsOf(JsonElement updates)
    {
        string[] sources = ["message", "edited_message", "channel_post", "edited_channel_post", "my_chat_member"];
        var chats = new List<TelegramChat>();
        var seen = new HashSet<string>();
        foreach (var update in updates.EnumerateArray().Reverse())
            foreach (var source in sources)
            {
                if (!update.TryGetProperty(source, out var body) || body.ValueKind != JsonValueKind.Object) continue;
                if (!body.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object) continue;
                if (!chat.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
                var id = idElement.GetRawText();
                if (!seen.Add(id)) continue;
                chats.Add(new TelegramChat(id, ChatTitle(chat, id)));
            }
        return chats;
    }

    private static string ChatTitle(JsonElement chat, string id)
    {
        string? Text(string name) =>
            chat.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        if (Text("title") is { } title) return title;
        if (Text("username") is { } username) return "@" + username;
        var name = string.Join(' ', new[] { Text("first_name"), Text("last_name") }.Where(n => n is not null));
        return name.Length > 0 ? name : id;
    }

    private Uri TelegramUri(string token, string method, string? query = null) =>
        new($"{options.TelegramBaseUrl.TrimEnd('/')}/bot{token}/{method}" + (query is null ? "" : "?" + query));

    /// <summary>Sends the request with the configured timeout. Either the response, or why there is none.</summary>
    private async Task<(HttpResponseMessage? Response, string? Error)> CallAsync(
        string service, string token, CancellationToken ct, Func<HttpRequestMessage> build)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = build();
            // The body is read after the call returns, so the response is buffered here, under the same timeout.
            return (await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, $"ยกเลิกการส่งไปยัง {service}");
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("{Service} ไม่ตอบกลับภายใน {Seconds} วินาที", service, options.Timeout.TotalSeconds);
            return (null, $"{service} ไม่ตอบกลับภายใน {options.Timeout.TotalSeconds:0.#} วินาที");
        }
        catch (Exception ex)
        {
            // Only the type and the redacted message: the exception object may carry the request address (and so the token).
            log.LogWarning("เชื่อมต่อ {Service} ไม่ได้: {Type} {Message}", service, ex.GetType().Name, Redact(ex.Message, token));
            return (null, $"เชื่อมต่อ {service} ไม่ได้");
        }
    }

    /// <summary>Null when Telegram accepted the message; otherwise why not.</summary>
    private async Task<string?> TelegramFailureAsync(HttpResponseMessage response, string token, CancellationToken ct)
    {
        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                // Telegram answers {"ok":true,...}; a 2xx with "ok": false is still a refusal.
                try
                {
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                        return Failed("Telegram", (int)response.StatusCode, Description(doc.RootElement, "description", token));
                }
                catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
                {
                    // An answer that is not JSON but a success status: believe the status.
                }
                return null;
            }
            return Failed("Telegram", (int)response.StatusCode, await DescriptionAsync(response, "description", token, ct));
        }
    }

    private async Task<string?> DescriptionAsync(HttpResponseMessage response, string property, string token, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return Description(doc.RootElement, property, token);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static string? Description(JsonElement root, string property, string token) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var d) && d.ValueKind == JsonValueKind.String
            ? Cut(Redact(d.GetString(), token), MaxErrorLength)
            : null;

    private string Failed(string service, int status, string? description)
    {
        var message = $"ส่งข้อความไปยัง {service} ไม่สำเร็จ ({status})" + (string.IsNullOrWhiteSpace(description) ? "" : $": {description}");
        log.LogWarning("{Message}", message);
        return message;
    }

    /// <summary>Hides the token, and anything shaped like a Telegram bot address, in a text that may reach a log or a person.</summary>
    public static string Redact(string? text, params string[] secrets)
    {
        var t = text ?? "";
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret)) t = t.Replace(secret, "***", StringComparison.Ordinal);
        return TelegramTokenInText().Replace(t, "bot***");
    }

    /// <summary>At most <paramref name="max"/> characters, without splitting a surrogate pair.</summary>
    private static string Cut(string? text, int max)
    {
        var t = text ?? "";
        if (t.Length <= max) return t;
        var end = max;
        if (char.IsHighSurrogate(t[end - 1])) end--;
        return t[..end];
    }
}
