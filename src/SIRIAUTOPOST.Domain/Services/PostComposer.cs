using System.Text.RegularExpressions;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Services;

public enum ComposeMode
{
    Group,
    Chat,
}

/// <summary>
/// Writes the text a group or chat really gets, the way the design's preview does: spintax resolved, the group's code,
/// customer name, and for groups the collection's footer and hashtags. Pure: randomness comes in as a function returning [0, 1).
/// </summary>
public static partial class PostComposer
{
    private const int MaxSpinRounds = 50;
    private const int MaxLengthAttempts = 30;
    public const int MaxBubbleLength = 5000;
    public const int MaxBubbles = 5;

    [GeneratedRegex(@"\{\{\s*(?:code|รหัส)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeTag();

    [GeneratedRegex(@"\{\{\s*(?:name|ชื่อ)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameTag();

    // The innermost {a|b|c}: no braces inside.
    [GeneratedRegex(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex SpinGroup();

    // A line that holds nothing but the code tag.
    [GeneratedRegex(@"^[ \t]*\{\{\s*(?:code|รหัส)\s*\}\}[ \t]*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CodeTagLine();

    // A line that holds nothing but the name tag.
    [GeneratedRegex(@"^[ \t]*\{\{\s*(?:name|ชื่อ)\s*\}\}[ \t]*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex NameTagLine();

    public static bool HasCodeTag(string? text) => CodeTag().IsMatch(text ?? "");

    public static bool HasNameTag(string? text) => NameTag().IsMatch(text ?? "");

    public static bool HasSpin(string? text) => SpinGroup().IsMatch(text ?? "");

    /// <summary>
    /// Resolves every {a|b|c}, innermost first, picking an option uniformly.
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

    /// <summary>
    /// In group mode: the group's code replaces every {{code}} tag; without a tag it becomes the first line.
    /// In chat mode: {{name}}/{{ชื่อ}} -> customer name, {{code}}/{{รหัส}} -> code; no fallback to first line.
    /// </summary>
    public static string Compose(string? text, string? code, ComposeMode mode = ComposeMode.Group, string? name = null)
    {
        var tx = text ?? "";
        var c = (code ?? "").Trim();
        var n = (name ?? "").Trim();

        if (mode == ComposeMode.Chat)
        {
            if (NameTag().IsMatch(tx))
            {
                if (n.Length == 0) tx = NameTagLine().Replace(tx, "");
                tx = NameTag().Replace(tx, _ => n);
            }
            if (CodeTag().IsMatch(tx))
            {
                if (c.Length == 0) tx = CodeTagLine().Replace(tx, "");
                tx = CodeTag().Replace(tx, _ => c);
            }
            return tx;
        }

        if (CodeTag().IsMatch(tx))
        {
            if (c.Length == 0) tx = CodeTagLine().Replace(tx, "");
            return CodeTag().Replace(tx, _ => c);
        }
        if (c.Length == 0) return tx;
        return tx.Length > 0 ? c + "\n" + tx : c;
    }

    /// <summary>
    /// Spin, then code/name replacement, then for groups the footer and hashtags.
    /// </summary>
    public static string ComposeFull(
        string? text, string? code, CollectionSettings settings, Func<double> rnd,
        ComposeMode mode = ComposeMode.Group, string? name = null)
    {
        DomainException? shortest = null;
        var shortestLength = int.MaxValue;
        for (var attempt = 0; attempt < MaxLengthAttempts; attempt++)
        {
            try
            {
                return ComposeOnce(text, code, settings, rnd, mode, name);
            }
            catch (TooLongException e)
            {
                if (e.Length < shortestLength)
                {
                    shortestLength = e.Length;
                    shortest = e;
                }
                // Nothing to vary: every pick is the same text.
                if (!HasSpin(text) && (mode == ComposeMode.Chat || !HasSpin(settings.Footer))) break;
            }
        }
        throw new DomainException(shortest!.Message);
    }

    public static string[] SplitBubbles(string? text, bool split)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return [];

        List<string> bubbles;
        if (!split)
        {
            bubbles = SplitChunk(raw, MaxBubbleLength);
        }
        else
        {
            var parts = Regex.Split(raw, @"\r?\n\s*\r?\n")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (parts.Count == 0) return [];

            bubbles = new List<string>();
            foreach (var p in parts)
            {
                bubbles.AddRange(SplitChunk(p, MaxBubbleLength));
            }

            while (bubbles.Count > MaxBubbles)
            {
                var bestIdx = -1;
                var minCombined = int.MaxValue;
                for (var i = 0; i < bubbles.Count - 1; i++)
                {
                    var combined = bubbles[i].Length + bubbles[i + 1].Length;
                    if (combined < minCombined)
                    {
                        minCombined = combined;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) break;
                bubbles[bestIdx] = bubbles[bestIdx] + "\n\n" + bubbles[bestIdx + 1];
                bubbles.RemoveAt(bestIdx + 1);
            }

            var finalBubbles = new List<string>();
            foreach (var b in bubbles)
            {
                finalBubbles.AddRange(SplitChunk(b, MaxBubbleLength));
            }
            bubbles = finalBubbles;
        }

        if (bubbles.Count > MaxBubbles)
            throw new DomainException("ข้อความแชทแบ่งได้ไม่เกิน 5 ฟอง");

        return bubbles.ToArray();
    }

    private static List<string> SplitChunk(string s, int maxLen = MaxBubbleLength)
    {
        var list = new List<string>();
        var remaining = s.Trim();
        while (remaining.Length > maxLen)
        {
            var window = remaining[..maxLen];
            var splitIdx = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            var skipLen = 2;
            if (splitIdx < 0)
            {
                splitIdx = window.LastIndexOf('\n');
                skipLen = 1;
            }
            if (splitIdx < 0)
            {
                splitIdx = -1;
                for (var j = window.Length - 1; j >= 0; j--)
                {
                    if (char.IsWhiteSpace(window[j]))
                    {
                        splitIdx = j;
                        skipLen = 1;
                        break;
                    }
                }
            }
            if (splitIdx <= 0)
            {
                splitIdx = maxLen;
                skipLen = 0;
            }
            var piece = remaining[..splitIdx].Trim();
            if (piece.Length > 0) list.Add(piece);
            remaining = remaining[(splitIdx + skipLen)..].Trim();
        }
        if (remaining.Length > 0) list.Add(remaining);
        return list;
    }

    private sealed class TooLongException(string message, int length) : DomainException(message)
    {
        public int Length { get; } = length;
    }

    private static string ComposeOnce(
        string? text, string? code, CollectionSettings settings, Func<double> rnd,
        ComposeMode mode, string? name)
    {
        if (mode == ComposeMode.Chat)
        {
            var body = Compose(Spin(text, rnd), code, ComposeMode.Chat, name);
            try
            {
                SplitBubbles(body, false);
            }
            catch (DomainException e)
            {
                throw new TooLongException(e.Message, body.Length);
            }
            return body;
        }

        var groupBody = Compose(Spin(text, rnd), code, ComposeMode.Group);
        var withoutExtras = groupBody.Length;
        var foot = Spin(settings.Footer, rnd).Trim();
        var footLength = 0;
        if (foot.Length > 0 && !groupBody.Contains(foot, StringComparison.Ordinal))
        {
            footLength = foot.Length;
            groupBody = settings.FooterPos == FooterPosition.Top ? foot + "\n" + groupBody : groupBody + "\n\n" + foot;
        }
        var tags = (settings.Hashtags ?? "").Trim();
        var tagsLength = 0;
        if (tags.Length > 0 && !groupBody.Contains(tags, StringComparison.Ordinal))
        {
            tagsLength = tags.Length;
            groupBody += "\n" + tags;
        }
        if (groupBody.Length > Post.MaxComposedLength)
        {
            var (part, length) = new[] { ("ข้อความโพสต์", withoutExtras), ("ข้อความส่วนท้าย", footLength), ("แฮชแท็ก", tagsLength) }
                .MaxBy(p => p.Item2);
            throw new TooLongException(
                $"ข้อความที่ประกอบแล้วยาว {groupBody.Length} ตัวอักษร เกิน {Post.MaxComposedLength} ตัวอักษร: ลดความยาวของ{part} (ตอนนี้ {length} ตัวอักษร)",
                groupBody.Length);
        }
        return groupBody;
    }
}
