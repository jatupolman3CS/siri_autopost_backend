using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The storage and contract changes the collection / link set / schedule workflow rests on.
[Collection(ApiCollection.Name)]
public class WorkflowFoundationTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    // ---- the contract of existing endpoints ----

    [Fact]
    public async Task Workspaces_say_which_features_the_owners_plan_includes()
    {
        async Task<WorkspaceDto> Of(string? plan)
        {
            var (client, _, ws) = await factory.SignUpAsync(plan);
            return (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(w => w.Id == ws);
        }

        var free = await Of(null);
        Assert.Equal((false, false, false, false), (free.AdvancedAntiBan, free.Notifications, free.AutoReply, free.ClientReports));
        var basic = await Of("basic");
        Assert.Equal((false, false, false, false), (basic.AdvancedAntiBan, basic.Notifications, basic.AutoReply, basic.ClientReports));
        var pro = await Of("pro");
        Assert.Equal((true, true, true, false), (pro.AdvancedAntiBan, pro.Notifications, pro.AutoReply, pro.ClientReports));
        var agency = await Of("agency");
        Assert.Equal((true, true, true, true), (agency.AdvancedAntiBan, agency.Notifications, agency.AutoReply, agency.ClientReports));

        // A new workspace of a Pro owner has them from the start, and a member sees the owner's plan, not their own.
        var (owner, _, _) = await factory.SignUpAsync("agency");
        var created = (await (await owner.PostAsJsonAsync("/api/workspaces", new { name = "ใหม่" }, Json)).Content.ReadFromJsonAsync<WorkspaceDto>(Json))!;
        Assert.Equal((true, true, true, true), (created.AdvancedAntiBan, created.Notifications, created.AutoReply, created.ClientReports));
        var team = await factory.TeamAsync();
        var seen = (await team.Viewer.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(w => w.Id == team.Ws);
        Assert.True(seen.ClientReports);
    }

    [Fact]
    public async Task The_advanced_anti_ban_numbers_start_at_the_defaults_and_pro_can_change_them()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var engine = (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;
        Assert.Equal(new AdvancedAntiBanDto(2, 0, 24, 48, 4, 10, 0, true, 3, 30), engine.AntiBan.Advanced);

        var changed = engine.AntiBan with { Advanced = new AdvancedAntiBanDto(10, 120, 12, 36, 6, 20, 6, false, 5, 50), Min = 4, Max = 15 };
        var saved = (await (await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", changed, Json)).Content.ReadFromJsonAsync<EngineSettingsDto>(Json))!;
        Assert.Equal(changed.Advanced, saved.AntiBan.Advanced);
        Assert.Equal(changed.Advanced, (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!.AntiBan.Advanced);

        var bad = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban",
            changed with { Advanced = changed.Advanced with { BlockMin = 48, BlockMax = 24 } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var outOfRange = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", changed with { Advanced = changed.Advanced with { StopFailPct = 101 } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOfRange.StatusCode);
        Assert.Equal(changed.Advanced, (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!.AntiBan.Advanced);
    }

    [Fact]
    public async Task Lower_plans_keep_the_advanced_numbers_they_have()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var engine = (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;

        var res = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban",
            engine.AntiBan with { Min = 5, Max = 20, Advanced = new AdvancedAntiBanDto(30, 99, 5, 6, 1, 1, 1, false, 1, 1) }, Json);
        var saved = (await res.Content.ReadFromJsonAsync<EngineSettingsDto>(Json))!;

        Assert.Equal(5, saved.AntiBan.Min);
        Assert.Equal(engine.AntiBan.Advanced, saved.AntiBan.Advanced);
    }

    [Fact]
    public async Task A_client_that_does_not_know_the_advanced_numbers_keeps_them()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var engine = (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;
        var custom = engine.AntiBan with { Advanced = engine.AntiBan.Advanced with { MinGap = 15 } };
        await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", custom, Json);

        var legacy = new
        {
            min = 6, max = 18, limits = engine.AntiBan.Limits, typing = true, scroll = true, shuffle = true, autoPause = true, warmup = false,
        };
        var res = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", legacy, Json);

        // Either the request is refused as incomplete or the stored numbers are kept; they are never reset.
        if (res.IsSuccessStatusCode)
            Assert.Equal(15, (await res.Content.ReadFromJsonAsync<EngineSettingsDto>(Json))!.AntiBan.Advanced.MinGap);
        else
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(15, (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!.AntiBan.Advanced.MinGap);
    }

    [Fact]
    public async Task A_workspace_stored_before_the_advanced_numbers_reads_with_the_defaults()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        // The shape the column had before this change (the migration fills new keys in; this checks a row that missed it).
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $$"""UPDATE "WORKSPACES" SET anti_ban = '{"Max": 12, "Min": 3, "Limits": {"X": 20, "Fb": 40, "Ig": 10, "Th": 10, "Tt": 5, "Line": 3}, "Scroll": true, "Typing": true, "Warmup": false, "Shuffle": true, "AutoPause": true}'::jsonb WHERE id = {{ws}}"""));

        var engine = await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json);

        Assert.Equal(new AdvancedAntiBanDto(2, 0, 24, 48, 4, 10, 0, true, 3, 30), engine!.AntiBan.Advanced);
    }

    [Fact]
    public async Task Posts_say_where_they_came_from()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var scheduleId = Guid.NewGuid();
        var (cp, link) = (Guid.NewGuid(), Guid.NewGuid());
        var at = DateTimeOffset.UtcNow.AddHours(3);
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == pair.AccountId);
            db.Posts.Add(Post.FromSchedule(ws, account, "plants", "ข้อความ", [], at, DateTimeOffset.UtcNow, scheduleId, cp, link, Post.LinkTargetKey(link), "2026-10-05T10:00",
                "https://www.facebook.com/groups/plants", "AB12"));
            db.Posts.Add(Post.Test(ws, account, "plants", "ทดสอบ", [], DateTimeOffset.UtcNow, null, null, null, null));
            await db.SaveChangesAsync();
        });

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(4).ToString("O"));
        var posts = (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?from={from}&to={to}", Json))!
            .Where(p => p.AccountId == pair.AccountId).ToList();

        var scheduled = posts.Single(p => !p.IsTest);
        Assert.Equal((scheduleId, cp, link, "AB12", "https://www.facebook.com/groups/plants"),
            (scheduled.ScheduleId, scheduled.CollectionPostId, scheduled.LinkId, scheduled.Code, scheduled.TargetUrl));
        var test = posts.Single(p => p.IsTest);
        Assert.Equal((null, null, null, null, null), ((Guid?)test.ScheduleId, (Guid?)test.CollectionPostId, (Guid?)test.LinkId, test.Code, test.TargetUrl));

        // Posts of the old composer carry no origin.
        var accounts = (await client.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        (await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "จากตัวเขียนเดิม", startAt = DateTimeOffset.UtcNow.AddHours(2), repeat = "none",
            targets = new[] { new { accountId = accounts.First(a => a.Platform == Platform.Ig).Id } },
        }, Json)).EnsureSuccessStatusCode();
        var legacy = (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?from={from}&to={to}", Json))!.Single(p => p.Content == "จากตัวเขียนเดิม");
        Assert.Equal((false, null, null, null), (legacy.IsTest, (Guid?)legacy.ScheduleId, (Guid?)legacy.LinkId, legacy.Code));
    }

    [Fact]
    public async Task A_notification_for_a_workspace_that_does_not_exist_is_dropped_quietly()
    {
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
        var before = factory.Notifications.All.Count;
        await dispatcher.NotifyAsync(NotifyEvent.Fail, Guid.NewGuid(), null, null, "ทดสอบ");
        Assert.Equal(before, factory.Notifications.All.Count);
    }

    // ---- what is stored ----

    [Fact]
    public async Task Notification_and_auto_reply_settings_are_stored_as_documents()
    {
        var (_, _, ws) = await factory.SignUpAsync("pro");
        var (set, link) = (Guid.NewGuid(), Guid.NewGuid());

        // A new workspace starts with the defaults.
        var fresh = await factory.WithDbAsync(async db => await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == ws));
        Assert.Equal((false, NotifyChannel.Tg, true), (fresh.Notifications.Telegram.On, fresh.Notifications.Channel, fresh.Notifications.Events.Fail));
        Assert.False(fresh.AutoReply.On);

        await factory.WithDbAsync(async db =>
        {
            var w = await db.Workspaces.SingleAsync(x => x.Id == ws);
            var n = new NotificationSettings
            {
                Telegram = new TelegramChannel { On = true, Token = "tg-secret", ChatId = "-100123" },
                Line = new LineChannel { On = true, Token = "line-secret", To = "U1" },
                Channel = NotifyChannel.Both,
                Events = new NotifyEventSet { Success = true, Quota = true },
                CommandsOn = true,
                CommandsUsers = "@boss",
            };
            n.Sets[set] = new SetNotifyRule { Channel = NotifyChannel.Line, Events = new NotifyEventSet { Fail = false, Quota = true } };
            n.Sets[set].Groups[link] = new GroupNotifyRule { Channel = NotifyChannel.Off };
            w.UpdateNotifications(n);
            var rule = new AutoReplyRule { Keywords = "ราคา, สนใจ", Reply = "ทักแชทนะคะ", Inbox = "ส่งราคาให้ทางแชท", Scope = "all" };
            w.UpdateAutoReply(new AutoReplySettings { On = true, Rules = [rule] });
            await db.SaveChangesAsync();
        });

        var loaded = await factory.WithDbAsync(async db => await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == ws));
        Assert.Equal(("tg-secret", "-100123", "line-secret", NotifyChannel.Both, "@boss"),
            (loaded.Notifications.Telegram.Token, loaded.Notifications.Telegram.ChatId, loaded.Notifications.Line.Token, loaded.Notifications.Channel, loaded.Notifications.CommandsUsers));
        Assert.True(loaded.Notifications.Events.Success);
        Assert.Equal(NotifyChannel.Line, loaded.Notifications.Sets[set].Channel);
        Assert.False(loaded.Notifications.Sets[set].Events!.Fail);
        Assert.Equal(NotifyChannel.Off, loaded.Notifications.Sets[set].Groups[link].Channel);
        Assert.Null(loaded.Notifications.Sets[set].Groups[link].Events);
        Assert.Equal(new NotifyRoute(false, true), loaded.Notifications.Resolve(NotifyEvent.Quota, set, null));
        Assert.True(loaded.AutoReply.On);
        Assert.Equal("ราคา, สนใจ", loaded.AutoReply.Rules.Single().Keywords);

        // Saving the same content again changes nothing (change tracking compares what is stored).
        await factory.WithDbAsync(async db =>
        {
            var w = await db.Workspaces.SingleAsync(x => x.Id == ws);
            Assert.False(db.ChangeTracker.HasChanges());
            w.UpdateAutoReply(new AutoReplySettings { On = true, Rules = [new AutoReplyRule { Id = w.AutoReply.Rules[0].Id, Keywords = "ราคา, สนใจ", Reply = "ทักแชทนะคะ", Inbox = "ส่งราคาให้ทางแชท" }] });
            Assert.DoesNotContain(db.ChangeTracker.Entries<Workspace>(), e => e.Property(nameof(Workspace.AutoReply)).IsModified);
        });
    }

    [Fact]
    public async Task A_workspace_row_from_before_the_documents_reads_with_defaults()
    {
        var (_, _, ws) = await factory.SignUpAsync();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($$"""UPDATE "WORKSPACES" SET notifications = '{}'::jsonb, auto_reply = '{}'::jsonb WHERE id = {{ws}}"""));

        var loaded = await factory.WithDbAsync(async db => await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == ws));

        Assert.Equal((false, "", NotifyChannel.Tg), (loaded.Notifications.Telegram.On, loaded.Notifications.Line.Token, loaded.Notifications.Channel));
        Assert.Empty(loaded.Notifications.Sets);
        Assert.Empty(loaded.AutoReply.Rules);
    }

    [Fact]
    public async Task A_schedule_keeps_everything_it_was_made_with()
    {
        var (_, _, ws) = await factory.SignUpAsync();
        var (collection, set, link) = (Guid.Empty, Guid.Empty, Guid.NewGuid());
        await factory.WithDbAsync(async db =>
        {
            var c = PostCollection.Create(ws, "c", null, DateTimeOffset.UtcNow);
            var s = LinkSet.Create(ws, "s", null, DateTimeOffset.UtcNow);
            db.Collections.Add(c);
            db.LinkSets.Add(s);
            await db.SaveChangesAsync();
            (collection, set) = (c.Id, s.Id);
        });
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var made = Schedule.Create(ws, "ตาราง", collection, set, ScheduleMode.Drip, ["09:00"], 4, "08:30", start, "15:45", PostOrder.Rotate,
            "10:00", "20:00", 5, 12, 7, new Dictionary<string, IReadOnlyList<string>> { [Schedule.LinkKey(link)] = ["20:00", "06:00"], [Schedule.AccountKey(Guid.NewGuid())] = ["12:00"] },
            -300, DateTimeOffset.UtcNow);
        made.SetActive(false);
        made.MarkGenerated(new DateOnly(2026, 11, 15), 9);
        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Add(made);
            await db.SaveChangesAsync();
        });

        var s = await factory.WithDbAsync(db => db.Schedules.AsNoTracking().SingleAsync(x => x.Id == made.Id));

        Assert.Equal(("ตาราง", ScheduleMode.Drip, PostOrder.Rotate, 4, "08:30", "15:45"), (s.Name, s.Mode, s.Order, s.EveryHours, s.FirstTime, s.OnceTime));
        Assert.Equal((start, "10:00", "20:00", 5, 12, 7), (s.StartDate, s.DripFrom, s.DripTo, s.DripCount, s.BumpHours, s.AutoDeleteDays));
        Assert.Equal((false, -300, new DateOnly(2026, 11, 15), 9), (s.Active, s.UtcOffsetMinutes, s.GeneratedThrough, s.Cursor));
        Assert.Equal(["09:00"], s.Times);
        Assert.Equal(["06:00", "20:00"], s.Overrides[Schedule.LinkKey(link)]);
        Assert.Equal(2, s.Overrides.Count);
        Assert.Equal(made.Slots(), s.Slots());

        // Changing an override in place is noticed by change tracking.
        await factory.WithDbAsync(async db =>
        {
            var tracked = await db.Schedules.SingleAsync(x => x.Id == made.Id);
            tracked.Overrides[Schedule.LinkKey(link)].Add("23:00");
            Assert.True(db.ChangeTracker.HasChanges());
        });
    }

    [Fact]
    public async Task A_schedule_cannot_post_the_same_target_and_slot_twice()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var schedule = Guid.NewGuid();
        var link = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        async Task AddAsync(Guid? scheduleId, string slot)
        {
            await factory.WithDbAsync(async db =>
            {
                var account = await db.Accounts.SingleAsync(a => a.Id == pair.AccountId);
                db.Posts.Add(scheduleId is { } s
                    ? Post.FromSchedule(ws, account, "g", "x", [], now.AddHours(1), now, s, Guid.NewGuid(), link, Post.LinkTargetKey(link), slot, null, null)
                    : Post.Test(ws, account, "g", "x", [], now, null, link, null, null));
                await db.SaveChangesAsync();
            });
        }

        await AddAsync(schedule, "2026-10-05T10:00");
        await Assert.ThrowsAsync<DuplicateKeyException>(() => AddAsync(schedule, "2026-10-05T10:00"));
        await AddAsync(schedule, "2026-10-05T11:00"); // another slot
        await AddAsync(Guid.NewGuid(), "2026-10-05T10:00"); // another schedule
        await AddAsync(null, "");
        await AddAsync(null, ""); // posts without a schedule are not constrained
    }

    [Fact]
    public async Task A_device_remembers_its_auto_pause()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var until = DateTimeOffset.UtcNow.AddHours(30);

        await factory.WithDbAsync(async db =>
        {
            var device = await db.Devices.SingleAsync(d => d.Id == pair.DeviceId);
            device.AutoPause(until, "Facebook บล็อก", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        });

        var loaded = await factory.WithDbAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.Id == pair.DeviceId));
        Assert.Equal("Facebook บล็อก", loaded.AutoPauseReason);
        Assert.True(loaded.IsAutoPaused(DateTimeOffset.UtcNow));
        Assert.Equal(until.ToUnixTimeSeconds(), loaded.AutoPausedUntil!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Report_shares_are_found_by_token_and_expire()
    {
        var (_, _, ws) = await factory.SignUpAsync("agency");
        var now = DateTimeOffset.UtcNow;
        var token = ReportShare.NewToken();
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.NotEqual(token, ReportShare.NewToken());

        await factory.WithDbAsync(async db =>
        {
            db.ReportShares.Add(ReportShare.Create(ws, token, "Smile+", "week", true, """{"brand":"Smile+"}""", now));
            db.ReportShares.Add(ReportShare.Create(ws, ReportShare.NewToken(), "Old", "month", false, "{}", now.AddDays(-40)));
            await db.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var shares = scope.ServiceProvider.GetRequiredService<IReportShareRepository>();
        var found = await shares.GetByTokenAsync(token);
        Assert.Equal(("Smile+", "week", true), (found!.Brand, found.Period, found.ShowLogo));
        Assert.Equal("""{"brand":"Smile+"}""", found.SnapshotJson);
        Assert.False(found.IsExpired(now.AddDays(29)));
        Assert.True(found.IsExpired(now.AddDays(31)));
        Assert.Null(await shares.GetByTokenAsync(ReportShare.NewToken()));
        Assert.Equal(1, await shares.CountActiveAsync(ws, now));

        await shares.DeleteExpiredAsync(now);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ReportShares.CountAsync(r => r.WorkspaceId == ws)));
    }

    [Fact]
    public async Task Everything_of_a_workspace_goes_when_it_is_deleted()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);
        await client.AddPostAsync(ws, c.Id);
        var set = await client.CreateLinkSetAsync(ws);
        await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/x");
        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Add(Schedule.Create(ws, "s", c.Id, set.Id, ScheduleMode.Daily, ["09:00"], 6, "09:00", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "14:00", PostOrder.Shuffle,
                "09:00", "21:00", 3, 0, 0, null, 420, DateTimeOffset.UtcNow));
            db.ReportShares.Add(ReportShare.Create(ws, ReportShare.NewToken(), "b", "week", false, "{}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        // The database cascades from the workspace to everything in it, schedules (which restrict their collection and set) included.
        await factory.WithDbAsync(db => db.Workspaces.Where(w => w.Id == ws).ExecuteDeleteAsync());

        Assert.Equal(0, await factory.WithDbAsync(async db =>
            await db.Collections.CountAsync(x => x.WorkspaceId == ws) + await db.CollectionPosts.CountAsync(x => x.WorkspaceId == ws) +
            await db.LinkSets.CountAsync(x => x.WorkspaceId == ws) + await db.SetLinks.CountAsync(x => x.WorkspaceId == ws) +
            await db.Schedules.CountAsync(x => x.WorkspaceId == ws) + await db.ReportShares.CountAsync(x => x.WorkspaceId == ws)));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_delete_a_collection_or_set_a_schedule_uses()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);
        var set = await client.CreateLinkSetAsync(ws);
        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Add(Schedule.Create(ws, "s", c.Id, set.Id, ScheduleMode.Daily, ["09:00"], 6, "09:00", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "14:00", PostOrder.Shuffle,
                "09:00", "21:00", 3, 0, 0, null, 420, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db => db.Collections.Where(x => x.Id == c.Id).ExecuteDeleteAsync()));
        await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db => db.LinkSets.Where(x => x.Id == set.Id).ExecuteDeleteAsync()));
    }
}
