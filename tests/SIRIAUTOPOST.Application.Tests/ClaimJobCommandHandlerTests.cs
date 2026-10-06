using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using static SIRIAUTOPOST.Application.Tests.SchedulingWorld;

namespace SIRIAUTOPOST.Application.Tests;

public class ClaimJobCommandHandlerTests
{
    private readonly SchedulingWorld _world = new(links: 1, posts: 1);
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly INotificationDispatcher _notifier = Substitute.For<INotificationDispatcher>();
    private readonly IScheduleRepository _schedules = Substitute.For<IScheduleRepository>();
    private readonly ICurrentDevice _current = Substitute.For<ICurrentDevice>();
    private readonly Guid _deviceId = Guid.NewGuid();

    private ClaimJobCommandHandler Handler()
    {
        _current.DeviceId.Returns(_deviceId);
        _current.WorkspaceId.Returns(_world.Ws.Id);
        _schedules.ListActiveAsync(_world.Ws.Id, Arg.Any<CancellationToken>()).Returns([]);
        var workspaces = Substitute.For<IWorkspaceRepository>();
        var topUp = new ScheduleTopUp(workspaces, _schedules, _world.Materializer(), Substitute.For<IMediaRepository>(), _uow, new FixedClock(Now),
            new TopUpThrottle(), NullLogger<ScheduleTopUp>.Instance);
        return new ClaimJobCommandHandler(
            _current, Substitute.For<IDeviceRepository>(), workspaces, Substitute.For<IAccountRepository>(), Substitute.For<IPostRepository>(),
            Substitute.For<IMediaRepository>(), Substitute.For<IUserRepository>(), Substitute.For<IPlanRepository>(), Substitute.For<ISetLinkRepository>(), Substitute.For<ILinkSetRepository>(),
            _schedules, Substitute.For<ICollectionRepository>(), Substitute.For<IPostBumpRepository>(), Substitute.For<IDeviceEventRepository>(),
            topUp, _notifier, _uow, new FixedClock(Now));
    }

    [Fact]
    public async Task A_post_that_changed_under_the_claim_means_no_job_now_and_nothing_is_announced()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<string?>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyConflictException());

        var job = await Handler().HandleAsync(new ClaimJobCommand());

        Assert.Null(job);
        await _uow.Received(1).ExecuteInTransactionAsync($"claim:{_deviceId:N}", Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()); // one claim of a device at a time
        _uow.Received().DiscardChanges();
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default, default, default, default!, default);
    }

    [Fact]
    public async Task Other_failures_still_reach_the_caller()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<string?>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthenticationException("gone"));

        await Assert.ThrowsAsync<AuthenticationException>(() => Handler().HandleAsync(new ClaimJobCommand()));
    }
}
