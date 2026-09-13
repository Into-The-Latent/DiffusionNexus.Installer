using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The bar across the top of the welcome and workload screens. Carries the links that used to sit
/// in the gallery footer.
/// </summary>
public class TopBarTests : BunitContext
{
    [Fact]
    public void Links_to_licences_and_updates()
    {
        var cut = Render<TopBar>();

        cut.Find("a[href='/licenses']").TextContent.Should().Contain("Licences");
        cut.Find("a[href='/updates']").TextContent.Should().Contain("Check for Updates");
    }

    [Fact]
    public void Shows_the_app_version()
    {
        var cut = Render<TopBar>();

        // Whatever the assembly reports, it must not be the raw informational version with its
        // "+<commit sha>" suffix -- that is build metadata, not something to show a user.
        var version = cut.Find(".top-bar-version").TextContent;
        version.Should().StartWith("v");
        version.Should().NotContain("+");
    }

    [Fact]
    public void Raises_the_feedback_callback_rather_than_navigating()
    {
        var raised = false;
        var cut = Render<TopBar>(p => p.Add(x => x.OnFeedback, () => raised = true));

        var button = cut.Find(".top-bar-feedback");
        button.TagName.Should().Be("BUTTON", "an anchor would navigate the Electron window away");

        button.Click();

        raised.Should().BeTrue();
    }

    [Fact]
    public void Only_offers_developer_tools_when_the_page_it_links_to_exists()
    {
        var cut = Render<TopBar>();

        // The /debug page is compiled out of Release builds by the csproj, so the link must go
        // with it -- a Release build linking to a page that does not exist is a 404 in the user's
        // face. This assertion therefore flips with the build configuration.
        var expected =
#if DEBUG
            1;
#else
            0;
#endif
        cut.FindAll("a[href='/debug']").Should().HaveCount(expected);
    }
}
