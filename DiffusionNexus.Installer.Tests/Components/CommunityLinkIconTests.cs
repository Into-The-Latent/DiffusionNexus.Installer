using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The glyph a footer chip carries. The key comes from an operator-edited document, so the
/// contract that matters is what happens to a key nobody drew yet.
/// </summary>
public class CommunityLinkIconTests : BunitContext
{
    [Theory]
    [InlineData("youtube")]
    [InlineData("patreon")]
    [InlineData("civitai")]
    [InlineData("globe")]
    [InlineData("mail")]
    [InlineData("linktree")]
    public void Every_shipped_key_has_its_own_glyph(string key)
    {
        var cut = Render<CommunityLinkIcon>(p => p.Add(i => i.Key, key));

        var svg = cut.Find("svg.community-icon");
        svg.GetAttribute("data-icon").Should().Be(key);
        svg.InnerHtml.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Keys_match_case_insensitively_and_ignore_whitespace()
    {
        var cut = Render<CommunityLinkIcon>(p => p.Add(i => i.Key, "  YouTube "));

        cut.Find("svg").GetAttribute("data-icon").Should().Be("youtube");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("discourse")]
    [InlineData("<script>")]
    public void Unknown_or_missing_keys_render_the_neutral_link_glyph(string? key)
    {
        // Never a blank chip: the operator can add a row before the installer knows its icon.
        var cut = Render<CommunityLinkIcon>(p => p.Add(i => i.Key, key));

        var svg = cut.Find("svg.community-icon");
        svg.GetAttribute("data-icon").Should().Be(CommunityLinkIcon.Fallback);
        svg.InnerHtml.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Is_decorative_to_assistive_tech()
    {
        // The chip's text is the label; a second announcement of "youtube" would be noise.
        var cut = Render<CommunityLinkIcon>(p => p.Add(i => i.Key, "youtube"));

        cut.Find("svg").GetAttribute("aria-hidden").Should().Be("true");
    }
}
