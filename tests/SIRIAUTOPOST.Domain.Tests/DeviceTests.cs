using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class DeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static (Device Device, SocialAccount Account) Paired()
    {
        var ws = Guid.NewGuid();
        var device = Device.Pair(ws, " Office PC ", "Chrome", "2.1.0", "hash", Now);
        var account = SocialAccount.Connect(ws, device, 0);
        account.SyncGroups([new GroupLink { Name = "Plants", Url = "https://www.facebook.com/groups/plants" }]);
        return (device, account);
    }

    [Fact]
    public void A_device_is_online_for_100_seconds_after_it_called_in()
    {
        var (device, _) = Paired();
        Assert.Equal("Office PC", device.Name);
        Assert.True(device.IsOnline(Now.AddSeconds(100)));
        Assert.False(device.IsOnline(Now.AddSeconds(101)));
        device.Seen("2.2.0", Now.AddMinutes(5));
        Assert.True(device.IsOnline(Now.AddMinutes(6)));
        Assert.Equal("2.2.0", device.Version);
    }

    [Fact]
    public void A_pairing_code_works_once_and_expires_after_10_minutes()
    {
        var code = DevicePairing.Create(Guid.NewGuid(), "ABCD-2345", Now);
        Assert.Throws<DomainException>(() => code.Use(Now.AddMinutes(11)));
        code.Use(Now.AddMinutes(9));
        Assert.Throws<DomainException>(() => code.Use(Now.AddMinutes(9)));
    }

    [Fact]
    public void Synced_groups_become_the_account_groups_and_resolve_to_urls()
    {
        var (_, account) = Paired();
        account.SyncGroups(
        [
            new GroupLink { Name = "Plants", Url = "https://www.facebook.com/groups/plants" },
            new GroupLink { Name = "Plants", Url = "https://www.facebook.com/groups/duplicate" },
            new GroupLink { Name = "", Url = "https://www.facebook.com/groups/noname" },
            new GroupLink { Name = "No link", Url = " " },
        ]);
        Assert.Equal(["Plants", "https://www.facebook.com/groups/noname"], account.Groups);
        Assert.Equal("https://www.facebook.com/groups/plants", account.UrlFor("Plants"));
        Assert.Null(account.UrlFor("No link"));
        Assert.True(account.IsConnected);
    }

    [Fact]
    public void Unbinding_keeps_the_account_but_it_needs_a_new_login()
    {
        var (_, account) = Paired();
        account.Disconnect();
        Assert.False(account.IsConnected);
        Assert.False(account.CanPost);
    }

    private static Post Due(SocialAccount account) =>
        Post.Schedule(account.WorkspaceId, account, "Plants", "hello", [], Now.AddMinutes(1), Now);

    [Fact]
    public void A_claimed_post_is_posted_once()
    {
        var (device, account) = Paired();
        var post = Due(account);
        post.Claim(device.Id, Now.AddMinutes(2));
        Assert.Equal(PostStatus.Posting, post.Status);
        Assert.Throws<DomainException>(() => post.Claim(device.Id, Now.AddMinutes(2)));

        post.CompletePosted(awaitingApproval: false, Now.AddMinutes(3));
        Assert.Equal(PostStatus.Success, post.Status);
        Assert.Equal(Now.AddMinutes(3), post.PublishedAt);
        Assert.Null(post.ClaimedByDeviceId);
        Assert.Throws<DomainException>(() => post.CompletePosted(false, Now.AddMinutes(4)));
    }

    [Fact]
    public void A_group_that_needs_approval_leaves_the_post_pending()
    {
        var (device, account) = Paired();
        var post = Due(account);
        post.Claim(device.Id, Now.AddMinutes(2));
        post.CompletePosted(awaitingApproval: true, Now.AddMinutes(3));
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal(FailureCode.PendingApproval, post.FailureCode);
        Assert.True(post.IsOpenError);
    }

    [Fact]
    public void A_failed_post_keeps_the_reason_and_can_be_retried()
    {
        var (device, account) = Paired();
        var post = Due(account);
        post.Claim(device.Id, Now.AddMinutes(2));
        Assert.False(post.ClaimExpired(Now.AddMinutes(17)));
        Assert.True(post.ClaimExpired(Now.AddMinutes(18)));
        post.Fail(FailureCode.Session, new string('x', 900), Now.AddMinutes(3));
        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Equal(Post.MaxDetailLength, post.FailureDetail!.Length);
        post.Retry(Now.AddMinutes(4));
        Assert.Null(post.FailureDetail);
        Assert.Equal(PostStatus.Queued, post.Status);
    }

    [Theory]
    [InlineData(OfflinePolicy.Skip, "day", 10)]
    [InlineData(OfflinePolicy.Queue, "30m", 30)]
    [InlineData(OfflinePolicy.Queue, "2h", 120)]
    [InlineData(OfflinePolicy.Notify, "day", 1440)]
    public void The_offline_policy_decides_how_late_a_post_may_go_out(OfflinePolicy policy, string window, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), new OfflineSettings { Policy = policy, Window = window }.MaxLateness);
    }

}
