using Bunit;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The bar across the top of the welcome and workload screens. Carries the links that used to sit
/// in the gallery footer.
/// </summary>
public class TopBarTests : BunitContext
{
    private readonly (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) _signals;

    public TopBarTests()
    {
        _signals = UpdateSignals.Register(Services);
    }

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

    private static string Updates => "a[href='/updates']";

    [Fact]
    public void Stays_plain_when_nothing_is_waiting()
    {
        var cut = Render<TopBar>();

        cut.Find(Updates).ClassList.Should().NotContain("top-bar-attention");
        cut.Find(Updates).HasAttribute("title").Should().BeFalse();
        cut.Find(Updates).TextContent.Trim().Should().Be("Check for Updates");
    }

    [Fact]
    public void Marks_the_updates_link_when_a_catalog_update_is_waiting()
    {
        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<TopBar>();

        cut.Find(Updates).ClassList.Should().Contain("top-bar-attention");
        cut.Find(Updates).GetAttribute("title").Should().Be("Catalog update available");
    }

    // A dot was too quiet to notice: the button now says it, on the accent background.
    [Fact]
    public void Says_update_available_instead_of_check_for_updates_when_something_is_waiting()
    {
        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        Render<TopBar>().Find(Updates).TextContent.Trim().Should().Be("Update Available");
    }

    // electron-updater downloads straight away, which can take minutes; the user hears about the
    // update when it is found, not when the download ends.
    [Fact]
    public void Marks_it_as_soon_as_an_app_update_is_found()
    {
        _signals.App.MarkAvailable("3.0.8");

        var cut = Render<TopBar>();

        cut.Find(Updates).ClassList.Should().Contain("top-bar-attention");
        cut.Find(Updates).TextContent.Trim().Should().Be("Update Available");
        cut.Find(Updates).GetAttribute("title").Should().Be("App update 3.0.8 downloading");
    }

    [Fact]
    public void Lights_up_when_the_app_check_finds_one_after_the_bar_rendered()
    {
        var cut = Render<TopBar>();

        _signals.App.MarkAvailable("3.0.8");

        cut.WaitForAssertion(() => cut.Find(Updates).TextContent.Trim().Should().Be("Update Available"));
    }

    [Fact]
    public void Marks_it_when_an_app_update_is_ready()
    {
        _signals.App.MarkUpdateReady();

        var cut = Render<TopBar>();

        cut.Find(Updates).GetAttribute("title").Should().Be("App update ready");
    }

    [Fact]
    public void Names_both_when_both_are_waiting()
    {
        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.App.MarkUpdateReady();

        Render<TopBar>().Find(Updates).GetAttribute("title").Should().Be("Catalog update available and app update ready");
    }

    [Fact]
    public void Lights_up_when_the_startup_check_finishes_after_the_bar_rendered()
    {
        var cut = Render<TopBar>();
        cut.Find(Updates).ClassList.Should().NotContain("top-bar-attention");

        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.Catalog.RaiseChanged();

        cut.WaitForAssertion(() => cut.Find(Updates).ClassList.Should().Contain("top-bar-attention"));
    }

    [Fact]
    public async Task Stops_listening_when_disposed()
    {
        Render<TopBar>();
        _signals.Catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        // The coordinator is a singleton that outlives every circuit; a handler left behind pins
        // the bar (and the page under it) for the life of the app.
        _signals.Catalog.Subscribers.Should().Be(0);
    }
}
