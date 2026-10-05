using System.Text.RegularExpressions;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>
/// Writes the text a group really gets, the way the design's preview does: spintax resolved, the group's code,
/// the collection's footer and hashtags. Pure: randomness comes in as a function returning [0, 1).
/// </summary>
public static partial class PostComposer
{
    private const int MaxSpinRounds = 50;
    private const int MaxLengthAttempts = 30;

    [GeneratedRegex(@"\{\{\s*(?:code|รหัส)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeTag();

    // The innermost {a|b|c}: no braces inside.
    [GeneratedRegex(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex SpinGroup();

    public static bool HasCodeTag(string? text) => CodeTag().IsMatch(text ?? "");

    public static bool HasSpin(string? text) => SpinGroup().IsMatch(text ?? "");

    /// <summary>
    /// Resolves every {a|b|c}, innermost first, picking an option uniformly. Each round settles all the groups that have
    /// no braces inside at once, so the rounds only count how deep the nesting goes (a post with hundreds of groups is
    /// resolved completely, as the preview in the web app does).
    /// </summary>
    public static string Spin(string? text, Func<double> rnd)
    {
        var s = text ?? "";
        var regex = SpinGroup();
        for (var round = 0; round < MaxSpinRounds && regex.IsMatch(s); round++)
        {
            s = regex.Replace(s, m =>
            {
                var options = m.Groups[1].Value.Split('|');
                var index = (int)Math.Floor(rnd() * options.Length);
                return options[Math.Clamp(index, 0, options.Length - 1)];
            });
        }
        return s;
    }

    /// <summary>The group's code replaces every {{code}} tag; without a tag it becomes the first line.</summary>
    public static string Compose(string? text, string? code)
    {
        var tx = text ?? "";
        var c = (code ?? "").Trim();
        if (CodeTag().IsMatch(tx)) return CodeTag().Replace(tx, _ => c);
        if (c.Length == 0) return tx;
        return tx.Length > 0 ? c + "\n" + tx : c;
    }

    /// <summary>
    /// Spin, then the code, then the footer (unless the body already has it) at the end or the top, then the hashtags
    /// (unless the body already has them). A result over the post length limit is refused, never cut; but spintax can
    /// make one pick longer than another, so a pick over the limit is spun again (up to <see cref="MaxLengthAttempts"/>
    /// times) before the shortest one is refused.
    /// </summary>
    public static string ComposeFull(string? text, string? code, CollectionSettings settings, Func<double> rnd)
    {
        DomainException? shortest = null;
        var shortestLength = int.MaxValue;
        for (var attempt = 0; attempt < MaxLengthAttempts; attempt++)
        {
            try
            {
                return ComposeOnce(text, code, settings, rnd);
            }
            catch (TooLongException e)
            {
                if (e.Length < shortestLength)
                {
                    shortestLength = e.Length;
                    shortest = e;
                }
                // Nothing to vary: every pick is the same text.
                if (!HasSpin(text) && !HasSpin(settings.Footer)) break;
            }
        }
        throw new DomainException(shortest!.Message);
    }

    private sealed class TooLongException(string message, int length) : DomainException(message)
    {
        public int Length { get; } = length;
    }

    private static string ComposeOnce(string? text, string? code, CollectionSettings settings, Func<double> rnd)
    {
        var body = Compose(Spin(text, rnd), code);
        var withoutExtras = body.Length;
        var foot = Spin(settings.Footer, rnd).Trim();
        var footLength = 0;
        if (foot.Length > 0 && !body.Contains(foot, StringComparison.Ordinal))
        {
            footLength = foot.Length;
            body = settings.FooterPos == FooterPosition.Top ? foot + "\n" + body : body + "\n\n" + foot;
        }
        var tags = (settings.Hashtags ?? "").Trim();
        var tagsLength = 0;
        if (tags.Length > 0 && !body.Contains(tags, StringComparison.Ordinal))
        {
            tagsLength = tags.Length;
            body += "\n" + tags;
        }
        if (body.Length > Post.MaxComposedLength)
        {
            var (part, length) = new[] { ("ข้อความโพสต์", withoutExtras), ("ข้อความส่วนท้าย", footLength), ("แฮชแท็ก", tagsLength) }
                .MaxBy(p => p.Item2);
            throw new TooLongException(
                $"ข้อความที่ประกอบแล้วยาว {body.Length} ตัวอักษร เกิน {Post.MaxComposedLength} ตัวอักษร: ลดความยาวของ{part} (ตอนนี้ {length} ตัวอักษร)",
                body.Length);
        }
        return body;
    }
}
