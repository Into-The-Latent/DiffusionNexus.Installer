using DiffusionNexus.Installer.Core.Text;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Text;

/// <summary>
/// The catalog authors every workload description in Markdown. The subset it actually uses was
/// counted across all 25 workload.json files before this parser was written: bold (22 files),
/// bullet lists (13), italic (4) and inline code (5). No links, headings, numbered lists,
/// blockquotes, tables or raw HTML appear anywhere, and the longest description is 659 characters.
/// That inventory is the scope -- everything outside it must degrade to plain text, never throw.
/// </summary>
public class DescriptionMarkdownTests
{
    private static string Text(MarkdownBlock block) => block switch
    {
        MarkdownParagraph p => string.Concat(p.Spans.Select(s => s.Text)),
        MarkdownBullets b => string.Join(" | ", b.Items.Select(i => string.Concat(i.Select(s => s.Text)))),
        _ => throw new InvalidOperationException($"Unhandled block {block.GetType().Name}")
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Nothing_to_say_is_no_blocks_rather_than_an_empty_one(string? markdown)
    {
        DescriptionMarkdown.Parse(markdown).Should().BeEmpty();
    }

    [Fact]
    public void A_blank_line_separates_paragraphs_and_wrapped_lines_rejoin()
    {
        var blocks = DescriptionMarkdown.Parse("First one\nwrapped across lines.\n\nSecond one.");

        blocks.Should().HaveCount(2);
        Text(blocks[0]).Should().Be("First one wrapped across lines.");
        Text(blocks[1]).Should().Be("Second one.");
    }

    [Fact]
    public void Consecutive_dash_lines_are_one_list_not_one_list_each()
    {
        var blocks = DescriptionMarkdown.Parse("Intro.\n\n- first\n- second\n- third");

        blocks.Should().HaveCount(2);
        blocks[1].Should().BeOfType<MarkdownBullets>();
        Text(blocks[1]).Should().Be("first | second | third");
    }

    [Fact]
    public void A_paragraph_after_a_list_starts_a_new_block()
    {
        var blocks = DescriptionMarkdown.Parse("- only item\n\nAfterwards.");

        blocks.Should().HaveCount(2);
        blocks[0].Should().BeOfType<MarkdownBullets>();
        blocks[1].Should().BeOfType<MarkdownParagraph>();
    }

    [Fact]
    public void Bold_italic_and_code_become_styled_spans_without_their_delimiters()
    {
        var spans = DescriptionMarkdown.Parse("**Fooocus** is *fast* and reads `config.json`.")
            .OfType<MarkdownParagraph>().Single().Spans;

        spans.Should().BeEquivalentTo(new[]
        {
            new MarkdownSpan("Fooocus", MarkdownStyle.Bold),
            new MarkdownSpan(" is ", MarkdownStyle.None),
            new MarkdownSpan("fast", MarkdownStyle.Italic),
            new MarkdownSpan(" and reads ", MarkdownStyle.None),
            new MarkdownSpan("config.json", MarkdownStyle.Code),
            new MarkdownSpan(".", MarkdownStyle.None),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Emphasis_nests()
    {
        var spans = DescriptionMarkdown.Parse("**bold with *both* inside**")
            .OfType<MarkdownParagraph>().Single().Spans;

        spans.Should().BeEquivalentTo(new[]
        {
            new MarkdownSpan("bold with ", MarkdownStyle.Bold),
            new MarkdownSpan("both", MarkdownStyle.Bold | MarkdownStyle.Italic),
            new MarkdownSpan(" inside", MarkdownStyle.Bold),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Inline_markup_works_inside_bullets_too()
    {
        var items = DescriptionMarkdown.Parse("- **Triton** compiles kernels").OfType<MarkdownBullets>().Single().Items;

        items.Single().Should().BeEquivalentTo(new[]
        {
            new MarkdownSpan("Triton", MarkdownStyle.Bold),
            new MarkdownSpan(" compiles kernels", MarkdownStyle.None),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void A_star_with_no_partner_stays_a_star()
    {
        // "2 * 3" and a trailing footnote star are prose, not emphasis. Swallowing the character
        // would silently delete content the catalog author typed on purpose.
        var spans = DescriptionMarkdown.Parse("roughly 2 * 3 GB*").OfType<MarkdownParagraph>().Single().Spans;

        string.Concat(spans.Select(s => s.Text)).Should().Be("roughly 2 * 3 GB*");
        spans.Should().OnlyContain(s => s.Style == MarkdownStyle.None);
    }

    [Fact]
    public void A_lone_star_does_not_pair_with_the_opener_of_a_bold_run()
    {
        // The naive "is there another star later?" test finds the first star of "**" and opens an
        // italic run that then swallows the bold delimiters as ordinary text.
        var spans = DescriptionMarkdown.Parse("2 * 3 and **bold**").OfType<MarkdownParagraph>().Single().Spans;

        spans.Should().BeEquivalentTo(new[]
        {
            new MarkdownSpan("2 * 3 and ", MarkdownStyle.None),
            new MarkdownSpan("bold", MarkdownStyle.Bold),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void An_unclosed_backtick_stays_a_backtick()
    {
        var spans = DescriptionMarkdown.Parse("press ` to open").OfType<MarkdownParagraph>().Single().Spans;

        string.Concat(spans.Select(s => s.Text)).Should().Be("press ` to open");
        spans.Should().OnlyContain(s => s.Style == MarkdownStyle.None);
    }

    [Fact]
    public void Code_spans_are_literal_inside()
    {
        // Backticks win over emphasis in Markdown: the stars inside are characters, not markup.
        var spans = DescriptionMarkdown.Parse("run `a**b`").OfType<MarkdownParagraph>().Single().Spans;

        spans.Should().BeEquivalentTo(new[]
        {
            new MarkdownSpan("run ", MarkdownStyle.None),
            new MarkdownSpan("a**b", MarkdownStyle.Code),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void A_bold_run_at_the_start_of_a_line_is_not_mistaken_for_a_bullet()
    {
        // Every second description opens with "**Name** -- ...", and a bullet marker is a star
        // followed by a SPACE. Getting this wrong turns most descriptions into one-item lists.
        var blocks = DescriptionMarkdown.Parse("**Fooocus** (lllyasviel)");

        blocks.Single().Should().BeOfType<MarkdownParagraph>();
    }

    [Fact]
    public void Star_bullets_are_read_as_bullets()
    {
        var blocks = DescriptionMarkdown.Parse("* first\n* second");

        Text(blocks.Single()).Should().Be("first | second");
    }

    [Fact]
    public void Carriage_returns_do_not_leak_into_the_text()
    {
        // The catalog is edited on Windows and the JSON carries whatever the author's editor wrote.
        var blocks = DescriptionMarkdown.Parse("One.\r\n\r\n- item\r\n");

        Text(blocks[0]).Should().Be("One.");
        Text(blocks[1]).Should().Be("item");
    }

    [Fact]
    public void An_unindented_line_under_a_bullet_continues_that_bullet()
    {
        var blocks = DescriptionMarkdown.Parse("- a bullet that\n  wrapped");

        Text(blocks.Single()).Should().Be("a bullet that wrapped");
    }

    [Fact]
    public void Markup_the_catalog_never_uses_survives_as_plain_text()
    {
        // Headings, links and tables are outside the supported subset. The contract is that they
        // read as the characters the author typed -- not that they throw or vanish.
        var blocks = DescriptionMarkdown.Parse("# Heading\n\n[label](https://example.com)");

        Text(blocks[0]).Should().Be("# Heading");
        Text(blocks[1]).Should().Be("[label](https://example.com)");
    }
}
