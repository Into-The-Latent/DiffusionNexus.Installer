using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WelcomePageTests : BunitContext
{
    private readonly (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) _signals;

    public WelcomePageTests()
    {
        // Welcome wraps itself in <ScreenShell>, which hosts <FeedbackDialog> (it only renders
        // markup when opened), so the page still needs IFeedbackReportingService resolvable at
        // construction time.
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        _signals = UpdateSignals.Register(Services);

        // The software strip watches itself through wwwroot/js/jukebox.js once its cards exist.
        // Planned here rather than per test because every render that produces cards makes the
        // call, and bUnit's strict mode throws on an unplanned one.
        _jukebox = JSInterop.SetupModule("./js/jukebox.js");
        _jukebox.SetupVoid("step", _ => true).SetVoidResult();

        // `observe` hands back a handle object with a dispose() on it. To bUnit anything returning
        // an IJSObjectReference is a module, so the handle is set up as one.
        _jukebox.SetupModule("observe", _ => true).SetupVoid("dispose", _ => true).SetVoidResult();
    }

    private readonly BunitJSModuleInterop _jukebox;

    /// <summary>
    /// Plays the part of jukebox.js reporting an edge state, which is the only way the page ever
    /// learns one: a wheel, a resize and a button click all arrive here.
    /// </summary>
    private static Task Report(IRenderedComponent<Welcome> cut, bool atStart, bool atEnd) =>
        cut.InvokeAsync(() => cut.Instance.OnEdgesChanged(new JukeboxEdges(atStart, atEnd)));

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

        // No /software/Fooocus link: a screen offering a choice of one should not exist. The whole
        // card is the actuator, exactly as it is for a multi-workload software -- five of the six
        // cards take this path, so a card whose only clickable pixel is a small button would make
        // "not clickable" the majority behaviour.
        cut.WaitForAssertion(() => cut.FindAll("a[href='/software/Fooocus']").Should().BeEmpty());
        cut.Find(".software-card > a").GetAttribute("href").Should().Be($"/install/{fooocus.Id}");
        cut.Find(".software-card-count").TextContent.Should().Contain("straight to setup");
    }

    [Fact]
    public void Shows_the_artwork_that_ships_for_a_software()
    {
        // The logo is resolved by this component from Type -- the paths live in the Electron
        // project next to the files they name, not on SoftwareEntry -- so the card is where the
        // wiring can actually be checked.
        Arrange(Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.Find(".software-card-art img")
            .GetAttribute("src").Should().Be("img/software/fooocus.jpg"));
        cut.FindAll(".software-card-art-fallback").Should().BeEmpty();
    }

    [Fact]
    public void Does_not_promise_setup_on_a_card_that_goes_nowhere()
    {
        // Same fixture as the test below, asserting the half it never looked at. CardHref already
        // returns null for a single uninstallable workload, so the card renders with NO anchor --
        // and used to read "straight to setup" directly above the reason it cannot be set up.
        var fooocus = Workload(RepositoryType.Fooocus, "Fooocus");
        fooocus.ModelDownloads.Add(new ModelDownload
        {
            Name = "checkpoint",
            Url = "https://example.com/model.safetensors"
        });

        Arrange(fooocus);

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().ContainSingle());

        var count = cut.Find(".software-card-count").TextContent.Trim();
        count.Should().NotContain("straight to setup", "this card has no anchor to take anyone anywhere");
        count.Should().Be("1 workload", "and \"1 workloads\" is not a thing anyone should have to read");
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

        // No anchor at all: there must be no click-through to an install that cannot succeed, and
        // app.css gates the hover cue on `.software-card:has(> a)`, so this is also what stops the
        // card advertising a target it does not have.
        cut.FindAll(".software-card a").Should().BeEmpty();
        cut.Find(".software-card").ClassName.Should().Contain("software-card-disabled");
    }

    [Fact]
    public void The_software_cards_sit_in_one_scrollable_strip()
    {
        // One row, not a wrapping grid: the second row is what pushed the community links off a
        // 16:9 window.
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.Fooocus, "Fooocus"),
            Workload(RepositoryType.AceStep, "ACE-Step-1.5"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".jukebox-track .software-card").Should().HaveCount(3));
        cut.FindAll(".software-grid").Should().BeEmpty("the grid is what wrapped");
        cut.FindAll(".jukebox-arrow").Should().HaveCount(2);
    }

    [Fact]
    public void The_page_starts_watching_the_strip_once_it_has_cards_to_watch()
    {
        // Nothing to observe on the "Loading the catalog..." render, so this cannot be keyed on
        // firstRender -- the catalog read is awaited, so the first render has no track in it.
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"), Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => JSInterop.Invocations["observe"].Should().ContainSingle());

        // Until the browser says otherwise the strip is against its left edge, which is the one
        // thing true of every strip before it has been measured.
        cut.FindAll(".jukebox-arrow")[0].HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".jukebox-arrow")[1].HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Both_arrows_go_dead_when_every_software_already_fits()
    {
        // A button that cannot do anything must not look like it can. Only the browser knows
        // whether the strip overflows -- a card count cannot answer it -- so the page takes that
        // answer and does not compute one.
        Arrange(Workload(RepositoryType.Fooocus, "Fooocus"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".jukebox-arrow").Should().HaveCount(2));

        await Report(cut, atStart: true, atEnd: true);

        cut.FindAll(".jukebox-arrow").Should().OnlyContain(a => a.HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_arrows_follow_the_strip_even_when_nothing_was_clicked()
    {
        // A wheel, a trackpad and a resized window all move the strip or change how much of it
        // fits, without any click. Reading the position once per click would leave the buttons
        // claiming something the strip stopped agreeing with.
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"), Workload(RepositoryType.Fooocus, "Fooocus"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".jukebox-arrow").Should().HaveCount(2));

        await Report(cut, atStart: false, atEnd: true);

        cut.FindAll(".jukebox-arrow")[0].HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".jukebox-arrow")[1].HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Leaving_the_screen_detaches_the_listeners_it_attached()
    {
        // The scroll listener and the ResizeObserver live on the browser side and hold a reference
        // back to this component. Navigating between the welcome screen and a workload screen and
        // back is the app's most-travelled path, so leaking one per visit is not a slow leak.
        Arrange(Workload(RepositoryType.Fooocus, "Fooocus"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => JSInterop.Invocations["observe"].Should().ContainSingle());

        await DisposeComponentsAsync();

        JSInterop.Invocations["dispose"].Should().ContainSingle();
    }

    [Fact]
    public void Clicking_an_arrow_pages_the_strip_in_that_direction()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".jukebox-arrow")[1].HasAttribute("disabled").Should().BeFalse());

        cut.FindAll(".jukebox-arrow")[1].Click();

        // 1 is forward, which is the direction the module's `step` expects. The page deliberately
        // applies no edge state of its own here: the scroll is smooth and has not landed yet, so
        // anything read now would be the position being left, not the one being reached.
        JSInterop.Invocations["step"].Single().Arguments[1].Should().Be(1);
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
        cut.FindAll(".community-link").Should().HaveCount(CommunityLink.Defaults.Count);
    }

    private static string Notice => ".catalog-update-notice";

    [Fact]
    public void Announces_a_waiting_catalog_update_with_its_counts()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Available(
            workloads: [CatalogChecks.WorkloadUpdated("A", "V1.0", "V1.1"), CatalogChecks.WorkloadAdded("B", "V1.0"), CatalogChecks.WorkloadRemoved("C", "V1.0")],
            workflows: [CatalogChecks.WorkflowAdded("W", "V1.0", "A")]);
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
        {
            var notice = cut.Find(Notice);
            notice.TextContent.Should().Contain("A catalog update is available: 3 workloads and 1 workflow changed.");
            notice.QuerySelector("a[href='/updates']")!.TextContent.Should().Be("Review and apply");
        });
    }

    [Fact]
    public void Points_at_the_app_update_when_the_catalog_needs_newer_software()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.RequiresNewerSoftware);
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(Notice).TextContent.Should().Contain("Update the installer to receive it."));
    }

    [Theory]
    [InlineData(CatalogUpdateOutcome.UpToDate)]
    [InlineData(CatalogUpdateOutcome.Failed)]
    [InlineData(CatalogUpdateOutcome.OverrideActive)]
    public void Says_nothing_for_other_outcomes(CatalogUpdateOutcome outcome)
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Outcome(outcome, "boom");
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        cut.FindAll(Notice).Should().BeEmpty();
    }

    [Fact]
    public void Appears_when_the_startup_check_finishes_after_the_page_rendered()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        cut.FindAll(Notice).Should().BeEmpty();

        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.Catalog.RaiseChanged();

        cut.WaitForAssertion(() => cut.Find(Notice).TextContent.Should().Contain("1 workload and 0 workflows changed"));
    }

    [Fact]
    public async Task Unsubscribes_from_the_coordinator_on_dispose()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        var before = _signals.Catalog.Subscribers;   // page + its TopBar
        before.Should().Be(2, "the page and its TopBar each subscribe once");

        await DisposeComponentsAsync();

        _signals.Catalog.Subscribers.Should().Be(0, $"{before} handlers were attached and all must go");
    }
}
