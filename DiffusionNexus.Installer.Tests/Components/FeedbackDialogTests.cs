using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class FeedbackDialogTests : BunitContext
{
    private Mock<IFeedbackReportingService> Arrange(FeedbackSubmissionResult result)
    {
        var service = new Mock<IFeedbackReportingService>();
        service.Setup(s => s.SubmitAsync(It.IsAny<FeedbackReport>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(result);
        Services.AddSingleton(service.Object);
        return service;
    }

    private IRenderedComponent<FeedbackDialog> Open() =>
        Render<FeedbackDialog>(p => p.Add(x => x.Visible, true));

    [Fact]
    public void Renders_nothing_until_it_is_opened()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Render<FeedbackDialog>(p => p.Add(x => x.Visible, false));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Will_not_submit_without_a_title_and_a_description()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Open();

        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#feedback-title").Change("Install fails on a folder with spaces");
        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeTrue("description is still empty");

        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Sends_a_report_stamped_as_coming_from_the_installer()
    {
        var service = Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() => service.Verify(s => s.SubmitAsync(
            It.Is<FeedbackReport>(r =>
                r.Product == FeedbackProduct.Installer &&
                r.Title == "Install fails" &&
                r.Description == "It stops at step 3." &&
                r.ScreenshotPng == null &&
                r.LogTail == null &&
                r.AppVersion != null &&
                r.Os != null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void Shows_the_issue_url_when_the_report_lands()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/Into-The-Latent/Feedback/issues/42"));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".feedback-success").TextContent.Should().Contain("issues/42"));
    }

    [Fact]
    public void Keeps_the_dialog_and_the_typed_text_when_submission_fails()
    {
        // SubmitAsync never throws for network or parse failures -- it returns Success == false.
        // Closing on failure would throw away the thing the user just wrote.
        Arrange(FeedbackSubmissionResult.Failed("The relay returned 503."));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".feedback-error").TextContent.Should().Contain("The relay returned 503."));

        cut.Find("#feedback-title").GetAttribute("value").Should().Be("Install fails");
        cut.FindAll(".feedback-dialog").Should().ContainSingle("the dialog must stay open");
    }

    [Fact]
    public async Task Closes_when_cancelled()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));
        var closed = false;

        var cut = Render<FeedbackDialog>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.OnClose, () => closed = true));

        await cut.Find(".feedback-cancel").ClickAsync(new());

        closed.Should().BeTrue();
    }
}
