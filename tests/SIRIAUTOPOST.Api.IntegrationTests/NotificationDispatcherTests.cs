using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Api.IntegrationTests.Notifications;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Notifications;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The dispatcher with the workspace's rules (group, then set, then workspace), and the queue and worker behind it.
[Collection(ApiCollection.Name)]
public class NotificationDispatcherTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}";

    private sealed record Shop(HttpClient Owner, Guid Ws, LinkSetDto Set, SetLinkDto A, SetLinkDto B, string Chat, string To);

    /// <summary>
    /// A Pro workspace with a link set of two groups; Telegram and LINE are both on and ready (unique chat and recipient),
    /// the workspace default channel is Telegram and the events are the defaults.
    /// </summary>
    private async Task<Shop> ShopAsync(string channel = "tg", object? events = null, object[]? sets = null)
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var set = await owner.CreateLinkSetAsync(ws);
        var a = await owner.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/a-shop", "A1");
        var b = await owner.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/b-shop", "B1");
        var (chat, to) = (Unique("-100"), Unique("U"));
        var shop = new Shop(owner, ws, set, a, b, chat, to);
        await SaveAsync(shop, channel, events, sets);
        return shop;
    }

    private async Task SaveAsync(Shop s, string channel = "tg", object? events = null, object[]? sets = null) =>
        (await s.Owner.PutAsJsonAsync($"/api/workspaces/{s.Ws}/notifications",
            NotificationsEndpointsTests.Settings("123:tg", s.Chat, tgOn: true, lineToken: "line-token", lineTo: s.To, lineOn: true, channel: channel, events: events, sets: sets), Json))
            .EnsureSuccessStatusCode();

    private static object SetRule(Shop s, string channel = "default", object? events = null, Dictionary<string, object>? groups = null) =>
        new { linkSetId = s.Set.Id, channel, events, groups = groups ?? new Dictionary<string, object>() };

    private static object GroupRule(string channel = "default", object? events = null) => new { channel, events };

    private Task NotifyAsync(NotifyEvent ev, Shop s, bool set = true, SetLinkDto? link = null, string text = "ข้อความทดสอบ") =>
        factory.Services.GetRequiredService<INotificationDispatcher>().NotifyAsync(ev, s.Ws, set ? s.Set.Id : null, link?.Id, text);

    /// <summary>Which channels got messages for the shop: "tg", "line", "tg+line" or "".</summary>
    private string Where(Shop s) =>
        string.Join('+', new[] { factory.Notifications.To(s.Chat).Count > 0 ? "tg" : null, factory.Notifications.To(s.To).Count > 0 ? "line" : null }
            .Where(x => x is not null));

    // ---- resolution order ----

    [Theory]
    [InlineData("tg", "tg")]
    [InlineData("line", "line")]
    [InlineData("both", "tg+line")]
    [InlineData("off", "")]
    public async Task Without_rules_the_workspace_channel_decides(string channel, string expected)
    {
        var s = await ShopAsync(channel);

        await NotifyAsync(NotifyEvent.Fail, s, link: s.A, text: "ล้มเหลว");

        Assert.Equal(expected, Where(s));
        if (expected.Contains("tg"))
        {
            var msg = Assert.Single(factory.Notifications.To(s.Chat));
            Assert.Equal(("tg", "123:tg", "ล้มเหลว"), (msg.Channel, msg.Token, msg.Text)); // the text goes out as it is
        }
    }

    [Fact]
    public async Task A_set_rule_overrides_the_workspace_and_a_group_rule_overrides_the_set()
    {
        var s = await ShopAsync("tg");
        var rule = (Func<string, object[]>)(setChannel => [SetRule(s, setChannel, groups: new Dictionary<string, object>
        {
            [s.A.Id.ToString()] = GroupRule("off"),
            [s.B.Id.ToString()] = GroupRule("both"),
        })]);

        await SaveAsync(s, "tg", sets: rule("line"));
        await NotifyAsync(NotifyEvent.Fail, s); // about the set only: the set's rule (line)
        Assert.Equal("line", Where(s));

        await NotifyAsync(NotifyEvent.Fail, s, link: s.A); // group A is off
        Assert.Single(factory.Notifications.To(s.To));
        Assert.Empty(factory.Notifications.To(s.Chat));

        await NotifyAsync(NotifyEvent.Fail, s, link: s.B); // group B says both
        Assert.Single(factory.Notifications.To(s.Chat));
        Assert.Equal(2, factory.Notifications.To(s.To).Count);

        // A set rule that follows the workspace ("default") lets the workspace's channel through, groups still override.
        await SaveAsync(s, "tg", sets: rule("default"));
        var before = (factory.Notifications.To(s.Chat).Count, factory.Notifications.To(s.To).Count);
        await NotifyAsync(NotifyEvent.Fail, s);
        Assert.Equal((before.Item1 + 1, before.Item2), (factory.Notifications.To(s.Chat).Count, factory.Notifications.To(s.To).Count));

        // Events about another set, or about nothing in particular, follow the workspace.
        var other = await ShopAsync("line");
        await factory.Services.GetRequiredService<INotificationDispatcher>().NotifyAsync(NotifyEvent.Fail, other.Ws, Guid.NewGuid(), Guid.NewGuid(), "x");
        Assert.Equal("line", Where(other));
    }

    [Fact]
    public async Task Events_follow_the_most_specific_rule()
    {
        var s = await ShopAsync("tg"); // workspace: success off, fail on
        await NotifyAsync(NotifyEvent.Success, s, link: s.A);
        Assert.Equal("", Where(s)); // switched off at the workspace level

        // The set turns success on and fail off; group B has no events of its own, group A turns fail back on.
        await SaveAsync(s, "tg", sets:
        [
            SetRule(s, events: NotificationsEndpointsTests.Events(success: true, fail: false), groups: new Dictionary<string, object>
            {
                [s.A.Id.ToString()] = GroupRule(events: NotificationsEndpointsTests.Events(success: false, fail: true)),
            }),
        ]);

        await NotifyAsync(NotifyEvent.Success, s, link: s.B);
        Assert.Single(factory.Notifications.To(s.Chat)); // set: success on
        await NotifyAsync(NotifyEvent.Fail, s, link: s.B);
        Assert.Single(factory.Notifications.To(s.Chat)); // set: fail off
        await NotifyAsync(NotifyEvent.Fail, s, link: s.A);
        Assert.Equal(2, factory.Notifications.To(s.Chat).Count); // group: fail on
        await NotifyAsync(NotifyEvent.Success, s, link: s.A);
        Assert.Equal(2, factory.Notifications.To(s.Chat).Count); // group: success off

        // Events nothing sends yet still obey the rules (Shot and Offline are on by default).
        await NotifyAsync(NotifyEvent.Shot, s, set: false);
        Assert.Equal(3, factory.Notifications.To(s.Chat).Count);
    }

    [Fact]
    public async Task A_channel_is_used_only_when_it_is_on_and_has_its_token_and_target()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (chat, to) = (Unique("-100"), Unique("U"));
        var dispatcher = factory.Services.GetRequiredService<INotificationDispatcher>();
        async Task<string> TryAsync(object body)
        {
            (await owner.PutAsJsonAsync($"/api/workspaces/{ws}/notifications", body, Json)).EnsureSuccessStatusCode();
            var (c0, l0) = (factory.Notifications.To(chat).Count, factory.Notifications.To(to).Count);
            await dispatcher.NotifyAsync(NotifyEvent.Fail, ws, null, null, "x");
            return string.Join('+', new[] { factory.Notifications.To(chat).Count > c0 ? "tg" : null, factory.Notifications.To(to).Count > l0 ? "line" : null }.Where(v => v is not null));
        }

        // Switched off, no chat, no token: nothing, whatever the channel says.
        Assert.Equal("", await TryAsync(NotificationsEndpointsTests.Settings("1:a", chat, tgOn: false, channel: "both", lineToken: "t", lineTo: to, lineOn: false)));
        Assert.Equal("", await TryAsync(NotificationsEndpointsTests.Settings("", chat, tgOn: true, channel: "tg")));
        Assert.Equal("", await TryAsync(NotificationsEndpointsTests.Settings("1:a", "", tgOn: true, channel: "tg")));
        // One ready channel of two: only that one.
        Assert.Equal("line", await TryAsync(NotificationsEndpointsTests.Settings("1:a", chat, tgOn: false, channel: "both", lineToken: "t", lineTo: to, lineOn: true)));
        Assert.Equal("tg+line", await TryAsync(NotificationsEndpointsTests.Settings("1:a", chat, tgOn: true, channel: "both", lineToken: "t", lineTo: to, lineOn: true)));
    }

    // ---- never fails the caller ----

    [Fact]
    public async Task A_gateway_that_fails_or_throws_never_reaches_the_caller()
    {
        var s = await ShopAsync("both");
        factory.Notifications.TelegramResult = GatewayResult.Failure("ล้มเหลว");
        try
        {
            await NotifyAsync(NotifyEvent.Fail, s);
            Assert.Equal("tg+line", Where(s)); // Telegram failed, LINE still got its message
            factory.Notifications.Throw = new InvalidOperationException("boom 123:tg");
            await NotifyAsync(NotifyEvent.Fail, s); // an exception out of a gateway is swallowed too
        }
        finally
        {
            factory.Notifications.TelegramResult = GatewayResult.Success;
            factory.Notifications.Throw = null;
        }
    }

    [Fact]
    public async Task Unknown_workspaces_blank_texts_and_downgraded_owners_send_nothing()
    {
        var s = await ShopAsync("both");
        var dispatcher = factory.Services.GetRequiredService<INotificationDispatcher>();

        await dispatcher.NotifyAsync(NotifyEvent.Fail, Guid.NewGuid(), null, null, "x");
        await NotifyAsync(NotifyEvent.Fail, s, text: "  ");
        Assert.Equal("", Where(s));

        var admin = await factory.AdminAsync();
        var me = (await s.Owner.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
        (await admin.PutAsJsonAsync($"/api/admin/customers/{me.Id}/plan", new { plan = "basic" }, Json)).EnsureSuccessStatusCode();
        await NotifyAsync(NotifyEvent.Fail, s);
        Assert.Equal("", Where(s));
    }

    // ---- the queue and the worker ----

    private (NotificationQueue Queue, QueuedNotificationDispatcher Dispatcher, NotificationWorker Worker) Background(
        INotificationGateway gateway, int capacity = 1000, TimeSpan? grace = null)
    {
        var options = Options.Create(new NotificationOptions { QueueCapacity = capacity, ShutdownGrace = grace ?? TimeSpan.FromSeconds(5) });
        var queue = new NotificationQueue(options, NullLogger<NotificationQueue>.Instance);
        var delivery = new NotificationDelivery(factory.Services.GetRequiredService<IServiceScopeFactory>(), gateway, NullLogger<NotificationDelivery>.Instance);
        return (queue, new QueuedNotificationDispatcher(queue), new NotificationWorker(queue, delivery, options, NullLogger<NotificationWorker>.Instance));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int milliseconds = 5000)
    {
        for (var waited = 0; waited < milliseconds; waited += 20)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task The_worker_sends_queued_messages_in_order_and_stops_cleanly()
    {
        var s = await ShopAsync("tg");
        var gateway = new FakeNotificationGateway();
        var (_, dispatcher, worker) = Background(gateway);
        await worker.StartAsync(CancellationToken.None);

        foreach (var n in new[] { "หนึ่ง", "สอง", "สาม" })
            await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, s.Set.Id, s.A.Id, n); // returns at once: the worker sends

        Assert.True(await WaitForAsync(() => gateway.To(s.Chat).Count == 3));
        Assert.Equal(["หนึ่ง", "สอง", "สาม"], gateway.To(s.Chat).Select(m => m.Text));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "after the stop"); // still never throws
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_messages_and_never_blocks()
    {
        var s = await ShopAsync("tg");
        var gateway = new FakeNotificationGateway();
        var (queue, dispatcher, worker) = Background(gateway, capacity: 2);

        for (var i = 1; i <= 5; i++) await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, $"m{i}"); // nobody is reading yet

        Assert.Equal(3, queue.Dropped);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => gateway.To(s.Chat).Count == 2));
        Assert.Equal(["m4", "m5"], gateway.To(s.Chat).Select(m => m.Text));
        await worker.StopAsync(CancellationToken.None);
    }

    /// <summary>The first send waits (for <see cref="Release"/>, or until it is cancelled); the others go through at once.</summary>
    private sealed class SlowFirstGateway : INotificationGateway
    {
        private int calls;

        public List<string> Delivered { get; } = [];

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GatewayResult> SendTelegramAsync(string token, string chatId, string text, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstStarted.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            lock (Delivered) Delivered.Add(text);
            return GatewayResult.Success;
        }

        public Task<GatewayResult> SendLineAsync(string token, string to, string text, CancellationToken ct = default) => Task.FromResult(GatewayResult.Success);

        public Task<TelegramChatsResult> FindTelegramChatsAsync(string token, CancellationToken ct = default) => Task.FromResult(new TelegramChatsResult([], null));
    }

    [Fact]
    public async Task On_shutdown_the_worker_finishes_the_message_in_flight_and_sends_what_is_still_queued()
    {
        var s = await ShopAsync("tg");
        var gateway = new SlowFirstGateway();
        var (_, dispatcher, worker) = Background(gateway);
        await worker.StartAsync(CancellationToken.None);
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "in flight");
        await gateway.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "queued 1");
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "queued 2");

        var stopping = worker.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted); // it waits for the message in flight
        gateway.Release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["in flight", "queued 1", "queued 2"], gateway.Delivered);
    }

    [Fact]
    public async Task A_stuck_send_does_not_hold_up_shutdown_beyond_the_grace_period()
    {
        var s = await ShopAsync("tg");
        var gateway = new SlowFirstGateway(); // the first send never finishes
        var (_, dispatcher, worker) = Background(gateway, grace: TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(CancellationToken.None);
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "stuck");
        await gateway.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.NotifyAsync(NotifyEvent.Fail, s.Ws, null, null, "dropped with it");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(gateway.Delivered); // the stuck one was given up and the rest dropped with it
    }

    [Fact]
    public async Task The_application_uses_the_inline_dispatcher_under_test_and_the_queue_otherwise()
    {
        // The integration host delivers inline (Notifications:Inline) so tests can assert what was sent.
        Assert.IsType<InlineNotificationDispatcher>(factory.Services.GetRequiredService<INotificationDispatcher>());

        // Without the flag the real registration is the queue with its background worker.
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<INotificationDispatcher, NullNotificationDispatcherForTest>();
        services.AddNotifications(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<QueuedNotificationDispatcher>(provider.GetRequiredService<INotificationDispatcher>()); // it replaced the no-op one
        Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), h => h is NotificationWorker);
        Assert.IsType<HttpNotificationGateway>(provider.GetRequiredService<INotificationGateway>());
        await Task.CompletedTask;
    }

    private sealed class NullNotificationDispatcherForTest : INotificationDispatcher
    {
        public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default) => Task.CompletedTask;
    }
}
