using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class GalleryPageTests : BunitContext
{
    [Fact]
    public void The_footer_links_to_the_third_party_licences_even_when_the_catalog_is_empty()
    {
        // Outside the wizard the notices live on their own page. The footer must be reachable
        // whatever the catalog did, so it sits outside the loaded/empty/error branches.
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        Services.AddSingleton(source.Object);
        Services.AddSingleton(new GalleryBuilder(source.Object, new WizardModuleRegistry(() => [])));

        var cut = Render<DiffusionNexus.Installer.Electron.Components.Pages.Gallery>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No workloads are available"));
        cut.Find(".gallery-footer a[href='/licenses']").TextContent.Should().Contain("Third-party licences");
    }
}
