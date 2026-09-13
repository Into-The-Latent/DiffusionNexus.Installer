using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WelcomePageTests : BunitContext
{
    public WelcomePageTests()
    {
        // Welcome hosts <FeedbackDialog> unconditionally (it only renders markup when opened), so
        // the component still needs IFeedbackReportingService resolvable at construction time.
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
    }

    private static InstallationConfiguration Workload(RepositoryType software, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        WorkflowType = WorkflowType.Image,
        Repository = new MainRepositorySettings { Type = software }
    };

    private void Arrange(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));
    }

    [Fact]
    public void Shows_the_banner_and_the_title()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(".welcome-banner img").GetAttribute("src").Should().Be("img/banner.jpg"));
        cut.Find(".welcome-title").TextContent.Should().Contain("Easy Workload Installer");
    }

    [Fact]
    public void Renders_one_card_per_software_the_catalog_contains()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().HaveCount(2));
        cut.Markup.Should().Contain("ComfyUI").And.Contain("Fooocus");
        cut.Markup.Should().NotContain("Automatic 1111", "no A1111 workload is in this catalog");
    }

    [Fact]
    public void A_multi_workload_software_links_to_its_workload_screen()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.ComfyUI, "Ideogram-4.0"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(".software-card a").GetAttribute("href").Should().Be("/software/ComfyUI"));
        cut.Find(".software-card-count").TextContent.Should().Contain("2 workloads");
    }

    [Fact]
    public void A_single_workload_software_offers_the_workload_directly()
    {
        var fooocus = Workload(RepositoryType.Fooocus, "Fooocus");
        Arrange(fooocus);

        var cut = Render<Welcome>();

        // No /software/Fooocus link: a screen offering a choice of one should not exist.
        cut.WaitForAssertion(() => cut.FindAll("a[href='/software/Fooocus']").Should().BeEmpty());
        cut.Find(".software-card-count").TextContent.Should().Contain("straight to setup");
    }

    [Fact]
    public void Keeps_a_software_whose_only_workload_cannot_be_installed()
    {
        // A populated ModelDownloads list is one of WorkloadCapabilities.Blocking -- with no
        // modules registered nothing can satisfy it, so this workload is genuinely uninstallable,
        // not merely dressed up to look that way.
        var fooocus = Workload(RepositoryType.Fooocus, "Fooocus");
        fooocus.ModelDownloads.Add(new ModelDownload
        {
            Name = "checkpoint",
            Url = "https://example.com/model.safetensors"
        });

        // Verify by construction: if this fixture ever stopped being blocking, this assertion
        // catches it instead of the test silently exercising the installable branch below.
        new WizardModuleRegistry(() => []).IsInstallable(fooocus).Should().BeFalse();

        Arrange(fooocus);

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().ContainSingle());
        cut.Find(".software-card-unavailable").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.FindAll(".software-card-install").Should().BeEmpty();
    }

    [Fact]
    public void Says_why_it_is_empty_rather_than_taking_the_app_down()
    {
        // This rule was written for the old gallery and still applies: a hard catalog failure
        // must report itself, not throw out of the component lifecycle.
        //
        // The mock is deliberately discriminating rather than returning the error list
        // unconditionally: Diagnostics is empty until GetInstallerWorkloadsAsync has actually been
        // invoked, and only populated afterwards. That is the real SDK's behaviour -- diagnostics
        // are produced during the load the build triggers -- and it is what makes this test able to
        // tell "read after the build" apart from "read before it": a page that read Diagnostics too
        // early would see the empty list and render "No workloads are available" instead of CAT001.
        var loaded = false;
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .Callback(() => loaded = true)
              .ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(() => loaded
            ? new[] { new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, "CAT001", "catalog.zip is corrupt", "catalog.zip") }
            : Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CAT001").And.Contain("corrupt"));
    }

    [Fact]
    public void Reports_a_thrown_catalog_failure_instead_of_propagating_it()
    {
        // The try/catch around OnInitializedAsync exists for a hard failure, not just an empty
        // catalog -- a corrupt or locked catalog file can throw out of the SDK before it ever gets
        // to report a diagnostic. The page must show the message, not crash the component.
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new IOException("catalog.zip is locked"));
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("catalog.zip is locked"));
    }

    [Fact]
    public void Shows_the_community_footer_and_the_top_bar_whatever_the_catalog_did()
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        // Moved up from the old gallery footer; must survive an empty catalog.
        cut.WaitForAssertion(() => cut.Find("a[href='/licenses']").Should().NotBeNull());
        cut.FindAll(".community-link").Should().HaveCount(3);
    }
}
