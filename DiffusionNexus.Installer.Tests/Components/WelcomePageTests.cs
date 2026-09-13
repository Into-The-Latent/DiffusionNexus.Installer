using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WelcomePageTests : BunitContext
{
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
        // Registering no modules makes every workload with blocking capabilities uninstallable;
        // the card must still render so the reason is visible somewhere.
        Arrange(Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().ContainSingle());
    }

    [Fact]
    public void Says_why_it_is_empty_rather_than_taking_the_app_down()
    {
        // This rule was written for the old gallery and still applies: a hard catalog failure
        // must report itself, not throw out of the component lifecycle.
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(new[]
        {
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, "CAT001", "catalog.zip is corrupt", "catalog.zip")
        });

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CAT001").And.Contain("corrupt"));
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
