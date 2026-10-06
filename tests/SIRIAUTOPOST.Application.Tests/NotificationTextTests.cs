using SIRIAUTOPOST.Application.Common;

namespace SIRIAUTOPOST.Application.Tests;

// The markup of a notification, the picture a device may send with a result, and the lines under a group's name.
public class NotificationTextTests
{
    // ---------- markup ----------

    [Theory]
    [InlineData("กลุ่มร้านค้า", "กลุ่มร้านค้า")]
    [InlineData("A & B <b>x</b>", "A &amp; B &lt;b&gt;x&lt;/b&gt;")]
    [InlineData(null, "")]
    public void A_text_a_person_typed_is_escaped(string? text, string expected) => Assert.Equal(expected, NotificationText.Esc(text));

    [Fact]
    public void Bold_escapes_what_is_inside_it() =>
        Assert.Equal("<b>a &lt;b&gt; &amp; c</b>", NotificationText.Bold("a <b> & c"));

    [Fact]
    public void Plain_text_is_what_a_channel_without_bold_shows()
    {
        var html = $"✅ {NotificationText.Bold("โพสต์สำเร็จ")}\n{NotificationText.Esc("A & <B>")}";
        Assert.Equal("✅ โพสต์สำเร็จ\nA & <B>", NotificationText.ToPlain(html));
    }

    [Fact]
    public void A_time_is_written_day_month_then_clock_in_the_given_calendar()
    {
        var at = new DateTimeOffset(2026, 10, 3, 9, 41, 35, TimeSpan.Zero);
        Assert.Equal("03/10 16:41:35", NotificationText.Time(at, 420));
        Assert.Equal("03/10 09:41:35", NotificationText.Time(at, 0));
    }

    // ---------- the picture ----------

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void A_data_url_or_bare_base64_of_a_jpeg_or_png_is_read()
    {
        Assert.Equal(Jpeg, ShotImage.Parse("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg)));
        Assert.Equal(Png, ShotImage.Parse("DATA:image/png;base64," + Convert.ToBase64String(Png)));
        Assert.Equal(Jpeg, ShotImage.Parse(Convert.ToBase64String(Jpeg)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data:image/jpeg;base64")] // no payload
    [InlineData("data:image/jpeg;base64,@@@not base64@@@")]
    public void Nothing_usable_is_nothing(string? text) => Assert.Null(ShotImage.Parse(text));

    [Fact]
    public void Something_that_is_not_a_picture_is_ignored()
    {
        Assert.Null(ShotImage.Parse("data:text/plain;base64," + Convert.ToBase64String("hello world"u8.ToArray())));
        Assert.Null(ShotImage.Parse(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6 })));
    }

    [Fact]
    public void A_picture_over_the_limit_is_ignored()
    {
        var big = new byte[ShotImage.MaxBytes + 1];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;
        Assert.Null(ShotImage.Parse("data:image/jpeg;base64," + Convert.ToBase64String(big)));

        var fits = new byte[ShotImage.MaxBytes];
        fits[0] = 0xFF; fits[1] = 0xD8; fits[2] = 0xFF;
        Assert.Equal(ShotImage.MaxBytes, ShotImage.Parse("data:image/jpeg;base64," + Convert.ToBase64String(fits))!.Length);
    }

    // ---------- the lines under a group's name ----------

    private static PostContext Context(string? set = "SIRI", string? schedule = "โพสต์ในกลุ่ม", int? round = 1, int position = 3, int total = 69,
        DateTimeOffset? next = null, bool roundEnded = false) =>
        new(set, schedule, round, position, total, next, roundEnded, 420);

    [Fact]
    public void Where_a_post_stands_reads_like_the_extensions_line()
    {
        Assert.Equal("ชุด SIRI · โพสต์ในกลุ่ม · รอบ 1 (กลุ่ม 3/69)", Context().WhereLine());
    }

    [Fact]
    public void Only_what_is_known_is_written()
    {
        Assert.Equal("ชุด SIRI", Context(schedule: null, round: null, position: 0, total: 0).WhereLine()); // a test post
        Assert.Equal("ชุด SIRI · ตาราง · รอบ 2", Context(schedule: "ตาราง", round: 2, position: 0, total: 0).WhereLine());
        Assert.Null(PostContext.None.WhereLine());
    }

    [Fact]
    public void The_next_line_says_whether_a_group_or_a_round_comes_next()
    {
        var at = new DateTimeOffset(2026, 10, 3, 9, 41, 35, TimeSpan.Zero);
        Assert.Equal("⏭ กลุ่มถัดไป: 03/10 16:41:35", Context(next: at).NextLine());
        Assert.Equal("⏭ รอบถัดไป: 03/10 16:41:35", Context(next: at, roundEnded: true).NextLine());
        Assert.Null(Context().NextLine());
    }

    [Fact]
    public void Names_in_the_line_are_escaped()
    {
        Assert.Equal("ชุด A &amp; B · &lt;x&gt;", Context(set: "A & B", schedule: "<x>", round: null, position: 0, total: 0).WhereLine());
    }
}
