using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class NotificationSettingsTests
{
    private static readonly Guid Set = Guid.NewGuid();
    private static readonly Guid Link = Guid.NewGuid();

    private static NotificationSettings Ready() => new()
    {
        Telegram = new TelegramChannel { On = true, Token = "tg-token", ChatId = "123" },
        Line = new LineChannel { On = true, Token = "line-token", To = "U123" },
    };

    [Fact]
    public void The_default_events_are_the_design_s()
    {
        var e = new NotifyEventSet();
        Assert.Equal(
            [false, true, true, true, true, true, true, false],
            [e.Success, e.Fail, e.Shot, e.Round, e.StartStop, e.Block, e.Offline, e.Quota]);
        Assert.Equal(NotifyChannel.Tg, new NotificationSettings().Channel);
    }

    [Fact]
    public void An_event_goes_to_the_workspace_channel_when_nothing_overrides_it()
    {
        var n = Ready();
        Assert.Equal(new NotifyRoute(true, false), n.Resolve(NotifyEvent.Fail, null, null));
        n.Channel = NotifyChannel.Both;
        Assert.Equal(new NotifyRoute(true, true), n.Resolve(NotifyEvent.Fail, null, null));
        n.Channel = NotifyChannel.Line;
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, null, null));
        n.Channel = NotifyChannel.Off;
        Assert.False(n.Resolve(NotifyEvent.Fail, null, null).Any);
    }

    [Fact]
    public void An_event_that_is_off_is_not_sent()
    {
        var n = Ready();
        Assert.False(n.Resolve(NotifyEvent.Success, null, null).Any);
        Assert.False(n.Resolve(NotifyEvent.Quota, null, null).Any);
        n.Events.Success = true;
        Assert.True(n.Resolve(NotifyEvent.Success, null, null).Any);
    }

    [Fact]
    public void A_channel_is_used_only_when_it_is_on_and_has_a_token_and_a_target()
    {
        var n = Ready();
        n.Channel = NotifyChannel.Both;

        n.Telegram.On = false;
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, null, null));
        n.Telegram.On = true;
        n.Telegram.Token = "";
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, null, null));
        n.Telegram.Token = "x";
        n.Telegram.ChatId = "";
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, null, null));
        n.Line.To = "";
        Assert.False(n.Resolve(NotifyEvent.Fail, null, null).Any);
    }

    [Fact]
    public void A_set_rule_beats_the_workspace_and_a_group_rule_beats_the_set()
    {
        var n = Ready();
        n.Channel = NotifyChannel.Tg;
        n.Sets[Set] = new SetNotifyRule { Channel = NotifyChannel.Line };

        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, Set, Link)); // the set's channel
        Assert.Equal(new NotifyRoute(true, false), n.Resolve(NotifyEvent.Fail, Guid.NewGuid(), Link)); // another set: the workspace's
        Assert.Equal(new NotifyRoute(true, false), n.Resolve(NotifyEvent.Fail, null, null));

        n.Sets[Set].Groups[Link] = new GroupNotifyRule { Channel = NotifyChannel.Both };
        Assert.Equal(new NotifyRoute(true, true), n.Resolve(NotifyEvent.Fail, Set, Link));
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, Set, Guid.NewGuid())); // another group: the set's
        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, Set, null)); // about the set only
    }

    [Fact]
    public void A_default_channel_in_a_rule_means_use_the_parent()
    {
        var n = Ready();
        n.Channel = NotifyChannel.Line;
        n.Sets[Set] = new SetNotifyRule { Channel = NotifyChannel.Default };
        n.Sets[Set].Groups[Link] = new GroupNotifyRule { Channel = NotifyChannel.Default };

        Assert.Equal(new NotifyRoute(false, true), n.Resolve(NotifyEvent.Fail, Set, Link));

        n.Sets[Set].Channel = NotifyChannel.Tg; // the set speaks, the group still defers to it
        Assert.Equal(new NotifyRoute(true, false), n.Resolve(NotifyEvent.Fail, Set, Link));
    }

    [Fact]
    public void Custom_events_follow_the_same_order()
    {
        var n = Ready();
        n.Sets[Set] = new SetNotifyRule { Events = new NotifyEventSet { Success = true, Fail = false } };

        Assert.True(n.Resolve(NotifyEvent.Success, Set, Link).Any);
        Assert.False(n.Resolve(NotifyEvent.Fail, Set, Link).Any);
        Assert.False(n.Resolve(NotifyEvent.Success, null, null).Any);

        n.Sets[Set].Groups[Link] = new GroupNotifyRule { Events = new NotifyEventSet { Success = false, Fail = true } };
        Assert.False(n.Resolve(NotifyEvent.Success, Set, Link).Any);
        Assert.True(n.Resolve(NotifyEvent.Fail, Set, Link).Any);
    }

    [Fact]
    public void Validation_trims_and_limits_the_values()
    {
        var n = Ready();
        n.Telegram.Token = "  abc  ";
        n.CommandsUsers = "  @boss ";
        n.Validate();
        Assert.Equal("abc", n.Telegram.Token);
        Assert.Equal("@boss", n.CommandsUsers);

        Assert.Throws<DomainException>(() => new NotificationSettings { Telegram = new TelegramChannel { Token = new string('x', 201) } }.Validate());
        Assert.Throws<DomainException>(() => new NotificationSettings { Line = new LineChannel { To = new string('x', 101) } }.Validate());
        Assert.Throws<DomainException>(() => new NotificationSettings { CommandsUsers = new string('x', 301) }.Validate());
        Assert.Throws<DomainException>(() => new NotificationSettings { Channel = (NotifyChannel)99 }.Validate());
        Assert.Throws<DomainException>(() => new NotificationSettings
        {
            Sets = Enumerable.Range(0, 101).ToDictionary(_ => Guid.NewGuid(), _ => new SetNotifyRule()),
        }.Validate());
    }

    [Fact]
    public void Auto_reply_rules_need_keywords_and_a_reply_or_an_inbox_message_within_limits()
    {
        AutoReplySettings With(Action<AutoReplyRule> edit)
        {
            var rule = new AutoReplyRule { Keywords = "ราคา, สนใจ", Reply = "ทักแชทนะคะ" };
            edit(rule);
            return new AutoReplySettings { On = true, Rules = [rule] };
        }

        With(_ => { }).Validate();
        With(r => { r.Reply = ""; r.Inbox = "ส่งรายละเอียดให้ทางแชท"; }).Validate();
        Assert.Throws<DomainException>(() => With(r => r.Keywords = " ").Validate());
        Assert.Throws<DomainException>(() => With(r => { r.Reply = ""; r.Inbox = ""; }).Validate());
        Assert.Throws<DomainException>(() => With(r => r.Keywords = new string('x', 301)).Validate());
        Assert.Throws<DomainException>(() => With(r => r.Reply = new string('x', 501)).Validate());
        Assert.Throws<DomainException>(() => With(r => r.Inbox = new string('x', 501)).Validate());

        var many = new AutoReplySettings { Rules = Enumerable.Range(0, 51).Select(_ => new AutoReplyRule { Keywords = "a", Reply = "b" }).ToList() };
        Assert.Throws<DomainException>(() => many.Validate());
    }

    [Fact]
    public void Auto_reply_scope_defaults_to_all()
    {
        var a = new AutoReplySettings { Rules = [new AutoReplyRule { Keywords = "a", Reply = "b", Scope = " " }] };
        a.Validate();
        Assert.Equal("all", a.Rules[0].Scope);
    }
}
