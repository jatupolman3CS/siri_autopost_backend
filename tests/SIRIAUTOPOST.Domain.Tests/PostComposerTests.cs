using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Tests;

public class PostComposerTests
{
    private static Func<double> Always(double v) => () => v;

    private static Func<double> Sequence(params double[] values)
    {
        var i = 0;
        return () => values[i++ % values.Length];
    }

    [Theory]
    [InlineData("สวัสดี {{code}}", true)]
    [InlineData("{{ CODE }} ก่อน", true)]
    [InlineData("{{รหัส}}", true)]
    [InlineData("{{  รหัส  }}", true)]
    [InlineData("{code}", false)]
    [InlineData("{{codes}}", false)]
    [InlineData("ไม่มีแท็ก", false)]
    [InlineData("", false)]
    public void Detects_the_code_tag(string text, bool expected) => Assert.Equal(expected, PostComposer.HasCodeTag(text));

    [Fact]
    public void Detects_spintax_but_not_the_code_tag()
    {
        Assert.True(PostComposer.HasSpin("{a|b}"));
        Assert.False(PostComposer.HasSpin("{{code}}"));
        Assert.False(PostComposer.HasSpin("{ab}"));
    }

    [Fact]
    public void Spin_picks_an_option_by_the_random_number()
    {
        Assert.Equal("a", PostComposer.Spin("{a|b|c}", Always(0.0)));
        Assert.Equal("b", PostComposer.Spin("{a|b|c}", Always(0.5)));
        Assert.Equal("c", PostComposer.Spin("{a|b|c}", Always(0.99)));
        Assert.Equal("c", PostComposer.Spin("{a|b|c}", Always(1.0))); // never past the last option
    }

    [Fact]
    public void Spin_resolves_every_group_and_nested_ones_innermost_first()
    {
        Assert.Equal("สวัสดี ครับ", PostComposer.Spin("{สวัสดี|หวัดดี} {ครับ|ค่ะ}", Always(0.0)));
        // {x|{y|z}}: the inner group is resolved first, then the outer one picks between "x" and the result.
        Assert.Equal("y", PostComposer.Spin("{x|{y|z}}", Sequence(0.0, 0.9)));
        Assert.Equal("z", PostComposer.Spin("{x|{y|z}}", Sequence(0.9, 0.9)));
    }

