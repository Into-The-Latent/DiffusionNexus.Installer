using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class SoftwareWorkloadsPageTests : BunitContext
{
    public SoftwareWorkloadsPageTests()
    {
        // SoftwareWorkloads wraps itself in <ScreenShell>, which hosts <FeedbackDialog> (it only
        // renders markup when opened), so the page still needs IFeedbackReportingService
        // resolvable at construction time.
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        UpdateSignals.Register(Services);
    }

    private static InstallationConfiguration Workload(RepositoryType software, string name, WorkflowType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        WorkflowType = type,
        Repository = new MainRepositorySettings { Type = software }
    };

    private Mock<IWorkloadSource> Arrange(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));
        return source;
    }

    private IRenderedComponent<SoftwareWorkloads> RenderFor(string software) =>
        Render<SoftwareWorkloads>(p => p.Add(x => x.Software, software));

    [Fact]
    public void Lists_only_the_chosen_softwares_workloads()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video),
            Workload(RepositoryType.Fooocus, "Fooocus", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));
        cut.Markup.Should().NotContain("Fooocus");
    }

    [Fact]
    public void Offers_only_the_types_this_software_actually_has()
    {
        // Audio exists in the catalog but belongs to AceStep, so it must not appear here. The
        // filter is catalog-derived, so it will appear by itself the day a ComfyUI workload
        // declares Audio -- nothing to schedule for later.
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video),
            Workload(RepositoryType.AceStep, "ACE-Step-1.5", WorkflowType.Audio));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
        {
            var labels = cut.FindAll(".filters button").Select(b => b.TextContent.Trim()).ToList();
            labels.Should().Equal("All", "Image", "Video");
        });
    }

    [Fact]
    public void Filters_the_cards_when_a_type_is_chosen()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video));

        var cut = RenderFor("ComfyUI");
        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));

        cut.FindAll(".filters button").Single(b => b.TextContent.Trim() == "Video").Click();

        cut.FindAll(".workload-card").Should().ContainSingle();
        cut.Markup.Should().Contain("Wan 2.2 - GGUF").And.NotContain("Krea-2-Turbo");
    }

    [Fact]
    public void Has_no_software_filter_because_the_previous_screen_answered_it()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() => cut.FindAll(".filters").Should().ContainSingle());
    }

    [Fact]
    public void Offers_a_way_back_to_the_welcome_screen()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
            cut.Find(".workload-screen-back").GetAttribute("href").Should().Be("/"));
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("")]
    public void Says_so_when_the_software_is_not_in_the_catalog(string software)
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor(software);

        cut.WaitForAssertion(() => cut.Find(".software-not-found").Should().NotBeNull());
        cut.Find(".software-not-found a").GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void Says_so_for_a_real_software_the_catalog_has_no_workloads_for()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("Forge");

        cut.WaitForAssertion(() => cut.Find(".software-not-found").Should().NotBeNull());
    }

    [Fact]
    public void Does_not_inherit_the_previous_softwares_chosen_filter()
    {
        // Regression guard for the leak Task 7's review flagged: filter state must reset per
        // software, not just on a fresh component instance. Re-rendering the SAME component with
        // a new Software parameter is the only way to actually exercise OnParametersSetAsync's
        // reset -- a fresh Render<T> per software would pass even if the reset code were deleted,
        // because a brand-new component starts with _type == null regardless.
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video),
            Workload(RepositoryType.Fooocus, "Fooocus-Image", WorkflowType.Image),
            Workload(RepositoryType.Fooocus, "Fooocus-Video", WorkflowType.Video));

        var cut = RenderFor("ComfyUI");
        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));

        cut.FindAll(".filters button").Single(b => b.TextContent.Trim() == "Video").Click();
        cut.FindAll(".workload-card").Should().ContainSingle();

        cut.Render(p => p.Add(x => x.Software, "Fooocus"));

        // If the reset in OnParametersSetAsync were missing, the stale Video filter would still
        // be applied here and only Fooocus-Video would show.
        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));
        cut.Markup.Should().Contain("Fooocus-Image").And.Contain("Fooocus-Video");
    }

    [Fact]
    public void Keeps_the_chosen_filter_when_the_same_parameters_are_supplied_again()
    {
        // The other half of the test above. OnParametersSetAsync runs whenever the parent hands
        // this component parameters, not only when Software CHANGES: the SSR-prerender plus
        // interactive-circuit pair already makes that twice on a plain page load, and a router
        // re-render, a cascading value change or a reconnect adds more. Resetting unconditionally
        // threw the user's chosen filter away and re-read the whole catalog -- a deep copy of every
        // catalogued workload -- flashing the grid back through "Loading the catalog...".
        var source = Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video));

        var cut = RenderFor("ComfyUI");
        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));

        cut.FindAll(".filters button").Single(b => b.TextContent.Trim() == "Video").Click();
        cut.FindAll(".workload-card").Should().ContainSingle();

        // Same Software, supplied again -- exactly what a re-render does.
        cut.Render(p => p.Add(x => x.Software, "ComfyUI"));

        cut.FindAll(".workload-card").Should().ContainSingle("the chosen filter must survive a re-render");
        cut.Markup.Should().Contain("Wan 2.2 - GGUF").And.NotContain("Krea-2-Turbo");

        source.Verify(
            s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "re-supplying the same Software must not re-read the catalog");
    }

    [Fact]
    public void Shows_catalog_diagnostics_instead_of_the_generic_not_found_message()
    {
        // Mirrors WelcomePageTests.Says_why_it_is_empty_rather_than_taking_the_app_down. The mock
        // is deliberately discriminating rather than returning the error list unconditionally:
        // Diagnostics is empty until GetInstallerWorkloadsAsync has actually been invoked, and
        // only populated afterwards. That is the real SDK's behaviour -- diagnostics are produced
        // during the load the build triggers -- and it is what makes this test able to tell "read
        // after the build" apart from "read before it": a page that read Diagnostics too early
        // would see the empty list and fall back to the generic "not in the catalog" message
        // instead of CAT001.
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

        // No ComfyUI workload in this catalog at all, so _entry resolves to null the same way it
        // would for any other not-found case -- the diagnostic is what tells the two apart.
        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("CAT001").And.Contain("catalog.zip is corrupt"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_dead_end_on_this_page_offers_a_route_home(bool thrown)
    {
        // Both no-content states in one test on purpose: they were written independently and the
        // thrown-failure branch shipped without a way out. This page is deep-linkable -- it is
        // where an Electron window refresh lands -- and TopBar carries no home control, so a dead
        // end here strands a user who has no address bar. One test so the two cannot drift apart.
        var source = new Mock<IWorkloadSource>();
        if (thrown)
        {
            source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new IOException("catalog.zip is locked"));
        }
        else
        {
            source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        }

        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
            cut.FindAll("a[href='/']").Should().NotBeEmpty("a screen with no content still needs a way back"));
    }

    [Fact]
    public void Reports_a_thrown_catalog_failure_instead_of_propagating_it()
    {
        // Mirrors Welcome.razor's contract: this page is deep-linkable, so a refresh mid-flow can
        // land here directly and hit the same catalog failure the welcome screen guards against.
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new IOException("catalog.zip is locked"));
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("catalog.zip is locked"));
    }
}
