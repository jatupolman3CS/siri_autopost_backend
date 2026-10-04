using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Reports;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

// The report's arithmetic: grouping, counting, rates and ordering, without a database.
public class ReportBuilderTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly Guid Set = Guid.NewGuid();

    private static SetLink Link(string name, string slug) => SetLink.Create(Ws, Set, name, $"https://www.facebook.com/groups/{slug}", "", 0, To);

    private static IReadOnlyDictionary<Guid, SetLink> Links(params SetLink[] links) => links.ToDictionary(l => l.Id);

    private static PostOutcome Outcome(
        PostStatus status, Guid? link = null, string target = "กลุ่ม", Platform platform = Platform.Fb, string? url = null, Guid? cp = null, double hoursAgo = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), platform, link, target, url, cp, status, To.AddHours(-hoursAgo), status is PostStatus.Success or PostStatus.Pending ? To.AddHours(-hoursAgo) : null);

    private static ReportDto Build(IReadOnlyList<PostOutcome> outcomes, IReadOnlyDictionary<Guid, SetLink> links) =>
        ReportBuilder.Build(7, To.AddDays(-7), To, outcomes, links);

    [Theory]
    [InlineData(0, 0, 100)] // nothing finished yet
    [InlineData(1, 0, 100)]
    [InlineData(0, 3, 0)]
    [InlineData(3, 1, 75)]
    [InlineData(2, 1, 67)] // 66.67
    [InlineData(1, 2, 33)] // 33.33
    [InlineData(1, 7, 13)] // 12.5 rounds up
    [InlineData(199, 1, 100)] // 99.5 rounds up
    public void The_rate_is_the_share_of_posts_that_went_out_among_those_that_finished(int posted, int failed, int rate) =>
        Assert.Equal(rate, ReportBuilder.RateOf(posted, failed));

    [Fact]
    public void Posts_are_counted_per_link_by_outcome()
    {
        var a = Link("กลุ่มเอ", "a");
        var b = Link("กลุ่มบี", "b");
        var outcomes = new[]
        {
            Outcome(PostStatus.Success, a.Id), Outcome(PostStatus.Success, a.Id), Outcome(PostStatus.Success, a.Id),
            Outcome(PostStatus.Failed, a.Id), Outcome(PostStatus.Pending, a.Id),
            Outcome(PostStatus.Success, b.Id), Outcome(PostStatus.Failed, b.Id), Outcome(PostStatus.Failed, b.Id),
        };

        var report = Build(outcomes, Links(a, b));

        Assert.Equal((7, To.AddDays(-7), To), (report.Days, report.From, report.To));
        Assert.Equal(2, report.Groups.Count);
        var (ga, gb) = (report.Groups[0], report.Groups[1]);
        Assert.Equal((a.Id, "กลุ่มเอ", a.Url, 3, 1, 1, 75), (ga.LinkId, ga.Name, ga.Url, ga.Posted, ga.Pending, ga.Failed, ga.Rate));
        Assert.Equal((b.Id, "กลุ่มบี", 1, 0, 2, 33), (gb.LinkId, gb.Name, gb.Posted, gb.Pending, gb.Failed, gb.Rate));
        Assert.Empty(report.Posts); // the posts section is filled by the composer
    }

    [Fact]
    public void Groups_are_sorted_by_posted_then_by_name()
    {
        var links = new[] { Link("C", "c"), Link("A", "a"), Link("B", "b"), Link("D", "d") };
        var outcomes = new List<PostOutcome>();
        outcomes.AddRange(Enumerable.Repeat(0, 2).Select(_ => Outcome(PostStatus.Success, links[0].Id))); // C: 2
        outcomes.AddRange(Enumerable.Repeat(0, 5).Select(_ => Outcome(PostStatus.Success, links[1].Id))); // A: 5
        outcomes.AddRange(Enumerable.Repeat(0, 2).Select(_ => Outcome(PostStatus.Success, links[2].Id))); // B: 2
        outcomes.Add(Outcome(PostStatus.Failed, links[3].Id)); // D: 0

        var report = Build(outcomes, Links(links));

        Assert.Equal(["A", "B", "C", "D"], report.Groups.Select(g => g.Name));
    }

    [Fact]
    public void A_link_without_posts_in_the_period_is_not_listed_and_the_links_state_is_reported()
    {
        var quiet = Link("เงียบ", "quiet");
        var sick = Link("มีปัญหา", "sick");
        sick.AutoDisable(4);
        var waiting = Link("รออนุมัติ", "waiting");
        waiting.RecordPendingApproval();

        var report = Build([Outcome(PostStatus.Success, sick.Id), Outcome(PostStatus.Pending, waiting.Id)], Links(quiet, sick, waiting));

        Assert.Equal(2, report.Groups.Count);
        var s = report.Groups.Single(g => g.LinkId == sick.Id);
        Assert.Equal((false, LinkHealth.Off), (s.Enabled, s.Health));
        var w = report.Groups.Single(g => g.LinkId == waiting.Id);
        Assert.Equal((true, LinkHealth.Pending, 0, 1, 0, 100), (w.Enabled, w.Health, w.Posted, w.Pending, w.Failed, w.Rate)); // only waiting: nothing failed
    }

    [Fact]
    public void Posts_without_a_link_are_grouped_by_what_they_were_called_and_the_platform()
    {
        var outcomes = new[]
        {
            Outcome(PostStatus.Success, target: "กลุ่มเก่า", url: "https://www.facebook.com/groups/old", hoursAgo: 5),
            Outcome(PostStatus.Success, target: "กลุ่มเก่า", url: "https://www.facebook.com/groups/old-new", hoursAgo: 2), // the newest address wins
            Outcome(PostStatus.Failed, target: "กลุ่มเก่า"),
            Outcome(PostStatus.Success, target: "Feed", platform: Platform.Ig),
            Outcome(PostStatus.Success, target: "Feed", platform: Platform.Fb), // same name, other platform: its own row
        };

        var report = Build(outcomes, Links());

        Assert.Equal(3, report.Groups.Count);
        var old = report.Groups.Single(g => g.Name == "กลุ่มเก่า");
        Assert.Equal((null, "https://www.facebook.com/groups/old-new", Platform.Fb, 2, 1, 67, true, LinkHealth.Ok),
            (old.LinkId, old.Url, old.Platform, old.Posted, old.Failed, old.Rate, old.Enabled, old.Health));
        Assert.Equal([Platform.Fb, Platform.Ig], report.Groups.Where(g => g.Name == "Feed").Select(g => g.Platform).Order());
        Assert.All(report.Groups.Where(g => g.Name == "Feed"), g => Assert.Null(g.Url));
    }

    [Fact]
    public void A_post_whose_link_was_deleted_falls_back_to_its_name()
    {
        var alive = Link("ยังอยู่", "alive");
        var gone = Guid.NewGuid();

        var report = Build(
            [Outcome(PostStatus.Success, alive.Id), Outcome(PostStatus.Success, gone, target: "ถูกลบไปแล้ว", url: "https://www.facebook.com/groups/gone")],
            Links(alive));

        var ghost = report.Groups.Single(g => g.Name == "ถูกลบไปแล้ว");
        Assert.Equal((null, "https://www.facebook.com/groups/gone", 1), (ghost.LinkId, ghost.Url, ghost.Posted));
        Assert.Equal(alive.Id, report.Groups.Single(g => g.LinkId != null).LinkId);
    }

    [Fact]
    public void An_empty_period_is_an_empty_report()
    {
        var report = Build([], Links(Link("x", "x")));

        Assert.Empty(report.Groups);
        Assert.Empty(report.Posts);
    }

    [Fact]
    public void Usage_counts_successful_posts_per_collection_post_most_used_first()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var outcomes = new[]
        {
            Outcome(PostStatus.Success, cp: a), Outcome(PostStatus.Success, cp: a), Outcome(PostStatus.Success, cp: a),
            Outcome(PostStatus.Success, cp: b),
            Outcome(PostStatus.Failed, cp: c), Outcome(PostStatus.Pending, cp: c), // not a success: not "used"
            Outcome(PostStatus.Success, cp: null), // came from the old composer
        };

        Assert.Equal([(a, 3), (b, 1)], ReportBuilder.UsageOf(outcomes));
    }

    [Fact]
    public void Equal_usage_is_ordered_by_id_so_the_order_never_changes()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var outcomes = ids.Select(id => Outcome(PostStatus.Success, cp: id)).ToList();

        var first = ReportBuilder.UsageOf(outcomes).Select(x => x.CollectionPostId);
        var again = ReportBuilder.UsageOf(outcomes.AsEnumerable().Reverse().ToList()).Select(x => x.CollectionPostId);

        Assert.Equal(ids.Order(), first);
        Assert.Equal(first, again);
    }
}
