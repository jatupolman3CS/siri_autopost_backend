using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Domain.Tests;

// What a customer row may carry from a page the extension read: odd text and a time with the browser's offset.
public class LineChatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 3, 0, 0, TimeSpan.Zero);

    private static LineChat Make(string name, IEnumerable<string>? labels = null, DateTimeOffset? last = null) =>
        LineChat.Create(Guid.NewGuid(), Guid.NewGuid(), "U" + new string('1', 32), "https://chat.line.biz/Ubot/chat/U", name, labels, last, false, Now);

    [Fact]
    public void A_time_with_an_offset_is_kept_as_the_same_instant_in_UTC()
    {
        var local = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.FromHours(7));
        var chat = Make("สมชาย", last: local);
        Assert.Equal(TimeSpan.Zero, chat.LastMessageAt!.Value.Offset);
        Assert.Equal(local, chat.LastMessageAt);

        chat.Upsert("สมชาย", "https://chat.line.biz/Ubot/chat/U", null, local.AddHours(1), false, Now);
        Assert.Equal(TimeSpan.Zero, chat.LastMessageAt!.Value.Offset);
    }

    [Fact]
    public void Control_characters_are_dropped_from_the_name_and_the_labels()
    {
        var chat = Make("สม\0ชาย\n", ["VIP\0", "\0", "ใหม่"]);
        Assert.Equal("สมชาย", chat.Name);
        Assert.Equal(["VIP", "ใหม่"], chat.Labels);
    }

    [Fact]
    public void A_name_that_is_only_control_characters_falls_back_to_the_default()
    {
        Assert.Equal("ลูกค้า", Make("\0\0").Name);
    }
}
