using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class EmojiContentTests
{
    [Theory]
    [InlineData("😀", true)]
    [InlineData(" \n😀 \r\n 🎉 ", true)]
    [InlineData("👨‍👩‍👧‍👦 👍🏽 🇰🇿 🇰🇷", true)]
    [InlineData("1️⃣ #️⃣ *️⃣", true)]
    [InlineData("❤️ ☺️", true)]
    [InlineData("текст 😀", false)]
    [InlineData("😀x", false)]
    [InlineData("1 2", false)]
    [InlineData("# *", false)]
    [InlineData(":unknown:", false)]
    [InlineData("", false)]
    [InlineData(" \n\r\t", false)]
    [InlineData("\u200d", false)]
    [InlineData("\ufe0f", false)]
    public void OnlyCompleteEmojiContentUsesLargeMode(string text, bool expected)
    {
        Assert.Equal(expected, EmojiContent.IsEmojiOnly(text));
        Assert.Equal(expected ? 48d : 22d, EmojiContent.EmojiSize(text));
    }

    [Fact]
    public void KnownCustomEmojiCanMixWithUnicodeAndWhitespace()
    {
        string[] tokens = [":wisp:", ":огонь:"];
        Assert.True(EmojiContent.IsEmojiOnly(" :wisp: \n 😀 :огонь: ", tokens));
        Assert.False(EmojiContent.IsEmojiOnly(":wisp: :unknown:", tokens));
        Assert.False(EmojiContent.IsEmojiOnly("привет :wisp:", tokens));
        var parts = EmojiContent.Split("A👍🏽 :wisp:\n🇰🇿", tokens);
        Assert.Equal("A👍🏽 :wisp:\n🇰🇿", string.Concat(parts.Select(p => p.Value)));
        Assert.Equal(":wisp:", Assert.Single(parts,p => p.IsCustom).Value);
        Assert.Contains(parts,p => p.Value == "👍🏽" && p.IsEmoji);
        Assert.Contains(parts,p => p.Value == "🇰🇿" && p.IsEmoji);
    }
}
