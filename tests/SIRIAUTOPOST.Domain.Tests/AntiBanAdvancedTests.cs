using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class AntiBanAdvancedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 3, 0, 0, TimeSpan.Zero);

    private static Workspace Ws() => Workspace.Create(Guid.NewGuid(), "Shop", Now);

    [Fact]
    public void The_defaults_are_the_design_s()
    {
        var a = new AdvancedAntiBanSettings();
        Assert.Equal(2, a.MinGap);
        Assert.Equal(0, a.DailyAll);
        Assert.Equal(24, a.BlockMin);
        Assert.Equal(48, a.BlockMax);
        Assert.Equal(4, a.FailStreak);
        Assert.Equal(10, a.RecentAvoid);
        Assert.Equal(0, a.Cooldown);
        Assert.True(a.Focus);
        Assert.Equal(3, a.AutoOffFails);
        Assert.Equal(30, a.StopFailPct);
        a.Validate();
        new AntiBanSettings().Validate();
    }

    [Theory]
    [InlineData(nameof(AdvancedAntiBanSettings.MinGap), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.MinGap), 61)]
    [InlineData(nameof(AdvancedAntiBanSettings.DailyAll), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.DailyAll), 501)]
    [InlineData(nameof(AdvancedAntiBanSettings.BlockMin), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.BlockMin), 169)]
    [InlineData(nameof(AdvancedAntiBanSettings.BlockMax), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.BlockMax), 169)]
    [InlineData(nameof(AdvancedAntiBanSettings.FailStreak), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.FailStreak), 21)]
    [InlineData(nameof(AdvancedAntiBanSettings.RecentAvoid), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.RecentAvoid), 51)]
    [InlineData(nameof(AdvancedAntiBanSettings.Cooldown), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.Cooldown), 169)]
    [InlineData(nameof(AdvancedAntiBanSettings.AutoOffFails), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.AutoOffFails), 21)]
    [InlineData(nameof(AdvancedAntiBanSettings.StopFailPct), -1)]
    [InlineData(nameof(AdvancedAntiBanSettings.StopFailPct), 101)]
    public void Numbers_out_of_range_are_refused(string field, int value)
    {
        var a = new AdvancedAntiBanSettings();
        typeof(AdvancedAntiBanSettings).GetProperty(field)!.SetValue(a, value);
        Assert.Throws<DomainException>(() => a.Validate());
        Assert.Throws<DomainException>(() => new AntiBanSettings { Advanced = a }.Validate());
    }

    [Theory]
    [InlineData(nameof(AdvancedAntiBanSettings.MinGap), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.MinGap), 60)]
    [InlineData(nameof(AdvancedAntiBanSettings.DailyAll), 500)]
    [InlineData(nameof(AdvancedAntiBanSettings.FailStreak), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.FailStreak), 20)]
    [InlineData(nameof(AdvancedAntiBanSettings.RecentAvoid), 50)]
    [InlineData(nameof(AdvancedAntiBanSettings.Cooldown), 168)]
    [InlineData(nameof(AdvancedAntiBanSettings.AutoOffFails), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.StopFailPct), 0)]
    [InlineData(nameof(AdvancedAntiBanSettings.StopFailPct), 100)]
    public void The_edges_of_the_ranges_are_allowed(string field, int value)
    {
        var a = new AdvancedAntiBanSettings();
        typeof(AdvancedAntiBanSettings).GetProperty(field)!.SetValue(a, value);
        a.Validate();
    }

    [Fact]
    public void The_longest_block_pause_cannot_be_shorter_than_the_shortest()
    {
        Assert.Throws<DomainException>(() => new AdvancedAntiBanSettings { BlockMin = 48, BlockMax = 24 }.Validate());
        new AdvancedAntiBanSettings { BlockMin = 24, BlockMax = 24 }.Validate();
    }

    [Fact]
    public void Pro_plans_change_the_advanced_numbers_and_lower_plans_keep_theirs()
    {
        var ws = Ws();
        var custom = new AdvancedAntiBanSettings { MinGap = 10, DailyAll = 120, Cooldown = 6 };

        ws.UpdateAntiBan(new AntiBanSettings { Advanced = custom }, advancedAllowed: true);
        Assert.Equal(10, ws.AntiBan.Advanced.MinGap);
        Assert.Equal(120, ws.AntiBan.Advanced.DailyAll);

        ws.UpdateAntiBan(new AntiBanSettings { Min = 5, Max = 20, Advanced = new AdvancedAntiBanSettings { MinGap = 1, DailyAll = 0 } }, advancedAllowed: false);
        Assert.Equal(5, ws.AntiBan.Min); // the basic numbers still change
        Assert.Equal(10, ws.AntiBan.Advanced.MinGap);
        Assert.Equal(120, ws.AntiBan.Advanced.DailyAll);
        Assert.Equal(6, ws.AntiBan.Advanced.Cooldown);
    }

    [Fact]
    public void A_lower_plan_is_not_refused_for_advanced_numbers_it_cannot_change()
    {
        var ws = Ws();
        // Out of range, but ignored on this plan.
        ws.UpdateAntiBan(new AntiBanSettings { Advanced = new AdvancedAntiBanSettings { MinGap = 999 } }, advancedAllowed: false);
        Assert.Equal(2, ws.AntiBan.Advanced.MinGap);
        Assert.Throws<DomainException>(() =>
            ws.UpdateAntiBan(new AntiBanSettings { Advanced = new AdvancedAntiBanSettings { MinGap = 999 } }, advancedAllowed: true));
    }

    [Fact]
    public void Plan_features_follow_the_plan()
    {
        User Of(PlanKey p) => User.Create("a@b.co", "A", UserRole.User, p, Now);
        Assert.All([PlanKey.Free, PlanKey.Basic], p =>
        {
            Assert.False(Of(p).HasNotifications);
            Assert.False(Of(p).HasAutoReply);
            Assert.False(Of(p).HasClientReports);
        });
        Assert.All([PlanKey.Pro, PlanKey.Agency], p =>
        {
            Assert.True(Of(p).HasNotifications);
            Assert.True(Of(p).HasAutoReply);
        });
        Assert.False(Of(PlanKey.Pro).HasClientReports);
        Assert.True(Of(PlanKey.Agency).HasClientReports);
    }
}
