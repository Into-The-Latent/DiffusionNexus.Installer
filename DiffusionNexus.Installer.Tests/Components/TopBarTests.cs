using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.Electron.Services;
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
    public void Shows_the_app_version_the_rest_of_the_app_shows()
    {
        var cut = Render<TopBar>();

        // Asserted against AppVersion.Display, not against "contains no +". The bar, /updates and
        // the feedback report must agree, and the only way they can is by reading the one
        // accessor -- this fails the moment the bar grows its own copy of the two lines.
        //
        // The old "NotContain(\"+\")" assertion passed vacuously: Directory.Build.props sets
        // IncludeSourceRevisionInInformationalVersion=false, so no build of this repo produces a
        // suffix to strip. The stripping itself is covered where it can actually be exercised,
        // in Services/AppVersionTests.
        cut.Find(".top-bar-version").TextContent.Should().Be($"v{AppVersion.Display}");
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
