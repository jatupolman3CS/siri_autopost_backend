using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Tests;

public class LineChatUrlTests
{
    [Theory]
    [InlineData("https://chat.line.biz/U12345678/chat/c123456789012345", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    [InlineData("http://chat.line.biz/U12345678/chat/c123456789012345", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    [InlineData("chat.line.biz/U12345678/chat/c123456789012345", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    [InlineData("https://www.chat.line.biz/U12345678/chat/c123456789012345", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    [InlineData("  https://chat.line.biz/U12345678/chat/c123456789012345/  ", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    [InlineData("https://chat.line.biz/U12345678/chat/c123456789012345?query=1#tab", "https://chat.line.biz/U12345678/chat/c123456789012345")]
    public void Valid_line_chat_urls_are_normalized(string raw, string expected)
    {
        Assert.Equal(expected, LineChatUrl.Normalize(raw));
        Assert.True(LineChatUrl.IsValid(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://chat.line.biz/")]
    [InlineData("https://chat.line.biz/bot123")]
    [InlineData("https://chat.line.biz/bot123/chat")]
    [InlineData("https://chat.line.biz/bot123/chat/short")]
    [InlineData("https://other.line.biz/bot123/chat/c123456789012345")]
    [InlineData("https://facebook.com/groups/baandee")]
    public void Invalid_line_chat_urls_are_rejected(string? raw)
    {
        Assert.Null(LineChatUrl.Normalize(raw));
        Assert.False(LineChatUrl.IsValid(raw));
    }

    [Fact]
    public void Parse_extracts_botId_and_userId()
    {
        var parsed = LineChatUrl.Parse("https://chat.line.biz/myBot_01/chat/user_1234567890");
        Assert.NotNull(parsed);
        Assert.Equal("myBot_01", parsed.Value.BotId);
        Assert.Equal("user_1234567890", parsed.Value.UserId);
        Assert.Equal("https://chat.line.biz/myBot_01/chat/user_1234567890", parsed.Value.Url);
    }

    [Fact]
    public void Build_constructs_canonical_url()
    {
        var url = LineChatUrl.Build("myBot_01", "user_1234567890");
        Assert.Equal("https://chat.line.biz/myBot_01/chat/user_1234567890", url);
    }

    [Fact]
    public void Comparison_is_case_insensitive()
    {
        var a = "https://chat.line.biz/MYBOT/chat/USER1234567890";
        var b = "https://chat.line.biz/mybot/chat/user1234567890";
        Assert.True(LineChatUrl.Same(a, b));
        Assert.Equal(0, LineChatUrl.Comparer.Compare(a, b));
    }
}
