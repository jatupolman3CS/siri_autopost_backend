using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
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
        SetLink Link(string? url) => SetLink.Create(Ws, set.Id, null, url, null, 0, Now);
        var links = new[] { Link("fb.com/groups/a"), Link("facebook.com/groups/b"), Link("m.facebook.com/groups/a"), Link(""), Link(""), Link("https://nope.example") };

        var dto = LinkSetDto.From(set, links, 2);

        Assert.Equal([false, false, true, false, false, false], dto.Links.Select(l => l.Duplicate)); // invalid rows are not "duplicates" of each other
        Assert.Equal([true, true, true, false, false, false], dto.Links.Select(l => l.Valid));
        Assert.Equal(2, dto.ScheduleCount);
    }

    [Fact]
    public void The_account_that_posts_a_set_is_the_one_it_names_or_the_first_connected_facebook_account()
    {
        var device = Device.Pair(Ws, "PC", "Chrome", "2.2", "h", Now);
        var demoPage = SocialAccount.Create(Ws, Platform.Fb, "เพจตัวอย่าง", "", "เพจ");
        var ig = SocialAccount.Create(Ws, Platform.Ig, "@shop", "", "ฟีด");
        var connected = SocialAccount.Connect(Ws, device, 2);
        var all = new[] { demoPage, ig, connected };

        var unnamed = LinkSet.Create(Ws, "a", null, Now);
        Assert.Equal(connected, LinkSetAccounts.PostingAccount(unnamed, all));

        var named = LinkSet.Create(Ws, "b", demoPage.Id, Now);
        // A named account without a browser (unbound, or paired again as a new browser) gives way to the connected one...
        Assert.Equal(connected, LinkSetAccounts.PostingAccount(named, all));
        // ...and when nothing is connected it is still the one returned, so the caller can say it has no browser.
        Assert.Equal(demoPage, LinkSetAccounts.PostingAccount(named, [demoPage, ig]));
        var pinnedToConnected = LinkSet.Create(Ws, "d", connected.Id, Now);
        var other = SocialAccount.Connect(Ws, Device.Pair(Ws, "PC 2", "Chrome", "2.2", "h2", Now), 3);
        Assert.Equal(other, LinkSetAccounts.PostingAccount(LinkSet.Create(Ws, "e", other.Id, Now), [connected, other])); // a connected named account stays
        Assert.Equal(connected, LinkSetAccounts.PostingAccount(pinnedToConnected, [connected, other]));

        var gone = LinkSet.Create(Ws, "c", Guid.NewGuid(), Now);
        Assert.Equal(connected, LinkSetAccounts.PostingAccount(gone, all));
        Assert.Null(LinkSetAccounts.PostingAccount(unnamed, [demoPage, ig]));
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
        var w = WorkspaceDto.From(ws, 0, 1, WorkspaceRole.Owner, new LimitsDto(null, null, null, null), owner);
        Assert.Equal((true, true, true, false), (w.AdvancedAntiBan, w.Notifications, w.AutoReply, w.ClientReports));
    }
}
