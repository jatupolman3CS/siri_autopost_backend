using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class ExtensionSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Settings_get_a_new_revision_only_when_they_change()
    {
        var c = ExtensionConfig.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(0, c.Revision);
        Assert.True(c.Save("""{"campaigns":[]}""", false, 0, true, Now));
        Assert.Equal((1, true, false), (c.Revision, c.UpdatedByDevice, c.HasContent));
        Assert.False(c.Save("""{"campaigns":[]}""", false, 1, false, Now)); // same JSON
        Assert.Equal(1, c.Revision);
        Assert.True(c.Save("""{"campaigns":[{}]}""", true, null, false, Now)); // null overwrites
        Assert.Equal((2, false, true), (c.Revision, c.UpdatedByDevice, c.HasContent));
    }

    [Fact]
    public void Saving_over_a_newer_revision_is_a_conflict()
    {
        var c = ExtensionConfig.Create(Guid.NewGuid(), Guid.NewGuid());
        c.Save("""{"campaigns":[]}""", false, 0, true, Now);
        Assert.Throws<ConflictException>(() => c.Save("""{"campaigns":[{}]}""", true, 0, false, Now));
        Assert.Equal(1, c.Revision);
    }

    [Theory]
    [InlineData("img1", true)]
    [InlineData("siri-0bb673f08935e293208d", true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("", false)]
    [InlineData("../etc", false)]
    [InlineData("has space", false)]
    public void Image_ids_are_the_extension_s_own_safe_ids(string id, bool ok) =>
        Assert.Equal(ok, ExtensionImage.ValidId(id));

    [Fact]
    public void Only_images_and_videos_are_stored()
    {
        var ws = Guid.NewGuid();
        var img = ExtensionImage.Create(ws, "img1", "a.png", "IMAGE/PNG", [1, 2, 3], Now);
        Assert.Equal(("image/png", 3L), (img.ContentType, img.Size));
        Assert.Throws<DomainException>(() => ExtensionImage.Create(ws, "img2", "a.txt", "text/plain", [1], Now));
        Assert.Throws<DomainException>(() => ExtensionImage.Create(ws, "img3", "a.png", "image/png", [], Now));
        Assert.Throws<DomainException>(() => ExtensionImage.Create(ws, "bad id", "a.png", "image/png", [1], Now));
    }

    [Fact]
    public void A_command_is_open_until_it_has_a_result_and_given_up_on_after_ten_minutes()
    {
        var cmd = DeviceCommand.Create(Guid.NewGuid(), Guid.NewGuid(), "start", "", Now);
        Assert.Equal(("{}", CommandStatus.Pending), (cmd.Args, cmd.Status));
        Assert.False(cmd.Stale(Now.AddMinutes(10)));
        Assert.True(cmd.Stale(Now.AddMinutes(11)));

        Assert.True(cmd.Send(Now.AddMinutes(1)));
        Assert.True(cmd.IsOpen); // handed out, no result yet: a sync hands it out again
        Assert.False(cmd.Send(Now.AddMinutes(2))); // the second delivery changes nothing
        Assert.Equal(Now.AddMinutes(1), cmd.SentAt);
        Assert.False(cmd.Stale(Now.AddMinutes(9)));
        Assert.True(cmd.Stale(Now.AddMinutes(11))); // lost for good: the device never answered

        cmd.Complete("""{"ok":true}""", Now.AddMinutes(2));
        Assert.Equal(CommandStatus.Done, cmd.Status);
        Assert.False(cmd.IsOpen);
        Assert.False(cmd.Stale(Now.AddMinutes(30)));
        Assert.Throws<DomainException>(() => DeviceCommand.Create(Guid.NewGuid(), Guid.NewGuid(), "rm", "{}", Now));
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("runNow", true)]
    [InlineData("testPost", true)]
    [InlineData("takeJobs", true)] // the schedules' "take the due posts now" makes the extension post
    [InlineData("stop", false)]
    [InlineData("syncNow", false)]
    [InlineData("clearLogs", false)]
    public void The_buttons_that_make_the_extension_post_are_refused_for_blocked_owners(string cmd, bool posts)
    {
        Assert.Contains(cmd, DeviceCommand.Allowed);
        Assert.Equal(posts, DeviceCommand.StartsPosting(cmd));
    }

    [Fact]
    public void Log_lines_are_cut_to_size()
    {
        var log = DeviceLog.Create(Guid.NewGuid(), 1, null, new string('x', 3000));
        Assert.Equal(("info", DeviceLog.MaxMessageLength), (log.Level, log.Message.Length));
    }
}
