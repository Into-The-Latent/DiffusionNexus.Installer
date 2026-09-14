using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using FluentAssertions;
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
        cut.Find(".screen > .screen-body > .probe").TextContent.Should().Be("page content");
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
        cut.FindAll(".community-link").Should().HaveCount(3);
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
}
