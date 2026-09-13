using Bunit;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The "Join the Community" footer. Hardcoded this slice; issue #6 covers making it editable
/// without a release.
/// </summary>
public class CommunityLinksTests : BunitContext
{
    [Fact]
    public void Ships_the_three_links_the_2x_installer_ships()
    {
        CommunityLink.Default.Select(l => l.Name)
            .Should().Equal("YouTube", "Patreon", "Civitai");

        CommunityLink.Default.Select(l => l.Url).Should().Equal(
            "https://www.youtube.com/@IntoTheLatent",
            "https://patreon.com/AIKnowledgeCentral",
            "https://civitai.com/user/AIknowlege2go");
    }

    [Fact]
    public void Every_link_is_https()
    {
        CommunityLink.Default.Should().OnlyContain(l => l.Url.StartsWith("https://"));
    }

    [Fact]
    public void Renders_a_row_per_link_under_a_heading()
    {
        var cut = Render<CommunityLinks>();

        cut.Find(".community h4").TextContent.Should().Contain("Join the Community");
        cut.FindAll(".community-link").Should().HaveCount(3);
    }

    [Fact]
    public void Opens_links_outside_the_app_window()
    {
        var cut = Render<CommunityLinks>();

        // Outside Electron these are ordinary anchors, and they MUST carry target=_blank:
        // a same-window navigation strands the user in an installer that has become a browser
        // with no address bar and no way back.
        foreach (var link in cut.FindAll(".community-link"))
        {
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Contain("noopener");
        }
    }
}
