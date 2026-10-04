using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Infrastructure.Notifications;

namespace SIRIAUTOPOST.Api.IntegrationTests.Notifications;

// The real gateway against a fake HTTP handler: request shapes, failures that never throw, tokens that never leak.
public class HttpNotificationGatewayTests
{
    private const string Token = "123456:ABC-secret_token";
    private const string LineToken = "line-channel-secret-token";

    private sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string? Body);

    private sealed class FakeHandler(Func<Captured, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("Authorization", out var auth);
            var captured = new Captured(request.Method, request.RequestUri!, auth?.SingleOrDefault(), request.Content?.Headers.ContentType?.MediaType, body);
            lock (Requests) Requests.Add(captured);
            return await respond(captured, ct);
        }
    }

    private sealed class CapturingLogger : ILogger<HttpNotificationGateway>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpNotificationGateway Gateway, FakeHandler Handler, CapturingLogger Log) Make(
        Func<Captured, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var log = new CapturingLogger();
        var options = Options.Create(new NotificationOptions
        {
            TelegramBaseUrl = "https://tg.test/", LineBaseUrl = "https://line.test", Timeout = timeout ?? TimeSpan.FromSeconds(10),
        });
        return (new HttpNotificationGateway(new HttpClient(handler), options, log), handler, log);
    }

    private static (HttpNotificationGateway Gateway, FakeHandler Handler, CapturingLogger Log) Make(HttpStatusCode status, string json) =>
        Make((_, _) => Task.FromResult(Json(status, json)));

    // ---- request shapes ----

    [Fact]
    public async Task Telegram_messages_are_posted_to_sendMessage_with_the_chat_and_the_text()
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":7}}""");

        var result = await gateway.SendTelegramAsync(Token, " -100123 ", "โพสต์สำเร็จ: กลุ่มร้านค้า");

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://tg.test/bot{Token}/sendMessage", request.Uri.ToString());
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("-100123", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("โพสต์สำเร็จ: กลุ่มร้านค้า", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count()); // the text as it is: no decoration
    }

    [Fact]
    public async Task LINE_messages_are_pushed_with_a_bearer_token_and_one_text_message()
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, "{}");

        var result = await gateway.SendLineAsync(LineToken, "U1234567890", "หยุดโพสต์แล้ว");

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://line.test/v2/bot/message/push", request.Uri.ToString());
        Assert.Equal($"Bearer {LineToken}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("U1234567890", body.RootElement.GetProperty("to").GetString());
        var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("text", message.GetProperty("type").GetString());
        Assert.Equal("หยุดโพสต์แล้ว", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Long_texts_are_cut_to_what_each_service_accepts()
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, """{"ok":true}""");
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 3000)); // 2 UTF-16 units each: a cut must not split one

        await gateway.SendTelegramAsync(Token, "1", new string('ก', 5000));
        await gateway.SendTelegramAsync(Token, "1", emoji);
        await gateway.SendLineAsync(LineToken, "U1", new string('ก', 6000));

        string TextOf(Captured r)
        {
            using var doc = JsonDocument.Parse(r.Body!);
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()! : doc.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;
        }
        Assert.Equal(HttpNotificationGateway.TelegramMaxText, TextOf(handler.Requests[0]).Length);
        var cutEmoji = TextOf(handler.Requests[1]);
        Assert.True(cutEmoji.Length <= HttpNotificationGateway.TelegramMaxText && cutEmoji.Length % 2 == 0);
        Assert.False(char.IsHighSurrogate(cutEmoji[^1]));
        Assert.Equal(HttpNotificationGateway.LineMaxText, TextOf(handler.Requests[2]).Length);
    }

    // ---- failures never throw and never leak the token ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"ok":false,"error_code":401,"description":"Unauthorized"}""", "401", "Unauthorized")]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""", "400", "chat not found")]
    [InlineData(HttpStatusCode.OK, """{"ok":false,"description":"Forbidden: bot was blocked by the user"}""", "200", "blocked by the user")]
    [InlineData(HttpStatusCode.InternalServerError, "", "500", "")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "502", "")]
    public async Task Telegram_refusals_come_back_as_a_short_thai_message(HttpStatusCode status, string body, string code, string reason)
    {
        var (gateway, _, log) = Make(status, body);

        var result = await gateway.SendTelegramAsync(Token, "1", "x");

        Assert.False(result.Ok);
        Assert.StartsWith("ส่งข้อความไปยัง Telegram ไม่สำเร็จ", result.Error);
        Assert.Contains(code, result.Error);
        Assert.Contains(reason, result.Error);
        Assert.DoesNotContain(Token, result.Error);
        Assert.DoesNotContain(Token, string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task A_success_status_without_a_json_answer_counts_as_sent()
    {
        var (gateway, _, _) = Make(HttpStatusCode.OK, "ok");

        Assert.True((await gateway.SendTelegramAsync(Token, "1", "x")).Ok);
    }

    [Fact]
    public async Task LINE_refusals_say_why()
    {
        var (gateway, _, _) = Make(HttpStatusCode.Unauthorized, """{"message":"Authentication failed due to the following reason: invalid token."}""");

        var result = await gateway.SendLineAsync(LineToken, "U1", "x");

        Assert.False(result.Ok);
        Assert.Contains("LINE", result.Error);
        Assert.Contains("401", result.Error);
        Assert.Contains("invalid token", result.Error);
        Assert.DoesNotContain(LineToken, result.Error);
    }

    [Fact]
    public async Task A_token_echoed_by_the_service_or_an_exception_is_redacted_everywhere()
    {
        var echo = Make(HttpStatusCode.Unauthorized, $$"""{"ok":false,"description":"bad {{Token}} at /bot{{Token}}/sendMessage"}""");
        var failed = await echo.Gateway.SendTelegramAsync(Token, "1", "x");
        Assert.DoesNotContain("ABC-secret_token", failed.Error);
        Assert.DoesNotContain("ABC-secret_token", string.Join("\n", echo.Log.Lines));

        // The exception text of the HTTP stack may contain the address, which holds the token.
        var thrown = Make((r, _) => throw new HttpRequestException($"connection refused ({r.Uri})"));
        var down = await thrown.Gateway.SendTelegramAsync(Token, "1", "x");
        Assert.False(down.Ok);
        Assert.Equal("เชื่อมต่อ Telegram ไม่ได้", down.Error);
        Assert.DoesNotContain("ABC-secret_token", string.Join("\n", thrown.Log.Lines));
        Assert.NotEmpty(thrown.Log.Lines); // the failure was logged, without the token

        var line = Make((_, _) => throw new InvalidOperationException($"boom {LineToken}"));
        var lineDown = await line.Gateway.SendLineAsync(LineToken, "U1", "x");
        Assert.Equal("เชื่อมต่อ LINE ไม่ได้", lineDown.Error);
        Assert.DoesNotContain(LineToken, string.Join("\n", line.Log.Lines));
    }

    [Fact]
    public async Task A_service_that_does_not_answer_times_out_without_throwing()
    {
        var (gateway, _, log) = Make(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, "{}");
        }, TimeSpan.FromMilliseconds(150));

        var clock = Stopwatch.StartNew();
        var tg = await gateway.SendTelegramAsync(Token, "1", "x");
        var line = await gateway.SendLineAsync(LineToken, "U1", "x");
        var chats = await gateway.FindTelegramChatsAsync(Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(tg.Ok);
        Assert.Contains("ไม่ตอบกลับ", tg.Error);
        Assert.Contains("ไม่ตอบกลับ", line.Error);
        Assert.Contains("ไม่ตอบกลับ", chats.Error);
        Assert.DoesNotContain(Token, string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task A_cancelled_send_says_so()
    {
        var (gateway, _, _) = Make(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, "{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await gateway.SendTelegramAsync(Token, "1", "x", cts.Token);

        Assert.False(result.Ok);
        Assert.Contains("ยกเลิก", result.Error);
    }

    [Theory]
    [InlineData("12 34:abc")]
    [InlineData("../../x")]
    [InlineData("a/b:c")]
    [InlineData("tok\nen:abc")]
    [InlineData("")]
    public async Task A_token_that_could_change_the_address_is_refused_before_any_request(string token)
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, """{"ok":true}""");

        Assert.False((await gateway.SendTelegramAsync(token, "1", "x")).Ok);
        Assert.False((await gateway.FindTelegramChatsAsync(token)).Ok);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("line token")]
    [InlineData("tok\r\nX-Injected: 1")]
    [InlineData("")]
    public async Task A_LINE_token_that_could_change_the_headers_is_refused_before_any_request(string token)
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, "{}");

        Assert.False((await gateway.SendLineAsync(token, "U1", "x")).Ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_missing_chat_or_recipient_is_refused_before_any_request()
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, """{"ok":true}""");

        Assert.False((await gateway.SendTelegramAsync(Token, " ", "x")).Ok);
        Assert.False((await gateway.SendLineAsync(LineToken, "", "x")).Ok);
        Assert.Empty(handler.Requests);
    }

    // ---- finding chats ----

    private const string Updates = """
        {"ok":true,"result":[
          {"update_id":1,"message":{"message_id":1,"chat":{"id":42,"first_name":"สมชาย","last_name":"ใจดี","username":"somchai","type":"private"},"text":"hi"}},
          {"update_id":2,"my_chat_member":{"chat":{"id":-100123,"title":"ร้านค้า","type":"supergroup"},"new_chat_member":{"status":"member"}}},
          {"update_id":3,"channel_post":{"message_id":9,"chat":{"id":-100999,"title":"ประกาศ","username":"news","type":"channel"}}},
          {"update_id":4,"message":{"message_id":2,"chat":{"id":42,"first_name":"สมชาย","username":"somchai","type":"private"}}},
          {"update_id":5,"message":{"message_id":3,"chat":{"id":77,"first_name":"Ann","last_name":"Lee","type":"private"}}},
          {"update_id":6,"message":{"message_id":4,"chat":{"id":88,"username":"bob","type":"private"}}},
          {"update_id":7,"message":{"message_id":5,"chat":{"id":99,"type":"private"}}},
          {"update_id":8,"callback_query":{"id":"x"}}
        ]}
        """;

    [Fact]
    public async Task Chats_that_wrote_to_the_bot_are_listed_newest_first_without_repeats()
    {
        var (gateway, handler, _) = Make(HttpStatusCode.OK, Updates);

        var found = await gateway.FindTelegramChatsAsync(Token);

        Assert.True(found.Ok);
        Assert.Equal(
            [("99", "99"), ("88", "@bob"), ("77", "Ann Lee"), ("42", "@somchai"), ("-100999", "ประกาศ"), ("-100123", "ร้านค้า")],
            found.Chats.Select(c => (c.Id, c.Title)));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith($"https://tg.test/bot{Token}/getUpdates?", request.Uri.ToString());
        Assert.Contains("allowed_updates=", request.Uri.Query);
        Assert.Contains("channel_post", Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact]
    public async Task No_messages_yet_is_an_empty_list_and_a_refusal_is_an_error()
    {
        var empty = await Make(HttpStatusCode.OK, """{"ok":true,"result":[]}""").Gateway.FindTelegramChatsAsync(Token);
        Assert.True(empty.Ok);
        Assert.Empty(empty.Chats);

        var wrong = await Make(HttpStatusCode.Unauthorized, """{"ok":false,"error_code":401,"description":"Unauthorized"}""").Gateway.FindTelegramChatsAsync(Token);
        Assert.False(wrong.Ok);
        Assert.Contains("401", wrong.Error);
        Assert.Empty(wrong.Chats);

        var webhook = await Make(HttpStatusCode.Conflict, """{"ok":false,"error_code":409,"description":"Conflict: can't use getUpdates method while webhook is active"}""")
            .Gateway.FindTelegramChatsAsync(Token);
        Assert.Contains("webhook", webhook.Error);

        foreach (var junk in new[] { "<html/>", """{"ok":false}""", """{"ok":true}""", """{"ok":true,"result":{}}""", "" })
        {
            var result = await Make(HttpStatusCode.OK, junk).Gateway.FindTelegramChatsAsync(Token);
            Assert.False(result.Ok);
            Assert.Equal("Telegram ตอบกลับในรูปแบบที่ไม่รู้จัก", result.Error);
        }
    }

    [Fact]
    public void Redact_hides_the_secret_and_anything_shaped_like_a_bot_address()
    {
        Assert.Equal("a *** b", HttpNotificationGateway.Redact("a secret-1 b", "secret-1"));
        Assert.Equal("https://api.telegram.org/bot***/sendMessage", HttpNotificationGateway.Redact("https://api.telegram.org/bot987654:AAE-xyz_1/sendMessage"));
        Assert.Equal("", HttpNotificationGateway.Redact(null, "x"));
        Assert.Equal("keep", HttpNotificationGateway.Redact("keep", "", "nothing"));
    }
}
