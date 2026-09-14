using System.Text;

namespace DiffusionNexus.Installer.Core.Text;

/// <summary>How a run of text is emphasised. Flags, because emphasis nests.</summary>
[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>One run of text that shares a single style.</summary>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style);

/// <summary>A paragraph or a bullet list. Deliberately a closed set -- see <see cref="DescriptionMarkdown"/>.</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record MarkdownBullets(IReadOnlyList<IReadOnlyList<MarkdownSpan>> Items) : MarkdownBlock;

/// <summary>
/// Parses the Markdown subset the catalog's workload descriptions actually use into blocks a
/// component can render as real elements.
///
/// Scope was measured, not guessed: across all 25 workload.json files the descriptions use bold
/// (22 files), bullet lists (13), italic (4) and inline code (5), and nothing else -- no links,
/// headings, numbered lists, blockquotes, tables or raw HTML -- with the longest at 659
/// characters. A full CommonMark implementation (Markdig) would be a new dependency, a
/// third-party-notices regeneration and an HTML string the UI would have to trust; this returns a
/// tree the renderer turns into elements, so no markup the catalog carries can become markup the
/// browser executes.
///
/// Anything outside the subset degrades to the literal characters the author typed. That is the
/// contract: a description is content, and content must never disappear because the parser did not
/// recognise it.
/// </summary>
public static class DescriptionMarkdown
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        // Paragraph lines and bullet items both accumulate their raw text first and are parsed for
        // inline markup only when the block closes, so a run of emphasis is allowed to span the
        // soft line break the author's editor happened to wrap at.
        var paragraph = new StringBuilder();
        var items = new List<string>();

        void CloseParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MarkdownParagraph(ParseSpans(paragraph.ToString())));
            paragraph.Clear();
        }

        void CloseList()
        {
            if (items.Count == 0) return;
            blocks.Add(new MarkdownBullets(items.Select(ParseSpans).ToList()));
            items.Clear();
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');

            if (line.Length == 0)
            {
                CloseParagraph();
                CloseList();
                continue;
            }

            if (BulletContent(line) is { } bullet)
            {
                CloseParagraph();
                items.Add(bullet);
                continue;
            }

            if (items.Count > 0)
            {
                // Lazy continuation: a plain line under a bullet belongs to that bullet. The
                // alternative -- starting a paragraph -- would split one wrapped item in two.
                items[^1] = $"{items[^1]} {line}";
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }

        CloseParagraph();
        CloseList();
        return blocks;
    }

    /// <summary>
    /// The text of a bullet item, or null when the line is not one.
    ///
    /// The marker is a dash or a star followed by whitespace. The space is what makes it a marker:
    /// half the catalog's descriptions open with "**Name** -- ..." and reading that leading star
    /// as a bullet would turn most of them into one-item lists.
    /// </summary>
    private static string? BulletContent(string line)
    {
        if (line.Length < 2) return null;
        if (line[0] is not ('-' or '*')) return null;
        if (!char.IsWhiteSpace(line[1])) return null;
        return line[2..].TrimStart();
    }

    private static IReadOnlyList<MarkdownSpan> ParseSpans(string text)
    {
        var spans = new List<MarkdownSpan>();
        var buffer = new StringBuilder();
        var style = MarkdownStyle.None;
        var i = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new MarkdownSpan(buffer.ToString(), style));
            buffer.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close < 0)
                {
                    // No partner: a backtick in prose is a backtick.
                    buffer.Append(c);
                    i++;
                    continue;
                }

                // Code wins over emphasis: stars between the backticks are characters, not markup.
                Flush();
                spans.Add(new MarkdownSpan(text[(i + 1)..close], style | MarkdownStyle.Code));
                i = close + 1;
                continue;
            }

            if (c == '*')
            {
                var doubled = i + 1 < text.Length && text[i + 1] == '*';
                var width = doubled ? 2 : 1;
                var flag = doubled ? MarkdownStyle.Bold : MarkdownStyle.Italic;

                if (style.HasFlag(flag) && ClosesHere(text, i))
                {
                    Flush();
                    style &= ~flag;
                    i += width;
                    continue;
                }

                if (OpensHere(text, i + width) && FindCloser(text, i + width, doubled) >= 0)
                {
                    Flush();
                    style |= flag;
                    i += width;
                    continue;
                }

                // An opener with no closer is prose. Emit the whole run, so the second star of an
                // unmatched "**" is not re-read as an italic opener on the next iteration.
                buffer.Append(text, i, width);
                i += width;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// Whether a delimiter run ending just before <paramref name="at"/> could open emphasis: the
    /// character after it must exist and must not be whitespace. This is what keeps the star in
    /// "roughly 2 * 3 GB" a multiplication sign rather than the start of an italic run that swallows
    /// the rest of the sentence.
    /// </summary>
    private static bool OpensHere(string text, int at) => at < text.Length && !char.IsWhiteSpace(text[at]);

    /// <summary>The mirror of <see cref="OpensHere"/>: a closer needs non-whitespace before it.</summary>
    private static bool ClosesHere(string text, int at) => at > 0 && !char.IsWhiteSpace(text[at - 1]);

    /// <summary>
    /// Index of the delimiter that would close a run opened at <paramref name="from"/>, or -1.
    ///
    /// A single star must not pair with either star of a "**" pair: a plain IndexOf("*") finds the
    /// first character of the bold opener in "2 * 3 and **bold**", opens an italic run there, and
    /// the bold delimiters end up rendered as text.
    /// </summary>
    private static int FindCloser(string text, int from, bool doubled)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] != '*') continue;
            if (!ClosesHere(text, i)) continue;

            if (doubled)
            {
                if (i + 1 < text.Length && text[i + 1] == '*') return i;
                continue;
            }

            var partOfPair = (i + 1 < text.Length && text[i + 1] == '*')
                          || (i - 1 >= from && text[i - 1] == '*');
            if (!partOfPair) return i;
        }

        return -1;
    }
}
