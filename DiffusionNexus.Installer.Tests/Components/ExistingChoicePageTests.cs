using Bunit;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class ExistingChoicePageTests : BunitContext
{
    public ExistingChoicePageTests()
    {
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        Services.AddSingleton(OfflineServerMessages.Cache());
        UpdateSignals.Register(Services);
    }

    [Fact]
    public void Asks_what_the_user_wants_to_do()
    {
        var cut = Render<ExistingChoice>();

        cut.Find("h1").TextContent.Should().Be("What do you want to do?");
        cut.Find(".workload-screen-head p").TextContent.Should().Be("Choose one. Nothing changes until you confirm at the end.");
        cut.Find(".workload-screen-back").GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void Choice_models_card_links_on()
    {
        var cut = Render<ExistingChoice>();

        var card = cut.Find("a[data-role=models-card]");

        card.GetAttribute("href").Should().Be("/existing/models");
        card.TextContent.Should().Contain("Download models only")
            .And.Contain("Downloads a workload's models into any folder you choose. We don't check the folder, and nothing else is installed or changed.");
    }

    [Fact]
    public void Choice_update_card_opens_the_update_path()
    {
        var cut = Render<ExistingChoice>();

        var card = cut.Find("[data-role=update-card]");

        card.TagName.Should().Be("A");
        card.GetAttribute("href").Should().Be("/existing/update");
        card.TextContent.Should().Contain("Update existing ComfyUI").And.NotContain("Coming soon");
    }
}
