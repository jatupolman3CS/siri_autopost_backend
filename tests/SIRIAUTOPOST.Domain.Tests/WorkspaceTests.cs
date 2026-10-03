using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class WorkspaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Requires_a_name()
    {
        Assert.Throws<DomainException>(() => Workspace.Create(Guid.NewGuid(), "  ", Now));
        Assert.Equal("Shop", Workspace.Create(Guid.NewGuid(), " Shop ", Now).Name);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    [InlineData(5, 61)]
    public void Rejects_bad_delay_ranges(int min, int max)
    {
        var ws = Workspace.Create(Guid.NewGuid(), "Shop", Now);
        Assert.Throws<DomainException>(() => ws.UpdateAntiBan(new AntiBanSettings { Min = min, Max = max }, true));
    }

    [Fact]
    public void Rejects_daily_limits_out_of_range()
    {
        var ws = Workspace.Create(Guid.NewGuid(), "Shop", Now);
        var s = new AntiBanSettings { Limits = new PlatformLimits { Fb = 0 } };
        Assert.Throws<DomainException>(() => ws.UpdateAntiBan(s, true));
    }

    [Fact]
    public void Lower_plans_cannot_change_human_like_behaviour()
    {
        var ws = Workspace.Create(Guid.NewGuid(), "Shop", Now);
        ws.UpdateAntiBan(new AntiBanSettings { Min = 5, Max = 20, Typing = false, Warmup = true }, advancedAllowed: false);

        Assert.Equal(5, ws.AntiBan.Min);
        Assert.True(ws.AntiBan.Typing);
        Assert.False(ws.AntiBan.Warmup);
    }

    [Fact]
    public void Offline_window_must_be_known()
    {
        var ws = Workspace.Create(Guid.NewGuid(), "Shop", Now);
        Assert.Throws<DomainException>(() => ws.UpdateOffline(new OfflineSettings { Window = "3d" }));
        ws.UpdateOffline(new OfflineSettings { Policy = OfflinePolicy.Skip, Window = "day" });
        Assert.Equal(OfflinePolicy.Skip, ws.Offline.Policy);
    }
}
