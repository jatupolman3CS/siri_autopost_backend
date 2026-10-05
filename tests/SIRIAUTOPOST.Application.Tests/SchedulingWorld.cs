using NSubstitute;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Tests;

/// <summary>A source whose numbers come from a seeded generator: random enough to shuffle, the same every run.</summary>
internal sealed class SeededRandom(int seed) : IRandomSource
{
    private readonly Random _random = new(seed);

    public double NextDouble() => _random.NextDouble();
}

/// <summary>
/// One workspace of the schedule engine with its repositories replaced by substitutes: a connected Facebook account, a
/// collection of posts, a link set of groups. Saturday 3 Oct 2026, 10:30 in Bangkok is "now".
/// </summary>
internal sealed class SchedulingWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 30, 0, TimeSpan.FromHours(7));
    public static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);
    public static readonly DateOnly Today = new(2026, 10, 3);

    public Workspace Ws { get; } = Workspace.Create(Guid.NewGuid(), "Shop", Now);
    public SocialAccount Page { get; }
    public PostCollection Collection { get; }
    public List<CollectionPost> Posts { get; } = [];
    public LinkSet Set { get; private set; }
    public List<SetLink> Links { get; } = [];
    public List<SocialAccount> Accounts { get; } = [];

    public IPostRepository PostRepo { get; } = Substitute.For<IPostRepository>();
    public ICollectionRepository Collections { get; } = Substitute.For<ICollectionRepository>();
    public ICollectionPostRepository CollectionPosts { get; } = Substitute.For<ICollectionPostRepository>();
    public ILinkSetRepository LinkSets { get; } = Substitute.For<ILinkSetRepository>();
    public ISetLinkRepository SetLinks { get; } = Substitute.For<ISetLinkRepository>();
    public IAccountRepository AccountRepo { get; } = Substitute.For<IAccountRepository>();

    /// <summary>What <c>ListScheduleKeysAsync</c> says already exists.</summary>
    public List<(string TargetKey, string SlotKey)> ExistingKeys { get; } = [];
    /// <summary>What <c>ListRecentCollectionPostIdsByLinkAsync</c> says the links had lately (newest first).</summary>
    public Dictionary<Guid, IReadOnlyList<Guid>> History { get; } = new();
    /// <summary>What <c>ListAllCollectionPostIdsByLinkAsync</c> says the links ever had (the order means nothing).</summary>
    public Dictionary<Guid, IReadOnlyList<Guid>> AllHistory { get; } = new();
    /// <summary>The posts the materializer added.</summary>
    public List<Post> Added { get; } = [];

    public SchedulingWorld(int links = 3, int posts = 3)
    {
        var device = Device.Pair(Ws.Id, "PC", "Chrome", "2.2.0", "hash", Now);
        Page = SocialAccount.Connect(Ws.Id, device, 0);
        Accounts.Add(Page);
        Collection = PostCollection.Create(Ws.Id, "โปรโมชัน", null, Now);
        for (var i = 1; i <= posts; i++) AddPost($"โพสต์ {i}");
        Set = LinkSet.Create(Ws.Id, "กลุ่มขายของ", null, Now);
        for (var i = 0; i < links; i++) AddLink($"group{i}", $"{(char)('A' + i)}1");

        Collections.GetAsync(Ws.Id, Collection.Id, Arg.Any<CancellationToken>()).Returns(Collection);
        CollectionPosts.ListByCollectionAsync(Ws.Id, Collection.Id, Arg.Any<CancellationToken>()).Returns(_ => Posts.ToList());
        LinkSets.GetAsync(Ws.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Set);
        SetLinks.ListBySetAsync(Ws.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Links.ToList());
        AccountRepo.ListAsync(Ws.Id, Arg.Any<CancellationToken>()).Returns(_ => Accounts.ToList());
        PostRepo.ListScheduleKeysAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => ExistingKeys.ToList());
        PostRepo.ListRecentCollectionPostIdsByLinkAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => History);
        PostRepo.ListAllCollectionPostIdsByLinkAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => AllHistory);
        PostRepo.When(r => r.Add(Arg.Any<Post>())).Do(call => Added.Add(call.Arg<Post>()));
    }

    public CollectionPost AddPost(string text, params Guid[] media)
    {
        var post = CollectionPost.Create(Collection, text, media, Now.AddSeconds(Posts.Count));
        Posts.Add(post);
        return post;
    }

    public SetLink AddLink(string slug, string code = "", int dailyMax = 0, string? name = null)
    {
        var link = SetLink.Create(Ws.Id, Set.Id, name ?? $"กลุ่ม {slug}", $"https://www.facebook.com/groups/{slug}", code, dailyMax, Now, Links.Count);
        Links.Add(link);
        return link;
    }

    /// <summary>An account of the workspace that is not connected to a browser, like the demo accounts.</summary>
    public SocialAccount AddOtherAccount()
    {
        var account = SocialAccount.Create(Ws.Id, Platform.Ig, "@shop", "", "ฟีด");
        Accounts.Add(account);
        Set.Update(Set.Name, Set.PostAsAccountId, [.. Set.AccountIds, account.Id]);
        return account;
    }

    /// <summary>The advanced anti-ban numbers (Pro).</summary>
    public void SetAdvanced(Action<AdvancedAntiBanSettings> change)
    {
        var settings = AntiBanDto.From(Ws.AntiBan).ToSettings();
        change(settings.Advanced);
        Ws.UpdateAntiBan(settings, advancedAllowed: true);
    }

    public void SetDelay(int min, int max)
    {
        var settings = AntiBanDto.From(Ws.AntiBan).ToSettings();
        settings.Min = min;
        settings.Max = max;
        Ws.UpdateAntiBan(settings, advancedAllowed: true);
    }

    /// <summary>The anti-ban "shuffle the order of target groups" switch (on by default: a slot's groups are taken in a random order).</summary>
    public void SetGroupShuffle(bool on)
    {
        var settings = AntiBanDto.From(Ws.AntiBan).ToSettings();
        settings.Shuffle = on;
        Ws.UpdateAntiBan(settings, advancedAllowed: true);
    }

    public Schedule NewSchedule(
        ScheduleMode mode = ScheduleMode.Daily, string[]? times = null, PostOrder order = PostOrder.Rotate, DateOnly? start = null,
        string onceTime = "14:00", int everyHours = 6, string firstTime = "09:00", string dripFrom = "09:00", string dripTo = "21:00", int dripCount = 3,
        Dictionary<string, IReadOnlyList<string>>? overrides = null, int offsetMinutes = 420, bool startNow = false,
        PostRepeat repeat = PostRepeat.Recent) =>
        Schedule.Create(Ws.Id, "ตาราง", Collection.Id, Set.Id, mode, times ?? ["18:00"], everyHours, firstTime, start ?? Today, onceTime, order,
            dripFrom, dripTo, dripCount, 0, 0, overrides, offsetMinutes, Now, startNow, repeat);

    public ScheduleMaterializer Materializer(IRandomSource? random = null) =>
        new(PostRepo, Collections, CollectionPosts, LinkSets, SetLinks, AccountRepo, random ?? new FixedRandom(0.5));

    public Task<MaterializeResult> RunAsync(
        Schedule s, DateOnly from, DateOnly to, IRandomSource? random = null, DateTimeOffset? now = null) =>
        Materializer(random).MaterializeAsync(Ws, s, from, to, now ?? Now);

    /// <summary>The local time of a post in the schedule's calendar.</summary>
    public static DateTime Local(Post p, int offsetMinutes = 420) => p.ScheduledAt.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).DateTime;
}
