using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The one "Advanced settings" bar every wizard page uses, so they all look and behave the same.</summary>
public class AdvancedDisclosureTests : BunitContext
{
    [Fact]
    public void Is_closed_by_default_and_opens_on_click()
    {
        var cut = Render<AdvancedDisclosure>(p => p
            .Add(x => x.Subtitle, "custom model folders")
            .AddChildContent("<p id=\"inside\">hidden until opened</p>"));

        cut.FindAll("#inside").Should().BeEmpty();
        var toggle = cut.Find(".advanced-toggle");
        toggle.TextContent.Should().Contain("Advanced settings").And.Contain("custom model folders");
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        toggle.Click();

        cut.FindAll("#inside").Should().ContainSingle();
        cut.Find(".advanced-toggle").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void Shows_a_badge_only_when_one_is_given()
    {
        var plain = Render<AdvancedDisclosure>(p => p.Add(x => x.Subtitle, "x").AddChildContent("<p></p>"));
        plain.FindAll(".advanced-toggle .tag").Should().BeEmpty();

        var badged = Render<AdvancedDisclosure>(p => p.Add(x => x.Subtitle, "x").Add(x => x.Badge, "custom folders in use").AddChildContent("<p></p>"));
        badged.Find(".advanced-toggle .tag").TextContent.Should().Be("custom folders in use");
    }
}
