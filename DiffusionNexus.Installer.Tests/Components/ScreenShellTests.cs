using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The chrome the welcome and workload screens share. Exists because the three moving parts --
/// the bar, the dialog its button opens, and the open/close flag between them -- were
/// hand-assembled per page, so a third screen could add <c>TopBar</c>, forget
/// <c>FeedbackDialog</c>, and ship a Feedback button that silently did nothing.
/// </summary>
public class ScreenShellTests : BunitContext
{
    public ScreenShellTests()
    {
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        Services.AddSingleton(OfflineServerMessages.Cache());
        UpdateSignals.Register(Services);
    }

    private IRenderedComponent<ScreenShell> RenderShell() =>
        Render<ScreenShell>(p => p.AddChildContent("<p class=\"probe\">page content</p>"));

    [Fact]
    public void Puts_the_bar_above_the_pages_own_content()
    {
        var cut = RenderShell();

        // Direct child of .screen, which is what app.css's `.page:has(> .screen)` opt-out keys on:
        // the bar spans the window and .screen-body does the centring.
        cut.Find(".screen > .top-bar").Should().NotBeNull();
        cut.Find(".screen > .screen-scroll > .screen-body > .probe").TextContent.Should().Be("page content");
    }

    [Fact]
    public void Puts_the_community_footer_below_the_pages_own_content()
    {
        // The footer belongs to the shell, not to the welcome screen that used to own it: with it
        // there, picking a software made the bar and the footer vanish together and the window
        // changed shape mid-flow. Direct child of .screen for the same reason the bar is -- it
        // spans the window rather than stopping at the page's column.
        var cut = RenderShell();

        cut.Find(".screen > .community").Should().NotBeNull();
        cut.FindAll(".community-link").Should().HaveCount(CommunityLink.Defaults.Count);
    }

    [Fact]
    public void Puts_an_announcement_between_the_bar_and_the_pages_own_content()
    {
        // In the shell so every screen carries it (issue #12): a "download the fixed build"
        // notice has to reach a user who is already past the welcome screen. Direct child of
        // .screen, like the bar, so it spans the window rather than the page's column.
        Services.AddSingleton(OfflineServerMessages.Cache(new ServerMessage { Id = "fix", Message = "Fixed build available." }));

        var cut = RenderShell();

        cut.WaitForAssertion(() =>
            cut.Find(".screen > .top-bar + .server-messages + .screen-scroll").Should().NotBeNull());
        cut.Find(".server-message-text").TextContent.Should().Be("Fixed build available.");
    }

    [Fact]
    public void With_nothing_to_announce_the_body_follows_the_bar_directly()
    {
        var cut = RenderShell();

        cut.Find(".screen > .top-bar + .screen-scroll").Should().NotBeNull();
    }

    [Fact]
    public void The_feedback_button_opens_the_dialog_with_no_wiring_from_the_page()
    {
        var cut = RenderShell();

        cut.FindAll(".feedback-dialog").Should().BeEmpty("the dialog is closed until it is asked for");

        cut.Find(".top-bar-feedback").Click();

        cut.Find(".feedback-dialog").Should().NotBeNull(
            "a Feedback button that opens nothing is the failure this component exists to prevent");
    }

    [Fact]
    public void Closing_the_dialog_puts_it_away_again()
    {
        var cut = RenderShell();
        cut.Find(".top-bar-feedback").Click();

        cut.Find(".feedback-cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".feedback-dialog").Should().BeEmpty());
    }

    [Fact]
    public void Only_the_pages_own_content_scrolls()
    {
        // The bar and the footer are siblings of the scrolling region, never inside it -- inside,
        // they leave the window with the content, which is the bug this region exists to fix.
        var cut = RenderShell();

        cut.FindAll(".screen-scroll .top-bar, .screen-scroll .community").Should().BeEmpty();
        cut.Find(".screen > .screen-scroll + .community").Should().NotBeNull();
    }

    [Fact]
    public void Records_where_a_side_trip_should_come_back_to()
    {
        // Licences opened from a running install must lead back to that install, not the welcome
        // screen. Every flow screen wears the shell, so the shell is what remembers.
        Services.GetRequiredService<NavigationManager>().NavigateTo("/install/6f9619ff-8b86-d011-b42d-00cf4fc964ff?x=1");

        RenderShell();

        Services.GetRequiredService<DiffusionNexus.Installer.Electron.Services.ReturnTarget>()
            .Path.Should().Be("/install/6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    }
}
