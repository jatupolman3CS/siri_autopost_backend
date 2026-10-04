using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class DeviceAutoPauseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 3, 30, 0, TimeSpan.Zero);

    private static Device NewDevice() => Device.Pair(Guid.NewGuid(), "Laptop", "Chrome", "2.2", "hash", Now);

    [Fact]
    public void A_new_device_is_not_auto_paused()
    {
        var d = NewDevice();
        Assert.False(d.IsAutoPaused(Now));
        Assert.Null(d.AutoPausedUntil);
    }

    [Fact]
    public void A_pause_lasts_until_its_end()
    {
        var d = NewDevice();

        Assert.True(d.AutoPause(Now.AddHours(24), "Facebook บล็อก", Now));
        Assert.True(d.IsAutoPaused(Now.AddHours(23)));
        Assert.False(d.IsAutoPaused(Now.AddHours(24)));
        Assert.Equal("Facebook บล็อก", d.AutoPauseReason);
    }

    [Fact]
    public void A_second_pause_only_extends_the_first_and_does_not_announce_again()
    {
        var d = NewDevice();
        d.AutoPause(Now.AddHours(3), "a", Now);

        Assert.False(d.AutoPause(Now.AddHours(2), "shorter", Now));
        Assert.Equal(Now.AddHours(3), d.AutoPausedUntil);
        Assert.Equal("a", d.AutoPauseReason);

        Assert.False(d.AutoPause(Now.AddHours(5), "longer", Now));
        Assert.Equal(Now.AddHours(5), d.AutoPausedUntil);
        Assert.Equal("longer", d.AutoPauseReason);
    }

    [Fact]
    public void A_pause_that_is_over_can_be_cleared_once_and_a_new_one_is_announced_again()
    {
        var d = NewDevice();
        d.AutoPause(Now.AddHours(1), "a", Now);

        Assert.False(d.EndAutoPause(Now.AddMinutes(30))); // not over yet
        Assert.True(d.EndAutoPause(Now.AddHours(1)));
        Assert.False(d.EndAutoPause(Now.AddHours(2))); // nothing left to clear
        Assert.Null(d.AutoPausedUntil);
        Assert.Null(d.AutoPauseReason);

        Assert.True(d.AutoPause(Now.AddHours(5), "b", Now.AddHours(2)));
    }

    [Fact]
    public void The_owner_can_lift_a_pause_early()
    {
        var d = NewDevice();
        d.AutoPause(Now.AddHours(10), "a", Now);
        Assert.True(d.EndAutoPause(Now, force: true));
        Assert.False(d.IsAutoPaused(Now));
    }

    [Fact]
    public void A_pause_must_end_in_the_future()
    {
        Assert.Throws<DomainException>(() => NewDevice().AutoPause(Now, "x", Now));
    }
}
