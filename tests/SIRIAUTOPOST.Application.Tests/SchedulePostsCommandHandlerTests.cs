using NSubstitute;
using SIRIAUTOPOST.Application.Features.Posts;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

public class SchedulePostsCommandHandlerTests
{
    // Saturday 3 Oct 2026, 10:30 in Bangkok.
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 30, 0, TimeSpan.FromHours(7));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Workspace _ws;
    private readonly SocialAccount _page;
    private readonly SocialAccount _ig;
    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly List<Post> _added = [];

    public SchedulePostsCommandHandlerTests()
    {
        _ws = Workspace.Create(_userId, "Shop", Now);
        _page = SocialAccount.Create(_ws.Id, Platform.Fb, "Page", "", "เพจ", groups: ["A", "B", "C"]);
        _ig = SocialAccount.Create(_ws.Id, Platform.Ig, "@shop", "", "ฟีด");
        _workspaces.GetByIdAsync(_ws.Id, Arg.Any<CancellationToken>()).Returns(_ws);
        _accounts.ListAsync(_ws.Id, Arg.Any<CancellationToken>()).Returns([_page, _ig]);
        _posts.When(p => p.Add(Arg.Any<Post>())).Do(c => _added.Add(c.Arg<Post>()));
    }

    private SchedulePostsCommandHandler Handler(Guid? userId = null) =>
        new(_workspaces, _accounts, _media, _posts, new FakeUser(userId ?? _userId), new FixedRandom(0.5), _uow, new FixedClock(Now));

    private SchedulePostsCommand Command(string repeat = "none", bool useDelay = true, params TargetSelection[] targets) =>
        new(_ws.Id, "โปรวันนี้", null, Now.AddHours(1), useDelay, repeat,
            targets.Length > 0 ? targets : [new TargetSelection(_page.Id, ["A", "B"]), new TargetSelection(_ig.Id, null)]);

    [Fact]
    public async Task One_task_per_selected_group_plus_the_default_target_spaced_by_smart_delay()
    {
        var result = await Handler().HandleAsync(Command());

        Assert.Equal(3, result.Created);
        Assert.Equal(["A", "B", "ฟีด"], _added.Select(p => p.Target));
        // Default delay 3–12 min, fixed random 0.5 => 7.5 min apart.
        Assert.Equal(TimeSpan.FromMinutes(7.5), _added[1].ScheduledAt - _added[0].ScheduledAt);
        Assert.Equal(TimeSpan.FromMinutes(15), _added[2].ScheduledAt - _added[0].ScheduledAt);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_smart_delay_every_task_starts_at_once()
    {
        await Handler().HandleAsync(Command(useDelay: false));
        Assert.Single(_added.Select(p => p.ScheduledAt).Distinct());
    }

    [Theory]
    [InlineData("daily", 14)]
    [InlineData("weekdays", 10)]
    [InlineData("weekly", 2)]
    public async Task Repeats_over_the_next_two_weeks(string repeat, int days)
    {
        var result = await Handler().HandleAsync(Command(repeat, true, new TargetSelection(_ig.Id, null)));
        Assert.Equal(days, result.Created);
        Assert.DoesNotContain(_added, p => repeat == "weekdays" && p.ScheduledAt.ToOffset(TimeSpan.FromHours(7)).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public async Task Group_accounts_need_known_groups()
    {
        await Assert.ThrowsAsync<DomainException>(() => Handler().HandleAsync(Command("none", true, new TargetSelection(_page.Id, []))));
        await Assert.ThrowsAsync<DomainException>(() => Handler().HandleAsync(Command("none", true, new TargetSelection(_page.Id, ["Z"]))));
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Someone_elses_workspace_reads_as_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Handler(Guid.NewGuid()).HandleAsync(Command()));
}
