using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Schedules;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Tests;

public class WorkflowDtoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();

    private static NotificationSettings Stored()
    {
        var n = new NotificationSettings
        {
            Telegram = new TelegramChannel { On = true, Token = "tg-secret", ChatId = "-100" },
            Line = new LineChannel { On = true, Token = "line-secret", To = "U1" },
            Channel = NotifyChannel.Both,
        };
        n.Sets[Guid.NewGuid()] = new SetNotifyRule { Channel = NotifyChannel.Line, Events = new NotifyEventSet { Success = true } };
        return n;
    }

    [Fact]
    public void Notification_settings_never_carry_a_token_only_whether_one_is_stored()
    {
        var dto = NotificationSettingsDto.From(Stored());

        Assert.Null(dto.Telegram.Token);
        Assert.Null(dto.Line.Token);
        Assert.True(dto.Telegram.HasToken);
        Assert.True(dto.Line.HasToken);
        Assert.Equal(("-100", "U1"), (dto.Telegram.ChatId, dto.Line.To));
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(dto));

        var empty = NotificationSettingsDto.From(new NotificationSettings());
        Assert.False(empty.Telegram.HasToken);
        Assert.False(empty.Line.HasToken);
    }

    [Fact]
    public void A_null_token_keeps_the_stored_one_an_empty_one_clears_it_and_a_value_replaces_it()
    {
        var current = Stored();
        var shown = NotificationSettingsDto.From(current);

        var kept = shown.ToSettings(current);
        Assert.Equal(("tg-secret", "line-secret"), (kept.Telegram.Token, kept.Line.Token));

        var changed = (shown with { Telegram = shown.Telegram with { Token = "new-tg" }, Line = shown.Line with { Token = "" } }).ToSettings(current);
        Assert.Equal(("new-tg", ""), (changed.Telegram.Token, changed.Line.Token));
        Assert.False(changed.Line.IsReady);
    }

    [Fact]
    public void Notification_rules_survive_the_trip_through_the_dto()
    {
        var current = Stored();
        var set = current.Sets.Keys.Single();
        var link = Guid.NewGuid();
        current.Sets[set].Groups[link] = new GroupNotifyRule { Channel = NotifyChannel.Off, Events = new NotifyEventSet { Fail = false } };
        current.CommandsOn = true;
        current.CommandsUsers = "@boss";

        var dto = NotificationSettingsDto.From(current);
        var rule = Assert.Single(dto.Sets);
        Assert.Equal((set, NotifyChannel.Line), (rule.LinkSetId, rule.Channel));
        Assert.Equal(NotifyChannel.Off, rule.Groups[link.ToString()].Channel);

        var back = dto.ToSettings(current);
        Assert.Equal(NotifyChannel.Both, back.Channel);
        Assert.True(back.Sets[set].Events!.Success);
        Assert.Equal(NotifyChannel.Off, back.Sets[set].Groups[link].Channel);
        Assert.False(back.Sets[set].Groups[link].Events!.Fail);
        Assert.Equal(("@boss", true), (back.CommandsUsers, back.CommandsOn));
    }

    [Fact]
    public void A_group_rule_with_a_bad_key_is_refused()
    {
        var dto = NotificationSettingsDto.From(new NotificationSettings()) with
        {
            Sets = [new SetNotifyRuleDto(Guid.NewGuid(), NotifyChannel.Default, null, new Dictionary<string, GroupNotifyRuleDto> { ["nope"] = new(NotifyChannel.Off, null) })],
        };
        Assert.Throws<DomainException>(() => dto.ToSettings(new NotificationSettings()));
    }

    [Fact]
    public void Auto_reply_rules_get_an_id_when_they_come_without_one()
    {
        var kept = Guid.NewGuid();
        var settings = new AutoReplyDto(true,
        [
            new AutoReplyRuleDto(Guid.Empty, "ราคา", "ทักแชท", "", "all", true),
            new AutoReplyRuleDto(kept, "สนใจ", "", "ส่งราคา", "abc", false),
        ]).ToSettings();

        Assert.True(settings.On);
        Assert.NotEqual(Guid.Empty, settings.Rules[0].Id);
        Assert.Equal(kept, settings.Rules[1].Id);
        Assert.Equal(("abc", false), (settings.Rules[1].Scope, settings.Rules[1].On));
        Assert.Equal(AutoReplyDto.From(settings).Rules.Select(r => r.Id), settings.Rules.Select(r => r.Id));
    }

    [Fact]
    public void An_anti_ban_body_without_advanced_numbers_gets_the_defaults()
    {
        var dto = AntiBanDto.From(new AntiBanSettings { Min = 5, Max = 9 }) with { Advanced = null! };

        var settings = dto.ToSettings();

        Assert.Equal((5, 9, 2), (settings.Min, settings.Max, settings.Advanced.MinGap));
        Assert.Equal(AdvancedAntiBanDto.From(new AdvancedAntiBanSettings()), AntiBanDto.From(settings).Advanced);
    }

    [Fact]
    public void Links_that_repeat_an_earlier_address_of_their_set_are_flagged()
    {
        var set = LinkSet.Create(Ws, "ชุด", null, Now);
        SetLink Link(string? url) => SetLink.Create(Ws, set.Id, null, url, null, 0, Now, Platform.Fb);
        var links = new[] { Link("fb.com/groups/a"), Link("facebook.com/groups/b"), Link("m.facebook.com/groups/a"), Link(""), Link(""), Link("https://nope.example") };

        var dto = LinkSetDto.From(set, links, 2);

        Assert.Equal([false, false, true, false, false, false], dto.Links.Select(l => l.Duplicate)); // invalid rows are not "duplicates" of each other
        Assert.Equal([true, true, true, false, false, false], dto.Links.Select(l => l.Valid));
        Assert.Equal(2, dto.ScheduleCount);
    }

    [Fact]
    public void The_account_that_posts_a_set_is_the_one_it_names_never_another_extensions()
    {
        var oneDevice = Device.Pair(Ws, "PC 1", "Chrome", "2.3", "h1", Now);
        var twoDevice = Device.Pair(Ws, "PC 2", "Chrome", "2.3", "h2", Now);
        var one = SocialAccount.Connect(Ws, oneDevice, 0);
        var two = SocialAccount.Connect(Ws, twoDevice, 1);
        var noBrowser = SocialAccount.Create(Ws, Platform.Fb, "เพจตัวอย่าง", "", "เพจ");

        // A set that names none belongs to the ONLY connected Facebook account. With two extensions nobody is guessed
        // (2026-10-10: "the first one" sent a set's groups to whichever machine happened to be first): it waits for a choice.
        var unnamed = LinkSet.Create(Ws, "a", null, Now);
        Assert.Equal(one, LinkSetAccounts.PostingAccount(unnamed, [noBrowser, one]));
        Assert.Null(LinkSetAccounts.PostingAccount(unnamed, [noBrowser, one, two]));
        Assert.Null(LinkSetAccounts.PostingAccount(unnamed, [noBrowser]));
        Assert.Null(LinkOwnership.OwnerOf(unnamed, [noBrowser, one, two]));
        Assert.Equal(LinkSetAccounts.NoChoice, LinkSetAccounts.NoPosterReason([one, two]));
        Assert.Equal(CreateScheduleCommandHandler.NoDevice, LinkSetAccounts.NoPosterReason([noBrowser]));

        // A set that names an account is posted by that browser: connected, it is the answer even when another is first...
        Assert.Equal(two, LinkSetAccounts.PostingAccount(LinkSet.Create(Ws, "b", two.Id, Now), [one, two]));
        Assert.Equal(one, LinkSetAccounts.PostingAccount(LinkSet.Create(Ws, "c", one.Id, Now), [one, two]));

        // ...and while its browser is unbound it is STILL the answer (the caller says it has no browser, the posts wait):
        // the set never moves to the other extension, however many are connected.
        one.Disconnect();
        var pinned = LinkSet.Create(Ws, "d", one.Id, Now);
        var answer = LinkSetAccounts.PostingAccount(pinned, [one, two]);
        Assert.Equal(one, answer);
        Assert.False(answer!.IsConnected);
        Assert.Equal(noBrowser, LinkSetAccounts.PostingAccount(LinkSet.Create(Ws, "e", noBrowser.Id, Now), [noBrowser, two]));

        // A named account that no longer exists: the only connected one takes over, with several nobody is guessed.
        var gone = LinkSet.Create(Ws, "f", Guid.NewGuid(), Now);
        Assert.Equal(two, LinkSetAccounts.PostingAccount(gone, [one, two]));
        var three = SocialAccount.Connect(Ws, Device.Pair(Ws, "PC 3", "Chrome", "2.3", "h3", Now), 2);
        Assert.Null(LinkSetAccounts.PostingAccount(gone, [two, three]));
        Assert.Null(LinkSetAccounts.PostingAccount(gone, [noBrowser]));
    }

    [Fact]
    public void A_browser_that_pairs_again_takes_its_old_account_back_healthy()
    {
        var first = Device.Pair(Ws, "ส่วนขยาย 1", "Chrome", "2.3", "h1", Now);
        var account = SocialAccount.Connect(Ws, first, 0);
        account.Disconnect();
        Assert.False(account.IsConnected);
        Assert.False(account.CanPost);

        var again = Device.Pair(Ws, "ส่วนขยาย 1", "Chrome", "2.3", "h2", Now);
        account.Reconnect(again);

        Assert.Equal((again.Id, true, true, "Facebook · ส่วนขยาย 1"), (account.DeviceId!.Value, account.IsConnected, account.CanPost, account.Name));
    }

    [Fact]
    public void The_new_post_and_workspace_fields_are_filled_from_the_entities()
    {
        var device = Device.Pair(Ws, "PC", "Chrome", "2.2", "h", Now);
        var account = SocialAccount.Connect(Ws, device, 0);
        var link = Guid.NewGuid();
        var post = Post.FromSchedule(Ws, account, "g", "text", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), link, Post.LinkTargetKey(link), "s",
            "https://www.facebook.com/groups/g", "C1");

        var dto = PostDto.From(post);

        Assert.Equal((post.ScheduleId, post.CollectionPostId, link, "C1", "https://www.facebook.com/groups/g", false),
            (dto.ScheduleId, dto.CollectionPostId, dto.LinkId, dto.Code, dto.TargetUrl, dto.IsTest));

        var owner = User.Create("a@b.co", "A", UserRole.User, PlanKey.Pro, Now);
        var ws = Workspace.Create(owner.Id, "Shop", Now);
        var w = WorkspaceDto.From(ws, 0, 1, WorkspaceRole.Owner, new LimitsDto(null, null, null, null, null, null, null), owner);
        Assert.Equal((true, true, true, false), (w.AdvancedAntiBan, w.Notifications, w.AutoReply, w.ClientReports));
    }
}
