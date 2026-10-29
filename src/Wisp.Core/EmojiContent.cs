using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisp.Core;

/// <summary>Recognises complete emoji graphemes without confusing ordinary digits or unknown :tokens: with emoji.</summary>
public static class EmojiContent
{
    private static readonly Regex CustomToken = new(@":[\p{L}\p{N}_-]{1,40}:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const double InlineSize = 22;
    public const double StandaloneSize = 48;

    public static bool IsEmojiOnly(string? text, IEnumerable<string>? knownCustomTokens = null)
    {
        bool any = false;
        foreach (var part in Split(text ?? "", knownCustomTokens))
        {
            if (string.IsNullOrWhiteSpace(part.Value)) continue;
            if (!part.IsEmoji) return false;
            any = true;
        }
        return any;
    }

    public static double EmojiSize(string? text, IEnumerable<string>? knownCustomTokens = null)
        => IsEmojiOnly(text, knownCustomTokens) ? StandaloneSize : InlineSize;

    public static IReadOnlyList<EmojiSegment> Split(string text, IEnumerable<string>? knownCustomTokens = null)
    {
        var known = knownCustomTokens?.ToHashSet(StringComparer.Ordinal);
        var result = new List<EmojiSegment>();
        int offset = 0;
        foreach (Match match in CustomToken.Matches(text))
        {
            if (known?.Contains(match.Value) != true) continue;
            AddUnicode(text[offset..match.Index]);
            result.Add(new EmojiSegment(match.Value, true, true));
            offset = match.Index + match.Length;
        }
        AddUnicode(text[offset..]);
        return result;

        void AddUnicode(string value)
        {
            var elements = StringInfo.GetTextElementEnumerator(value);
            while (elements.MoveNext())
            {
                string grapheme = elements.GetTextElement();
                bool emoji = IsEmojiGrapheme(grapheme);
                if (result.Count > 0 && !result[^1].IsCustom && result[^1].IsEmoji == emoji)
                    result[^1] = result[^1] with { Value = result[^1].Value + grapheme };
                else result.Add(new EmojiSegment(grapheme, emoji));
            }
        }
    }

    private static bool IsEmojiGrapheme(string grapheme)
    {
        var runes = grapheme.EnumerateRunes().ToArray();
        if (runes.Length == 0) return false;
        // Keycaps need the enclosing mark; the digit itself is ordinary text.
        if (runes[0].Value is >= '0' and <= '9' or '#' or '*')
            return runes[^1].Value == 0x20E3 && runes.Skip(1).All(r => r.Value is 0xFE0F or 0x20E3);
        bool hasBase = false;
        foreach (Rune rune in runes)
        {
            int value = rune.Value;
            if (value is 0x200D or 0xFE0E or 0xFE0F or >= 0x1F3FB and <= 0x1F3FF or >= 0xE0020 and <= 0xE007F) continue;
            if (!IsEmojiBase(value)) return false;
            hasBase = true;
        }
        return hasBase;
    }

    private static bool IsEmojiBase(int value) => value is
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
        >= 0x2194 and <= 0x2199 or 0x21A9 or 0x21AA or 0x231A or 0x231B or 0x2328 or 0x23CF or
        >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA or 0x24C2 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or
        >= 0x25FB and <= 0x25FE or >= 0x2600 and <= 0x27BF or 0x2934 or 0x2935 or
        >= 0x2B05 and <= 0x2B07 or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 or 0x3030 or 0x303D or 0x3297 or 0x3299 or
        >= 0x1F004 and <= 0x1F0CF or >= 0x1F170 and <= 0x1F251 or >= 0x1F300 and <= 0x1FAFF;
}

public readonly record struct EmojiSegment(string Value, bool IsEmoji, bool IsCustom = false);
