using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class MarkdownTextTests : BunitContext
{
    [Fact]
    public void Paragraphs_and_bullets_become_real_elements()
    {
        var cut = Render<MarkdownText>(p => p.Add(m => m.Text, "Intro line.\n\n- first\n- second"));

        cut.Find("p").TextContent.Should().Be("Intro line.");
        cut.FindAll("ul li").Select(li => li.TextContent).Should().Equal("first", "second");
    }

    [Fact]
    public void Emphasis_becomes_strong_em_and_code()
    {
        var cut = Render<MarkdownText>(p => p.Add(m => m.Text, "**bold** *slanted* `literal`"));

        cut.Find("strong").TextContent.Should().Be("bold");
        cut.Find("em").TextContent.Should().Be("slanted");
        cut.Find("code").TextContent.Should().Be("literal");
    }

    [Fact]
    public void Nested_emphasis_nests_the_elements()
    {
        var cut = Render<MarkdownText>(p => p.Add(m => m.Text, "**bold with *both* inside**"));

        cut.Find("strong em").TextContent.Should().Be("both");
    }

    [Fact]
    public void Nothing_to_say_renders_nothing()
    {
        var cut = Render<MarkdownText>(p => p.Add(m => m.Text, null));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void A_description_can_never_inject_markup()
    {
        // The descriptions arrive in a catalog downloaded from a GitHub release. Rendering them
        // through a MarkupString would make every one of them an injection site; this is the test
        // that fails if someone "simplifies" the renderer into one.
        var cut = Render<MarkdownText>(p => p.Add(m => m.Text, "<img src=x onerror=\"alert(1)\"> and **bold**"));

        cut.FindAll("img").Should().BeEmpty();
        cut.Find("p").TextContent.Should().StartWith("<img src=x onerror=\"alert(1)\">");
        cut.Find("strong").TextContent.Should().Be("bold");
    }
}
