// DiffusionNexus.Installer.Tests/Components/ModelSelectionPanelTests.cs
using Bunit;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class ModelSelectionPanelTests : BunitContext
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };
    private static readonly ModelDownload Unet = new() { Name = "UNet", Destination = @"models\unet", Url = "https://h.invalid/unet.gguf" };

    private static WizardSelection Selection(string tiers = "")
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.Vram.VramProfiles = tiers;
        w.ModelDownloads.AddRange([Vae, Unet]);
        return new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
    }

    private static Mock<IModelPresenceScanner> Scanner(bool vaePresent)
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(
        [
            new ModelPresence(Vae.Id, vaePresent, vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null, []),
            new ModelPresence(Unet.Id, false, null, []),
        ]);
        return scanner;
    }

    private static IDiskSpaceEstimator Estimator(bool sufficient = true)
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(3L * 1024 * 1024 * 1024, 40L * 1024 * 1024 * 1024, sufficient, []));
        return estimator.Object;
    }

    private IRenderedComponent<ModelSelectionPanel> RenderPanel(ModelSelectionModule module, WizardSelection selection, Action? onChanged = null) =>
        Render<ModelSelectionPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Selection, selection)
            .Add(x => x.EstimateDebounce, TimeSpan.Zero)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => onChanged?.Invoke())));

    [Fact]
    public async Task Lists_every_model_ticked_under_its_folder_and_marks_the_one_already_on_disk()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: true).Object, Estimator());
        var selection = Selection();
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.FindAll("h3").Select(h => h.TextContent.Trim()).Should().Equal(@"models\unet", @"models\vae");
        cut.FindAll("input[type=checkbox]").Should().HaveCount(2).And.OnlyContain(i => i.HasAttribute("checked"));
        cut.FindAll(".tag").Should().ContainSingle().Which.TextContent.Should().Contain("already downloaded");
        cut.Markup.Should().NotContain("variant", "spec decision 2: no tier annotation on rows");
    }

    [Fact]
    public async Task Unticking_a_model_updates_the_module_and_raises_Changed()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator());
        var selection = Selection();
        await module.InitializeAsync(selection);
        var changed = false;

        var cut = RenderPanel(module, selection, () => changed = true);
        cut.FindAll("input[type=checkbox]")[0].Change(false);

        module.SelectedCount.Should().Be(1);
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_free_space_is_said_plainly_and_not_flagged_as_a_shortfall()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(3L * 1024 * 1024 * 1024, 0, true, [], AvailableKnown: false));
        var selection = Selection();
        var module = new ModelSelectionModule(Scanner(false).Object, estimator.Object);
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".disk-space").TextContent.Should().Contain("Needs about").And.Contain("could not read the free space");
            cut.Find(".disk-space").ClassList.Should().NotContain("disk-space-bad");
        });
    }

    [Fact]
    public async Task Shows_the_disk_space_estimate_and_flags_a_shortfall()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator(sufficient: false));
        var selection = Selection();
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".disk-space").TextContent.Should().Contain("Needs about");
            cut.Find(".disk-space").ClassList.Should().Contain("disk-space-bad");
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_tier_change_made_elsewhere_triggers_a_rescan_on_re_render()
    {
        // The VRAM panel is a sibling; its Changed re-renders the page, which re-renders this panel
        // with the same parameters. That render must notice the tier moved and rescan.
        var scanner = Scanner(vaePresent: false);
        var module = new ModelSelectionModule(scanner.Object, Estimator());
        var selection = Selection("8,12,16");
        selection.SelectedVramProfile = 8;
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);
        var scansBefore = scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan));

        selection.SelectedVramProfile = 16;
        cut.Render();

        module.LastScannedTier.Should().Be(16);
        scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan)).Should().Be(scansBefore + 1);
    }

    [Fact]
    public async Task A_workload_whose_models_are_all_disabled_says_so()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.ModelDownloads.Add(new ModelDownload { Name = "Off", Enabled = false });
        var selection = new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
        var module = new ModelSelectionModule(Scanner(false).Object, Estimator());
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.Markup.Should().Contain("disabled by its author");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }
}
