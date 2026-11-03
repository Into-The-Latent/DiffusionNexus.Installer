using Bunit;
using Bunit.TestDoubles;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The workload screen on the two new paths: the Update only tile, and Select multiple.</summary>
public class SelectMultipleTests : BunitContext
{
    private readonly WorkloadPicks _picks = new();

    public SelectMultipleTests()
    {
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        Services.AddSingleton(OfflineServerMessages.Cache());
        UpdateSignals.Register(Services);
        Services.AddSingleton(_picks);
    }

    private static InstallationConfiguration Workload(string name, string tiers = "8,16,24,32", bool withModel = true, WorkflowType type = WorkflowType.Image)
    {
        var workload = new InstallationConfiguration
        {
            Id = Guid.NewGuid(),
            Name = name,
            WorkflowType = type,
            Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
            Vram = new VramSettings { VramProfiles = tiers },
        };
        if (withModel) workload.ModelDownloads.Add(new ModelDownload { Name = "model" });
        return workload;
    }

    private void Arrange(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));
    }

    private IRenderedComponent<SoftwareWorkloads> RenderPath(WizardMode mode) =>
        Render<SoftwareWorkloads>(p => p.Add(x => x.Software, "ComfyUI").Add(x => x.Mode, mode));

    private static void TurnOn(IRenderedComponent<SoftwareWorkloads> cut) =>
        cut.Find("[data-role=select-multiple]").Change(true);

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<SoftwareWorkloads> cut, string name) =>
        cut.FindAll(".workload-card").Single(c => c.QuerySelector(".workload-card-name")!.TextContent.Contains(name));

    private static string? Version(IRenderedComponent<SoftwareWorkloads> cut, string name) =>
        Card(cut, name).QuerySelector("select")!.GetAttribute("value");

    private static void Include(IRenderedComponent<SoftwareWorkloads> cut, string name) =>
        Card(cut, name).QuerySelector("[data-role=include]")!.Change(true);

    [Fact]
    public void A_fresh_install_has_none_of_this()
    {
        Arrange(Workload("A"));

        var cut = RenderPath(WizardMode.FreshInstall);

        cut.FindAll("[data-role=select-multiple]").Should().BeEmpty();
        cut.FindAll("[data-role=update-only]").Should().BeEmpty();
    }

    [Fact]
    public void The_update_path_has_the_tile_first()
    {
        Arrange(Workload("A"));

        var cut = RenderPath(WizardMode.UpdateExisting);

        var grid = cut.Find(".workload-grid");
        grid.Children[0].GetAttribute("data-role").Should().Be("update-only");
        grid.Children[0].TextContent.Should().Contain("Update only").And.Contain("Updates ComfyUI and adds no workload.");
    }

    [Fact]
    public void The_tile_goes_to_its_own_wizard()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.UpdateExisting);

        cut.Find("[data-role=update-only] button").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/existing/update/only");
    }

    [Fact]
    public void The_tile_goes_away_with_select_multiple()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.UpdateExisting);

        TurnOn(cut);

        cut.FindAll("[data-role=update-only]").Should().BeEmpty();
    }

    [Fact]
    public void Models_only_has_no_tile()
    {
        Arrange(Workload("A"));

        RenderPath(WizardMode.ModelsOnly).FindAll("[data-role=update-only]").Should().BeEmpty();
    }

    [Fact]
    public void A_workload_with_nothing_to_add_is_hidden_on_the_update_path()
    {
        Arrange(Workload("Bare", withModel: false), Workload("Full"));

        var cut = RenderPath(WizardMode.UpdateExisting);

        cut.FindAll(".workload-card:not(.update-only-tile) .workload-card-name").Should().ContainSingle().Which.TextContent.Should().Contain("Full");
    }

    [Fact]
    public void A_workload_that_only_brings_custom_nodes_is_offered_on_the_update_path()
    {
        var nodes = Workload("Nodes", withModel: false);
        nodes.GitRepositories.Add(new GitRepository { Url = "https://github.com/x/SomeNode" });
        Arrange(nodes);

        RenderPath(WizardMode.UpdateExisting).FindAll(".workload-card:not(.update-only-tile) .workload-card-name").Should().ContainSingle();
    }

    [Fact]
    public void A_workload_that_only_brings_the_manager_is_what_update_only_already_does()
    {
        var bare = Workload("Blank", withModel: false);
        bare.GitRepositories.Add(new GitRepository { Url = "https://github.com/ltdrdata/ComfyUI-Manager" });
        Arrange(bare);

        RenderPath(WizardMode.UpdateExisting).FindAll(".workload-card:not(.update-only-tile) .workload-card-name").Should().BeEmpty();
    }

    [Fact]
    public void A_workload_with_only_workflows_is_offered_on_the_update_path()
    {
        var workflows = Workload("Flows", withModel: false);
        workflows.Workflows.Add(new ComfUIWorkflow { Name = "w" });
        Arrange(workflows);

        RenderPath(WizardMode.UpdateExisting).FindAll(".workload-card:not(.update-only-tile) .workload-card-name").Should().ContainSingle();
    }

    [Fact]
    public void A_single_card_on_the_update_path_opens_its_own_wizard()
    {
        var workload = Workload("A");
        Arrange(workload);
        var cut = RenderPath(WizardMode.UpdateExisting);

        Card(cut, "A").QuerySelector(".workload-card-install")!.TextContent.Trim().Should().Be("Choose");
        cut.Find(".workload-card:not(.update-only-tile) .workload-card-install").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/existing/update/{workload.Id}");
    }

    [Theory]
    [InlineData(WizardMode.ModelsOnly)]
    [InlineData(WizardMode.UpdateExisting)]
    public void The_memory_panel_starts_at_32_with_the_switch_on(WizardMode mode)
    {
        Arrange(Workload("A"));
        var cut = RenderPath(mode);
        cut.FindAll("[data-role=memory]").Should().BeEmpty();

        TurnOn(cut);

        cut.Find("[data-role=memory]").GetAttribute("value").Should().Be("32");
        cut.Markup.Should().Contain("How much memory does your graphics card have?");
        cut.FindAll("[data-role=memory] option").Select(o => o.TextContent.Trim()).Should().Equal("8", "12", "16", "24", "32");
    }

    [Fact]
    public void Each_card_starts_on_its_largest_version()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.ModelsOnly);

        TurnOn(cut);

        Version(cut, "A").Should().Be("32");
    }

    [Fact]
    public void Setting_the_memory_moves_every_card()
    {
        Arrange(Workload("A"), Workload("B", "12,16,24"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);

        cut.Find("[data-role=memory]").Change("16");

        Version(cut, "A").Should().Be("16");
        Version(cut, "B").Should().Be("16");
    }

    [Fact]
    public void Setting_the_memory_also_resets_a_card_changed_by_hand()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);
        Card(cut, "A").QuerySelector("select")!.Change("8");

        cut.Find("[data-role=memory]").Change("24");

        Version(cut, "A").Should().Be("24");
    }

    [Fact]
    public void A_card_changes_only_itself()
    {
        Arrange(Workload("A"), Workload("B"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);

        Card(cut, "A").QuerySelector("select")!.Change("8");

        Version(cut, "A").Should().Be("8");
        Version(cut, "B").Should().Be("32");
    }

    [Fact]
    public void A_workload_that_does_not_fit_stays_selectable_on_its_smallest_and_explains_itself()
    {
        Arrange(Workload("Ideogram", "24,32"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);

        cut.Find("[data-role=memory]").Change("16");

        var card = Card(cut, "Ideogram");
        Version(cut, "Ideogram").Should().Be("24");
        card.ClassList.Should().NotContain("workload-card-disabled");
        card.QuerySelector("[data-role=include]").Should().NotBeNull();
        card.QuerySelector("[data-role=too-large]")!.TextContent.Should().Be("This version needs 24 GB, so it may not run on your 16 GB card.");
    }

    [Fact]
    public void Picking_a_size_too_large_by_hand_gets_the_same_note()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);
        cut.Find("[data-role=memory]").Change("16");

        Card(cut, "A").QuerySelector("select")!.Change("24");

        Card(cut, "A").QuerySelector("[data-role=too-large]")!.TextContent.Should().Contain("needs 24 GB");
    }

    [Fact]
    public void A_workload_with_no_versions_says_so()
    {
        Arrange(Workload("A", tiers: string.Empty));
        var cut = RenderPath(WizardMode.ModelsOnly);

        TurnOn(cut);

        Card(cut, "A").TextContent.Should().Contain("One version only");
        Card(cut, "A").QuerySelector("select").Should().BeNull();
    }

    [Fact]
    public void The_bar_counts_what_is_included()
    {
        Arrange(Workload("A"), Workload("B"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);

        cut.Find("[data-role=selected-count]").TextContent.Should().Be("No workloads selected");
        cut.Find("[data-role=continue]").HasAttribute("disabled").Should().BeTrue();

        Include(cut, "A");

        cut.Find("[data-role=continue]").TextContent.Trim().Should().Be("Continue with 1 workload");
        cut.Find("[data-role=continue]").HasAttribute("disabled").Should().BeFalse();

        Include(cut, "B");

        cut.Find("[data-role=continue]").TextContent.Trim().Should().Be("Continue with 2 workloads");
    }

    [Fact]
    public void A_filter_keeps_what_was_picked()
    {
        Arrange(Workload("A"), Workload("Clip", type: WorkflowType.Video));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);
        Include(cut, "A");

        cut.FindAll(".filters button").Single(b => b.TextContent.Trim() == "Video").Click();

        cut.Find("[data-role=continue]").TextContent.Trim().Should().Be("Continue with 1 workload");
    }

    [Theory]
    [InlineData(WizardMode.ModelsOnly, "/existing/models/selected")]
    [InlineData(WizardMode.UpdateExisting, "/existing/update/selected")]
    public void Continue_hands_over_the_picks_with_their_versions(WizardMode mode, string route)
    {
        var a = Workload("A");
        var b = Workload("B");
        Arrange(a, b);
        var cut = RenderPath(mode);
        TurnOn(cut);
        cut.Find("[data-role=memory]").Change("16");
        Card(cut, "B").QuerySelector("select")!.Change("8");
        Include(cut, "A");
        Include(cut, "B");

        cut.Find("[data-role=continue]").Click();

        _picks.Get(mode).Should().BeEquivalentTo([new WorkloadPick(a.Id, 16), new WorkloadPick(b.Id, 8)]);
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(route);
    }

    [Fact]
    public void Switching_off_gives_the_buttons_back()
    {
        Arrange(Workload("A"));
        var cut = RenderPath(WizardMode.ModelsOnly);
        TurnOn(cut);

        cut.Find("[data-role=select-multiple]").Change(false);

        cut.FindAll("[data-role=memory]").Should().BeEmpty();
        cut.FindAll(".workload-card-install").Should().ContainSingle();
    }
}