    [Fact]
    public void Spin_resolves_a_post_with_hundreds_of_groups_completely()
    {
        var text = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"{{a{i}|b{i}}}"));
        var spun = PostComposer.Spin(text, Always(0.0));
        Assert.DoesNotContain("{", spun);
        Assert.DoesNotContain("|", spun);
        Assert.StartsWith("a0 a1 a2", spun);
        Assert.EndsWith("a299", spun);
    }

    [Fact]
    public void Spin_leaves_the_code_tag_and_plain_braces_alone()
    {
        Assert.Equal("{{code}} {x} a", PostComposer.Spin("{{code}} {x} {a|b}", Always(0.0)));
        Assert.Equal("", PostComposer.Spin(null, Always(0.0)));
    }

    [Fact]
    public void Spin_stops_after_50_rounds()
    {
        var deep = string.Concat(Enumerable.Repeat("{a|", 60)) + "z" + string.Concat(Enumerable.Repeat("}", 60));
        // Must terminate (the design's guard); the exact leftover is not the point.
        Assert.NotNull(PostComposer.Spin(deep, Always(0.0)));
    }

    [Fact]
    public void The_code_replaces_every_tag()
    {
        Assert.Equal("ใช้ AB12 หรือ AB12", PostComposer.Compose("ใช้ {{code}} หรือ {{รหัส}}", " AB12 "));
    }

    [Fact]
    public void Without_a_tag_the_code_becomes_the_first_line()
    {
        Assert.Equal("AB12\nโปรวันนี้", PostComposer.Compose("โปรวันนี้", "AB12"));
        Assert.Equal("AB12", PostComposer.Compose("", "AB12"));
        Assert.Equal("โปรวันนี้", PostComposer.Compose("โปรวันนี้", ""));
        Assert.Equal("โปรวันนี้", PostComposer.Compose("โปรวันนี้", "   "));
    }

    [Fact]
    public void A_tag_without_a_code_is_removed()
    {
        Assert.Equal("ใช้  วันนี้", PostComposer.Compose("ใช้ {{code}} วันนี้", ""));
    }

    private static CollectionSettings Settings(string footer = "", string hashtags = "", FooterPosition pos = FooterPosition.End) =>
        new() { Footer = footer, Hashtags = hashtags, FooterPos = pos };

    [Fact]
    public void The_full_text_is_spin_then_code_then_footer_then_hashtags()
    {
        var text = PostComposer.ComposeFull("{ขาย|โปร} ของดี", "AB12", Settings("ทักแชท", "#บ้าน #สวน"), Always(0.0));

        Assert.Equal("AB12\nขาย ของดี\n\nทักแชท\n#บ้าน #สวน", text);
    }

    [Fact]
    public void The_footer_can_go_on_top()
    {
        var text = PostComposer.ComposeFull("เนื้อหา", "", Settings("ทักแชท", pos: FooterPosition.Top), Always(0.0));
        Assert.Equal("ทักแชท\nเนื้อหา", text);
    }

    [Fact]
    public void The_footer_is_spun_too()
    {
        var text = PostComposer.ComposeFull("เนื้อหา", "", Settings("{ทักแชท|โทรเลย}"), Always(0.9));
        Assert.Equal("เนื้อหา\n\nโทรเลย", text);
    }

    [Fact]
    public void A_footer_or_hashtags_the_text_already_has_are_not_added_again()
    {
        var text = PostComposer.ComposeFull("ขายของ ทักแชท #บ้าน", "", Settings("ทักแชท", "#บ้าน"), Always(0.0));
        Assert.Equal("ขายของ ทักแชท #บ้าน", text);
    }

    [Fact]
    public void Blank_settings_add_nothing()
    {
        Assert.Equal("เนื้อหา", PostComposer.ComposeFull("เนื้อหา", "", Settings("   ", "  "), Always(0.0)));
    }

    [Fact]
    public void A_text_that_gets_too_long_is_refused_and_says_what_to_shorten()
    {
        var long1 = new string('ก', 6990);
        var ex = Assert.Throws<DomainException>(() => PostComposer.ComposeFull(long1, "", Settings("ส่วนท้ายยาวมาก"), Always(0.0)));
        Assert.Contains("7000", ex.Message);
        Assert.Contains("ข้อความโพสต์", ex.Message);

        var tags = new string('#', 400);
        var ex2 = Assert.Throws<DomainException>(() => PostComposer.ComposeFull(new string('ก', 6700), "", Settings(hashtags: tags), Always(0.0)));
        Assert.Contains("ข้อความโพสต์", ex2.Message); // the post itself is the longest part

        // Footer and hashtags together can push a short post over the limit as well.
        var heavy = Settings(footer: new string('ท', 1000), hashtags: new string('#', 500));
        Assert.Equal(1 + 2 + 1000 + 1 + 500, PostComposer.ComposeFull("ส", "", heavy, Always(0.0)).Length);
    }

    [Fact]
    public void A_spin_pick_that_is_too_long_is_spun_again()
    {
        var text = "{" + new string('ก', 7010) + "|สั้น}";
        // First pick: the long option (0.0); second pick: the short one (0.9).
        Assert.Equal("สั้น", PostComposer.ComposeFull(text, "", Settings(), Sequence(0.0, 0.9)));

        // Every option is too long: still refused, with the shortest pick's numbers.
        var allLong = "{" + new string('ก', 7010) + "|" + new string('ข', 7020) + "}";
        var ex = Assert.Throws<DomainException>(() => PostComposer.ComposeFull(allLong, "", Settings(), Sequence(0.0, 0.9)));
        Assert.Contains("7010", ex.Message);
    }

    [Fact]
    public void A_full_size_post_with_a_code_footer_and_hashtags_fits()
    {
        // The case that failed in production: a 5000-character post plus the group code went over 5000.
        var tags = new string('#', 500);
        var text = PostComposer.ComposeFull(new string('ก', 5000), "GROUPCODE123456", Settings(new string('ท', 1000), tags), Always(0.0));
        Assert.True(text.Length > 5000 && text.Length <= Post.MaxComposedLength);
    }

    [Fact]
    public void A_text_exactly_at_the_limit_is_fine()
    {
        var text = PostComposer.ComposeFull(new string('ก', Post.MaxComposedLength), "", Settings(), Always(0.0));
        Assert.Equal(Post.MaxComposedLength, text.Length);
    }
}
