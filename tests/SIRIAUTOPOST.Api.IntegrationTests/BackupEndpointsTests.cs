using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.ValueObjects;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Backup and restore of the workflow: collections, link sets, schedules and the settings that go with them.
[Collection(ApiCollection.Name)]
public class BackupEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;
    private const string Alpha = "https://www.facebook.com/groups/alpha";
    private const string Beta = "https://www.facebook.com/groups/beta";
    private const string Gamma = "https://www.facebook.com/groups/gamma";
    private const string TelegramToken = "tg-secret-token";
    private const string ChatId = "-100123456";

    private static string Serialize(BackupDto b) => JsonSerializer.Serialize(b with { CreatedAt = default }, Json);

    private sealed record Rich(
        Shop Shop, CollectionDto Promo, CollectionDto Plain, LinkSetDto Main, LinkSetDto Second, ScheduleCreatedDto Morning, ScheduleCreatedDto Once,
        ScheduleCreatedDto Paused, AccountDto Instagram, Guid ImageId);

    /// <summary>
    /// A workspace with everything in it: two collections (one asks for approval and has a draft, a waiting and an approved post,
    /// one has media), two link sets (a set with codes, a cap, a group that is off, another account), three schedules (one
    /// paused), advanced anti-ban numbers, notification rules, an auto-reply rule, and a Telegram token that must never leave.
    /// </summary>
    private async Task<Rich> RichAsync(string plan = "pro")
    {
        var shop = await factory.ShopAsync(plan, links: 0, posts: 0);
        var (owner, ws) = (shop.Owner, shop.Ws);
        var image = await owner.UploadImageAsync(ws);

        // The collection the shop made asks for approval.
        (await owner.PutAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}",
            new
            {
                name = "โปรโมชัน", description = "ของลดราคา", icon = "ph-tag",
                settings = WorkflowTestSupport.Settings(requireApproval: true, hashtags: "#sale", footer: "ติดต่อ 081", footerPos: "top", pageTags: "เพจ | https://www.facebook.com/shop", shuffle: false, watermark: true, watermarkPos: "tr"),
            }, Json)).EnsureSuccessStatusCode();
        await owner.AddPostAsync(ws, shop.Collection.Id, "ร่าง {{code}}", [image.Id]); // draft
        var waiting = await owner.AddPostAsync(ws, shop.Collection.Id, "รออนุมัติ");
        var approved = await owner.AddPostAsync(ws, shop.Collection.Id, "อนุมัติแล้ว {a|b}");
        async Task Approval(Guid post, string action) =>
            (await owner.PostAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}/posts/{post}/approval", new { action }, Json)).EnsureSuccessStatusCode();
        await Approval(waiting.Id, "request");
        await Approval(approved.Id, "request");
        await Approval(approved.Id, "approve");

        var plain = await owner.CreateCollectionAsync(ws, "อีกชุด");
        await owner.AddPostAsync(ws, plain.Id, "โพสต์ธรรมดา 1", [image.Id]);
        await owner.AddPostAsync(ws, plain.Id, "โพสต์ธรรมดา 2");

        var instagram = await owner.AccountOfAsync(ws, Platform.Ig);
        var main = shop.Set; // the set the shop made becomes the main one (renamed below)
        var alpha = await owner.AddLinkAsync(ws, main.Id, Alpha, "A1", "อัลฟา", dailyMax: 2);
        var beta = await owner.AddLinkAsync(ws, main.Id, Beta, "B1", "เบต้า");
        await owner.AddLinkAsync(ws, main.Id, Gamma, null, "แกมมา");
        (await owner.PutAsJsonAsync($"{shop.Api}/link-sets/{main.Id}/links/{beta.Id}", new { name = "เบต้า", url = Beta, code = "B1", dailyMax = 0, enabled = false }, Json)).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"{shop.Api}/link-sets/{main.Id}", new { name = "ชุดหลัก", postAsAccountId = shop.Pair.AccountId, accountIds = new[] { instagram.Id } }, Json)).EnsureSuccessStatusCode();
        var second = await owner.CreateLinkSetAsync(ws, "ชุดรอง");
        await owner.AddLinkAsync(ws, second.Id, "https://www.facebook.com/groups/delta", "D1", "เดลต้า");
        shop.Set = (await owner.LinkSetsAsync(ws)).Single(s => s.Id == main.Id);

        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var morning = await CreateAsync(shop, plain.Id, main.Id, new ScheduleSpec(
            Times: [slot, "20:00"], Order: "shuffle", Offset: 420, Name: "ตารางเช้า", BumpHours: 6, AutoDeleteDays: 3,
            Overrides: new() { [alpha.Id.ToString("N")] = ["12:00"], [$"account:{instagram.Id:N}"] = ["21:30"] }));
        var once = await CreateAsync(shop, plain.Id, second.Id, new ScheduleSpec(Mode: "once", StartDate: ToLocalDay(shop.Now.AddDays(1), 420), OnceTime: "14:00", Offset: 420, Name: "ครั้งเดียว"));
        var paused = await CreateAsync(shop, plain.Id, main.Id, new ScheduleSpec(Mode: "weekdays", Times: ["08:00"], Offset: 420, Name: "พักไว้"));
        await shop.SetActiveAsync(paused.Schedule.Id, false);

        await shop.SetAdvancedAsync(a => a with { MinGap = 5, DailyAll = 100, BlockMin = 10, BlockMax = 20, FailStreak = 6, RecentAvoid = 4, Cooldown = 1, Focus = false, AutoOffFails = 5, StopFailPct = 40 });
        await factory.WithDbAsync(async db =>
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
            var rules = new NotificationSettings
            {
                Telegram = new TelegramChannel { On = true, Token = TelegramToken, ChatId = ChatId },
                Line = new LineChannel { On = true, Token = "line-secret", To = "U123" },
                Channel = NotifyChannel.Both,
                Events = new NotifyEventSet { Success = true, Fail = true, Shot = false, Round = true, StartStop = false, Block = true, Offline = false, Quota = true },
                CommandsOn = true,
                CommandsUsers = "@boss",
            };
            rules.Sets[main.Id] = new SetNotifyRule
            {
                Channel = NotifyChannel.Line, Events = new NotifyEventSet { Success = false },
                Groups = { [alpha.Id] = new GroupNotifyRule { Channel = NotifyChannel.Off, Events = new NotifyEventSet { Quota = true } } },
            };
            workspace.UpdateNotifications(rules);
            workspace.UpdateAutoReply(new AutoReplySettings
            {
                On = true,
                Rules = [new AutoReplyRule { Keywords = "ราคา, เท่าไหร่", Reply = "ทักแชทได้เลย", Inbox = "ราคาพิเศษ", Scope = plain.Id.ToString(), On = true }],
            });
            await db.SaveChangesAsync();
        });
        return new Rich(shop, shop.Collection, plain, main, second, morning, once, paused, instagram, image.Id);
    }

    private static async Task<ScheduleCreatedDto> CreateAsync(Shop shop, Guid collection, Guid set, ScheduleSpec spec) =>
        await (await shop.Owner.PostAsJsonAsync($"{shop.Api}/schedules", shop.Body(spec, collection, set), Json)).ReadAsync<ScheduleCreatedDto>();

    private static async Task<BackupDto> BackupAsync(Shop shop, HttpClient? as_ = null) =>
        await (await (as_ ?? shop.Owner).GetAsync($"{shop.Api}/backup")).ReadAsync<BackupDto>();

    private static Task<HttpResponseMessage> RestoreAsync(Shop shop, object body, HttpClient? as_ = null) =>
        (as_ ?? shop.Owner).PostAsJsonAsync($"{shop.Api}/restore", body, Json);

    /// <summary>What is in the workspace, by id: restoring a bad file must leave it as it was.</summary>
    private static async Task<string> SnapshotAsync(Shop shop)
    {
        var collections = await shop.Owner.CollectionsAsync(shop.Ws);
        var sets = await shop.Owner.LinkSetsAsync(shop.Ws);
        var schedules = await shop.SchedulesAsync();
        var posts = (await shop.PostsAsync()).Where(p => p.ScheduleId is not null).Select(p => p.Id).Order();
        return JsonSerializer.Serialize(new { collections, sets, schedules, posts }, Json);
    }

    // ---- what the file holds ----

    [Fact]
    public async Task A_backup_holds_the_whole_workflow_by_name_and_no_secrets()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;

        var raw = await shop.Owner.GetStringAsync($"{shop.Api}/backup");
        var backup = JsonSerializer.Deserialize<BackupDto>(raw, Json)!;

        Assert.DoesNotContain(TelegramToken, raw);
        Assert.DoesNotContain("line-secret", raw);
        Assert.DoesNotContain(ChatId, raw);
        Assert.DoesNotContain("U123", raw);
        Assert.Equal(2, backup.Version);
        Assert.InRange(backup.CreatedAt, shop.Now.AddMinutes(-1), shop.Now.AddMinutes(1));

        Assert.Equal(["โปรโมชัน", "อีกชุด"], backup.Collections.Select(c => c.Name));
        var promo = backup.Collections[0];
        Assert.Equal(("ของลดราคา", "ph-tag"), (promo.Description, promo.Icon));
        Assert.Equal(new CollectionSettingsDto("#sale", "เพจ | https://www.facebook.com/shop", "ติดต่อ 081", FooterPosition.Top, false, true, WatermarkPosition.Tr, true), promo.Settings);
        Assert.Equal(["ร่าง {{code}}", "รออนุมัติ", "อนุมัติแล้ว {a|b}"], promo.Posts.Select(p => p.Text));
        Assert.Equal([PostApproval.Draft, PostApproval.Pending, PostApproval.Approved], promo.Posts.Select(p => p.Approval));
        Assert.Equal([rich.ImageId], promo.Posts[0].MediaIds);

        Assert.Equal(["ชุดหลัก", "ชุดรอง"], backup.LinkSets.Select(s => s.Name));
        var main = backup.LinkSets[0];
        Assert.Equal(shop.Pair.AccountId, main.PostAsAccountId);
        Assert.Equal([rich.Instagram.Id], main.AccountIds);
        Assert.Equal([("อัลฟา", Alpha, "A1", 2, true), ("เบต้า", Beta, "B1", 0, false), ("แกมมา", Gamma, "", 0, true)],
            main.Links.Select(l => (l.Name, l.Url, l.Code, l.DailyMax, l.Enabled)));

        Assert.Equal(["ตารางเช้า", "ครั้งเดียว", "พักไว้"], backup.Schedules.Select(s => s.Name));
        var morning = backup.Schedules[0];
        Assert.Equal(("อีกชุด", "ชุดหลัก", ScheduleMode.Daily, PostOrder.Shuffle, 6, 3, 420, true), (morning.Collection, morning.LinkSet, morning.Mode, morning.Order, morning.BumpHours, morning.AutoDeleteDays, morning.UtcOffsetMinutes, morning.Active));
        Assert.Equal(2, morning.Times.Count);
        // Times of one target are keyed by its address, not its id: the id is new after a restore.
        Assert.Equal(["12:00"], morning.Overrides[Alpha]);
        Assert.Equal(["21:30"], morning.Overrides[$"account:{rich.Instagram.Id:N}"]);
        Assert.Equal(2, morning.Overrides.Count);
        Assert.False(backup.Schedules[2].Active);
        Assert.Equal((ScheduleMode.Once, "14:00"), (backup.Schedules[1].Mode, backup.Schedules[1].OnceTime));

        Assert.Equal(new AdvancedAntiBanDto(5, 100, 10, 20, 6, 4, 1, false, 5, 40), backup.AntiBanAdvanced);
        var rules = backup.NotificationRules!;
        Assert.Equal(NotifyChannel.Both, rules.Channel);
        Assert.True(rules.Events.Quota);
        var setRule = Assert.Single(rules.Sets);
        Assert.Equal(("ชุดหลัก", NotifyChannel.Line), (setRule.LinkSet, setRule.Channel));
        var groupRule = Assert.Single(setRule.Groups);
        Assert.Equal((Alpha, NotifyChannel.Off), (groupRule.Url, groupRule.Channel));
        var reply = backup.AutoReply!;
        Assert.True(reply.On);
        Assert.Equal(("ราคา, เท่าไหร่", "อีกชุด"), (reply.Rules.Single().Keywords, reply.Rules.Single().Collection));
    }

    [Fact]
    public async Task A_backup_of_an_empty_workspace_is_an_empty_file()
    {
        var (client, _, ws) = await factory.SignUpAsync();

        var backup = await (await client.GetAsync($"/api/workspaces/{ws}/backup")).ReadAsync<BackupDto>();

        Assert.Equal(2, backup.Version);
        Assert.Empty(backup.Collections);
        Assert.Empty(backup.LinkSets);
        Assert.Empty(backup.Schedules);
        Assert.Empty(backup.AutoReply!.Rules);
    }

    [Fact]
    public async Task Names_that_repeat_are_told_apart_in_the_file()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 1);
        var second = await shop.Owner.CreateCollectionAsync(shop.Ws, shop.Collection.Name); // same name
        await shop.Owner.AddPostAsync(shop.Ws, second.Id, "ชุดที่สอง");
        var secondSet = await shop.Owner.CreateLinkSetAsync(shop.Ws, shop.Set.Name, shop.Pair.AccountId);
        await shop.Owner.AddLinkAsync(shop.Ws, secondSet.Id, Alpha, "Z9", "ซีตา");
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        await CreateAsync(shop, second.Id, secondSet.Id, new ScheduleSpec(Times: [slot], Offset: 420, Name: "ของชุดที่สอง"));

        var backup = await BackupAsync(shop);

        Assert.Equal(["โปรโมชัน", "โปรโมชัน (2)"], backup.Collections.Select(c => c.Name));
        Assert.Equal(["กลุ่มขายของ", "กลุ่มขายของ (2)"], backup.LinkSets.Select(s => s.Name));
        Assert.Equal(("โปรโมชัน (2)", "กลุ่มขายของ (2)"), (backup.Schedules.Single().Collection, backup.Schedules.Single().LinkSet));

        // And the schedule finds its own again after a restore.
        var restored = await (await RestoreAsync(shop, backup)).ReadAsync<RestoreResultDto>();
        Assert.Equal(1, restored.Schedules);
        var collections = await shop.Owner.CollectionsAsync(shop.Ws);
        var schedule = (await shop.SchedulesAsync()).Single();
        Assert.Equal("ชุดที่สอง", collections.Single(c => c.Id == schedule.CollectionId).Posts.Single().Text);
        Assert.Equal("ซีตา", (await shop.Owner.LinkSetsAsync(shop.Ws)).Single(s => s.Id == schedule.LinkSetId).Links.Single().Name);
    }

    // ---- restoring ----

    [Fact]
    public async Task Restoring_the_same_file_gives_back_the_same_workflow_with_new_ids_and_queues_the_active_schedules_again()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        // One post of the morning schedule goes out first: the history must survive.
        shop.WaitUntil((await shop.PostsAsync(rich.Morning.Schedule.Id)).Where(p => p.LinkId is not null).Min(p => p.ScheduledAt));
        var sent = (await shop.RunNextAsync())!;
        Assert.Equal(PostStatus.Success, sent.Status);
        var before = await BackupAsync(shop);
        var oldSchedules = (await shop.SchedulesAsync()).Select(s => s.Id).ToList();
        var oldCollections = (await shop.Owner.CollectionsAsync(shop.Ws)).Select(c => c.Id).ToList();
        Assert.Contains(await shop.PostsAsync(), p => p.ScheduleId == rich.Morning.Schedule.Id && p.Status == PostStatus.Queued);

        var res = await RestoreAsync(shop, before);

        Assert.Equal(new RestoreResultDto(2, 2, 3), await res.ReadAsync<RestoreResultDto>());
        var after = await BackupAsync(shop);
        Assert.Equal(Serialize(before), Serialize(after));
        var schedules = await shop.SchedulesAsync();
        Assert.Equal(["ตารางเช้า", "ครั้งเดียว", "พักไว้"], schedules.Select(s => s.Name));
        Assert.Empty(schedules.Select(s => s.Id).Intersect(oldSchedules)); // everything is new
        Assert.Empty((await shop.Owner.CollectionsAsync(shop.Ws)).Select(c => c.Id).Intersect(oldCollections));
        Assert.Equal([true, true, false], schedules.Select(s => s.Active));

        // The times of one target follow the new link.
        var set = (await shop.Owner.LinkSetsAsync(shop.Ws)).Single(s => s.Name == "ชุดหลัก");
        var alpha = set.Links.Single(l => l.Url == Alpha);
        var morning = schedules[0];
        // Both sides sorted: the keys are random ids, so neither has a fixed order.
        Assert.Equal(new[] { alpha.Id.ToString("N"), $"account:{rich.Instagram.Id:N}" }.Order(), morning.Overrides.Keys.Order());
        Assert.Equal(["12:00"], morning.Overrides[alpha.Id.ToString("N")]);
        Assert.Equal((set.Id, 3), (morning.LinkSetId, morning.TargetCount)); // two usable links and the other account

        // The active schedules have posts again; the paused one has none; the old schedule's queued posts are gone, what went out stays.
        var posts = await shop.PostsAsync();
        Assert.Contains(posts, p => p.ScheduleId == morning.Id);
        Assert.Contains(posts, p => p.ScheduleId == schedules[1].Id);
        Assert.DoesNotContain(posts, p => p.ScheduleId == schedules[2].Id);
        Assert.DoesNotContain(posts, p => p.ScheduleId == rich.Morning.Schedule.Id && p.Status == PostStatus.Queued);
        Assert.Contains(posts, p => p.Id == sent.Id && p.Status == PostStatus.Success);
        Assert.DoesNotContain(posts, p => oldSchedules.Contains(p.ScheduleId ?? Guid.Empty) && p.Status != PostStatus.Success);
        Assert.All(posts.Where(p => p.ScheduleId == morning.Id), p => Assert.Equal(PostStatus.Queued, p.Status));
    }

    [Fact]
    public async Task A_restore_keeps_the_telegram_and_line_settings_and_the_rest_of_the_rules_come_back()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        var file = await BackupAsync(shop);

        (await RestoreAsync(shop, file)).EnsureSuccessStatusCode();

        var set = (await shop.Owner.LinkSetsAsync(shop.Ws)).Single(s => s.Name == "ชุดหลัก");
        var collection = (await shop.Owner.CollectionsAsync(shop.Ws)).Single(c => c.Name == "อีกชุด");
        await factory.WithDbAsync(async db =>
        {
            var ws = await db.Workspaces.SingleAsync(w => w.Id == shop.Ws);
            Assert.Equal((TelegramToken, ChatId, "line-secret", "U123"), (ws.Notifications.Telegram.Token, ws.Notifications.Telegram.ChatId, ws.Notifications.Line.Token, ws.Notifications.Line.To));
            Assert.Equal((true, true, "@boss"), (ws.Notifications.Telegram.On, ws.Notifications.CommandsOn, ws.Notifications.CommandsUsers));
            Assert.Equal(NotifyChannel.Both, ws.Notifications.Channel);
            var rule = Assert.Single(ws.Notifications.Sets);
            Assert.Equal(set.Id, rule.Key); // the rule follows the new set and the new link
            Assert.Equal(NotifyChannel.Line, rule.Value.Channel);
            Assert.Equal(set.Links.Single(l => l.Url == Alpha).Id, Assert.Single(rule.Value.Groups).Key);
            var reply = Assert.Single(ws.AutoReply.Rules);
            Assert.Equal(collection.Id.ToString(), reply.Scope); // and the auto-reply rule follows the new collection
            Assert.Equal(("ราคา, เท่าไหร่", true), (reply.Keywords, reply.On));
            Assert.Equal(5, ws.AntiBan.Advanced.MinGap);
        });
    }

    [Fact]
    public async Task A_file_that_does_not_pass_changes_nothing()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        var good = await BackupAsync(shop);
        var snapshot = await SnapshotAsync(shop);
        var advancedBefore = (await shop.AntiBanAsync()).Advanced;

        async Task Refused(BackupDto bad, string? mention = null)
        {
            var res = await RestoreAsync(shop, bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            if (mention is not null) Assert.Contains(mention, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
            Assert.Equal(snapshot, await SnapshotAsync(shop));
            Assert.Equal(advancedBefore, (await shop.AntiBanAsync()).Advanced);
        }

        var otherAdvanced = new AdvancedAntiBanDto(1, 1, 1, 1, 1, 1, 1, true, 1, 1); // would show if anything were applied
        await Refused(good with { Version = 1, AntiBanAdvanced = otherAdvanced }, "เวอร์ชัน");
        await Refused(good with { AntiBanAdvanced = otherAdvanced, Schedules = [good.Schedules[0] with { Collection = "ไม่มีชุดนี้" }] }, "ไม่มีชุดนี้");
        await Refused(good with { Schedules = [good.Schedules[0] with { LinkSet = "ไม่มีชุดลิงก์นี้" }] }, "ไม่มีชุดลิงก์นี้");
        await Refused(good with { Collections = [good.Collections[0], good.Collections[1] with { Name = good.Collections[0].Name }] }, "ซ้ำ");
        await Refused(good with { LinkSets = [good.LinkSets[0], good.LinkSets[1] with { Name = good.LinkSets[0].Name }] }, "ซ้ำ");
        await Refused(good with { Collections = [good.Collections[0] with { Posts = [good.Collections[0].Posts[0] with { Text = "  " }] }, good.Collections[1]] }, "ข้อความ");
        await Refused(good with { LinkSets = [good.LinkSets[0] with { Links = [good.LinkSets[0].Links[0] with { DailyMax = 99 }] }, good.LinkSets[1]] }, "เพดาน");
        await Refused(good with { Schedules = [good.Schedules[0] with { Times = ["25:00"] }, good.Schedules[1], good.Schedules[2]] }, "25:00");
        await Refused(good with { Schedules = [good.Schedules[0] with { StartDate = "03/10/2026" }] }, "วันที่เริ่ม");
        await Refused(good with { NotificationRules = good.NotificationRules! with { Sets = [good.NotificationRules.Sets[0] with { LinkSet = "ชุดที่หายไป" }] } }, "ชุดที่หายไป");
        await Refused(good with { AutoReply = new BackupAutoReplyDto(true, [new BackupAutoReplyRuleDto("คำ", "ตอบ", "", "ไม่มีชุดโพสต์นี้", true)]) }, "ไม่มีชุดโพสต์นี้");
        await Refused(good with { Collections = Enumerable.Range(0, 101).Select(i => good.Collections[1] with { Name = $"ชุด {i}" }).ToList() }, "100");
        await Refused(good with { Schedules = Enumerable.Range(0, 51).Select(i => good.Schedules[1] with { Name = $"ตาราง {i}" }).ToList() }, "50");

        // A file with its lists missing is not a backup, and must not read as "restore nothing".
        var missing = await shop.Owner.PostAsync($"{shop.Api}/restore", JsonContent.Create(new { version = 2, createdAt = shop.Now }, options: Json));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var nulls = await shop.Owner.PostAsync($"{shop.Api}/restore", new StringContent("{\"version\":2,\"collections\":null,\"linkSets\":[],\"schedules\":[]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nulls.StatusCode);
        Assert.Equal(snapshot, await SnapshotAsync(shop));
    }

    [Fact]
    public async Task A_start_date_beyond_a_year_ahead_is_refused_and_an_old_one_is_moved_up_to_yesterday()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        var good = await BackupAsync(shop);
        var snapshot = await SnapshotAsync(shop);

        var far = await RestoreAsync(shop, good with { Schedules = [good.Schedules[0] with { StartDate = "9999-12-31" }] });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, far.StatusCode);
        var title = (await far.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title!;
        Assert.Contains(good.Schedules[0].Name, title);
        Assert.Contains(ToLocalDay(shop.Now.AddDays(366), 420), title); // it says how far ahead is allowed
        Assert.Equal(snapshot, await SnapshotAsync(shop));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RestoreAsync(shop, good with { Schedules = [good.Schedules[0] with { StartDate = ToLocalDay(shop.Now.AddDays(367), 420) }] })).StatusCode);

        // A backup is restored later than it was made: dates in the past are what a daily schedule normally has.
        var old = await RestoreAsync(shop, good with { Schedules = good.Schedules.Select(x => x with { StartDate = "2020-01-01" }).ToList() });

        old.EnsureSuccessStatusCode();
        var schedules = await shop.SchedulesAsync();
        Assert.All(schedules, x => Assert.Equal(ToLocalDay(shop.Now.AddDays(-1), 420), x.StartDate));
        Assert.NotEmpty(await shop.PostsAsync(schedules[0].Id)); // the daily schedule still queues its posts
        var claim = await shop.Device.PostAsync("/api/device/jobs/claim", null); // and the engine goes on
        Assert.True(claim.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, claim.StatusCode.ToString());
    }

    [Fact]
    public async Task A_restore_whose_posts_do_not_fit_in_the_workspaces_queue_is_refused_whole_and_one_that_fits_goes_through()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], Offset: 420, Name: "ตารางเดียว"));
        var good = await BackupAsync(shop);
        var own = (await shop.PostsAsync(created.Schedule.Id)).Count; // a restore deletes these and makes about as many again
        var demo = await factory.QueuedAsync(shop.Ws, shop.Now) - own;
        await factory.FillQueueAsync(shop.Ws, shop.Pair.AccountId, 9_990 - demo); // everything but the schedule: 9,990
        var snapshot = await SnapshotAsync(shop);

        var refused = await RestoreAsync(shop, good);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var title = (await refused.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title!;
        Assert.Contains("9,990", title); // what the queue holds once the old schedule's posts are gone
        Assert.Contains("10,000", title);
        Assert.Equal(snapshot, await SnapshotAsync(shop)); // the old schedule and its posts are as they were

        await factory.EmptyFillerAsync(shop.Ws);
        await factory.FillQueueAsync(shop.Ws, shop.Pair.AccountId, 9_900 - demo);
        (await RestoreAsync(shop, good)).EnsureSuccessStatusCode();
        var schedule = Assert.Single(await shop.SchedulesAsync());
        Assert.NotEqual(created.Schedule.Id, schedule.Id);
        Assert.NotEmpty(await shop.PostsAsync(schedule.Id));
    }

    [Fact]
    public async Task An_empty_file_clears_the_workflow_and_leaves_the_settings_alone()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        var advanced = (await shop.AntiBanAsync()).Advanced;

        var res = await RestoreAsync(shop, new { version = 2, createdAt = shop.Now, collections = Array.Empty<object>(), linkSets = Array.Empty<object>(), schedules = Array.Empty<object>() });

        Assert.Equal(new RestoreResultDto(0, 0, 0), await res.ReadAsync<RestoreResultDto>());
        Assert.Empty(await shop.Owner.CollectionsAsync(shop.Ws));
        Assert.Empty(await shop.Owner.LinkSetsAsync(shop.Ws));
        Assert.Empty(await shop.SchedulesAsync());
        Assert.DoesNotContain(await shop.PostsAsync(), p => p.ScheduleId is not null && p.Status == PostStatus.Queued);
        Assert.Equal(advanced, (await shop.AntiBanAsync()).Advanced);
        await factory.WithDbAsync(async db =>
        {
            var ws = await db.Workspaces.SingleAsync(w => w.Id == shop.Ws);
            Assert.Equal(TelegramToken, ws.Notifications.Telegram.Token);
            Assert.Single(ws.AutoReply.Rules);
        });
    }

    [Fact]
    public async Task A_restore_replaces_what_the_workspace_had()
    {
        var rich = await RichAsync();
        using var shop = rich.Shop;
        var file = await BackupAsync(shop);
        var extra = await shop.Owner.CreateCollectionAsync(shop.Ws, "ชุดที่ไม่อยู่ในไฟล์");
        await shop.Owner.CreateLinkSetAsync(shop.Ws, "ชุดลิงก์ที่ไม่อยู่ในไฟล์");

        (await RestoreAsync(shop, file with { Collections = [file.Collections[1]], Schedules = [], NotificationRules = null, AutoReply = null })).EnsureSuccessStatusCode();

        Assert.Equal(["อีกชุด"], (await shop.Owner.CollectionsAsync(shop.Ws)).Select(c => c.Name));
        Assert.Equal(["ชุดหลัก", "ชุดรอง"], (await shop.Owner.LinkSetsAsync(shop.Ws)).Select(s => s.Name));
        Assert.Empty(await shop.SchedulesAsync());
        Assert.DoesNotContain(extra.Id, (await shop.Owner.CollectionsAsync(shop.Ws)).Select(c => c.Id));
    }

    [Fact]
    public async Task Media_the_library_does_not_have_is_dropped()
    {
        using var shop = await factory.ShopAsync(links: 0, posts: 0);
        var image = await shop.Owner.UploadImageAsync(shop.Ws);
        var gone = Guid.NewGuid();

        var res = await RestoreAsync(shop, new
        {
            version = 2, createdAt = shop.Now, linkSets = Array.Empty<object>(), schedules = Array.Empty<object>(),
            collections = new[]
            {
                new
                {
                    name = "มีรูป", description = "", icon = "ph-folder", settings = WorkflowTestSupport.Settings(),
                    posts = new[] { new { text = "โพสต์", mediaIds = new[] { image.Id, gone }, approval = "approved" } },
                },
            },
        });

        Assert.Equal(1, (await res.ReadAsync<RestoreResultDto>()).Collections);
        Assert.Equal([image.Id], (await shop.Owner.CollectionsAsync(shop.Ws)).Single().Posts.Single().MediaIds);
    }

    [Fact]
    public async Task A_workspace_without_a_browser_restores_its_schedules_but_queues_nothing()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var file = new
        {
            version = 2, createdAt = DateTimeOffset.UtcNow,
            collections = new[] { new { name = "ชุด", description = "", icon = "ph-folder", settings = WorkflowTestSupport.Settings(), posts = new[] { new { text = "โพสต์", mediaIds = Array.Empty<Guid>(), approval = "approved" } } } },
            linkSets = new[] { new { name = "กลุ่ม", postAsAccountId = (Guid?)null, accountIds = Array.Empty<Guid>(), links = new[] { new { name = "อัลฟา", url = Alpha, code = "A1", dailyMax = 0, enabled = true } } } },
            schedules = new[]
            {
                new
                {
                    name = "ตาราง", collection = "ชุด", linkSet = "กลุ่ม", mode = "daily", times = new[] { "10:00" }, everyHours = 6, firstTime = "09:00",
                    startDate = "2026-01-01", onceTime = "14:00", order = "rotate", dripFrom = "09:00", dripTo = "21:00", dripCount = 3, bumpHours = 0,
                    autoDeleteDays = 0, overrides = new Dictionary<string, string[]>(), active = true, utcOffsetMinutes = 420,
                },
            },
        };

        var res = await client.PostAsJsonAsync($"/api/workspaces/{ws}/restore", file, Json);

        Assert.Equal(new RestoreResultDto(1, 1, 1), await res.ReadAsync<RestoreResultDto>());
        var schedule = Assert.Single((await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/workspaces/{ws}/schedules", Json))!);
        Assert.Equal((true, 1, null), (schedule.Active, schedule.TargetCount, schedule.NextRunAt));
    }

    // ---- who may, and what the plan allows ----

    [Fact]
    public async Task Only_admins_can_back_up_and_restore()
    {
        using var shop = await factory.ShopAsync("agency", links: 1);
        async Task<HttpClient> Join(string role)
        {
            var (client, auth, _) = await factory.SignUpAsync();
            (await shop.Owner.PostAsJsonAsync($"{shop.Api}/members", new { email = auth.User.Email, role }, Json)).EnsureSuccessStatusCode();
            return client;
        }
        var viewer = await Join("viewer");
        var editor = await Join("editor");
        var admin = await Join("admin");
        var file = await BackupAsync(shop);

        foreach (var client in new[] { viewer, editor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{shop.Api}/backup")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await RestoreAsync(shop, file, client)).StatusCode);
        }
        Assert.Equal(file.Collections.Count, (await BackupAsync(shop, admin)).Collections.Count);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(shop, file, admin)).StatusCode);
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{shop.Api}/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RestoreAsync(shop, file, stranger)).StatusCode);
    }

    [Fact]
    public async Task A_plan_without_the_pro_features_restores_the_workflow_but_not_the_advanced_numbers_or_the_rules()
    {
        var rich = await RichAsync();
        var file = await BackupAsync(rich.Shop);
        rich.Shop.Dispose();

        // The same file into a Free workspace: its advanced numbers, notification rules and auto-reply rules are left as they were.
        using var free = await factory.ShopAsync("free", links: 0, posts: 0);
        var advanced = (await free.AntiBanAsync()).Advanced;
        var res = await RestoreAsync(free, file with { Schedules = [] });

        Assert.Equal(new RestoreResultDto(2, 2, 0), await res.ReadAsync<RestoreResultDto>());
        Assert.Equal(advanced, (await free.AntiBanAsync()).Advanced);
        await factory.WithDbAsync(async db =>
        {
            var ws = await db.Workspaces.SingleAsync(w => w.Id == free.Ws);
            Assert.Empty(ws.Notifications.Sets);
            Assert.Empty(ws.AutoReply.Rules);
            Assert.False(ws.AutoReply.On);
        });
        Assert.Equal(["โปรโมชัน", "อีกชุด"], (await free.Owner.CollectionsAsync(free.Ws)).Select(c => c.Name));
    }
}
