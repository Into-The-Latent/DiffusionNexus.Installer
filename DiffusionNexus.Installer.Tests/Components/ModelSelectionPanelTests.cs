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
    public async Task The_update_checkbox_moves_the_estimate()
    {
        var workload = new InstallationConfiguration
        {
            Name = "A",
            Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
            Torch = new TorchSettings { TorchVersion = "2.11.0", CudaVersion = "13.0" },
            Python = new PythonEnvironmentSettings { PythonVersion = "3.12" },
        };
        workload.ModelDownloads.Add(Vae);
        var union = SDK.Services.Installation.Existing.ExtendConfigurationMerger.Union([new SDK.Services.Installation.Existing.WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new Core.Existing.ExistingRun(selection, union, [workload], null);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DiskSpaceRequest, CancellationToken>((r, _) => seen.Enqueue(r.ModelsOnly))
            .ReturnsAsync(new DiskSpaceEstimate(1, 2, true, []));
        var module = new ModelSelectionModule(Scanner(false).Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        selection.Existing.Profile = new SDK.Services.Installation.Existing.ExistingComfyUIProfile(
            @"E:\C", SDK.Services.Installation.Existing.ExistingInstallSupport.Supported, @"E:\C", @"E:\C\venv", "3.12.7",
            new SDK.Services.Installation.Existing.InstalledTorchStack("2.8.0", "12.8", false, false), ManagerInstalled: true);
        var cut = RenderPanel(module, selection);
        cut.WaitForAssertion(() => seen.Should().Contain(false));

        selection.Existing.RunUpdate = false;
        cut.Render();

        cut.WaitForAssertion(() => seen.Should().Contain(true));
    }

    [Fact]
    public async Task A_panel_whose_page_left_finishes_its_scan_and_does_nothing_further()
    {
        // #45 shares the module with the page that left. Resumed elsewhere while this panel's scan was
        // in flight, the module reads as stale to this panel too; it used to scan again beside the live
        // panel and then schedule an estimate on the cancellation source Dispose had released.
        using var gate = new ManualResetEventSlim();
        var scans = 0;
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(() =>
        {
            if (Interlocked.Increment(ref scans) == 2) gate.Wait(TimeSpan.FromSeconds(5));
            return [];
        });
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(1, 2, true, []));
        var module = new ModelSelectionModule(scanner.Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        var selection = Selection();
        await module.InitializeAsync(selection);
        selection.TargetFolder = @"C:\Elsewhere";   // stale, so the panel's first render scans (and blocks)
        RenderPanel(module, selection);
        SpinWait.SpinUntil(() => Volatile.Read(ref scans) == 2, 10_000).Should().BeTrue();

        await module.RefreshAfterResumeAsync();        // the restored page's panel takes over
        await DisposeComponentsAsync();                // this one's page is gone
        gate.Set();

        // Nothing to wait on when it does the right thing; a third scan would start within milliseconds.
        SpinWait.SpinUntil(() => Volatile.Read(ref scans) >= 3, 500);
        scans.Should().Be(2, "the scan in flight finishes; no new one starts");
        estimator.Invocations.Should().BeEmpty("nothing is scheduled on a disposed panel");
    }

    private static WizardSelection ModelsOnlySelection() =>
        new() { Workload = Selection().Workload, Mode = WizardMode.ModelsOnly, TargetFolder = @"E:\AI\models" };

    [Fact]
    public async Task Flipping_only_the_subfolder_switch_rescans()
    {
        // Same folder, same tier: without the layout in the comparison the marks would stay stale.
        var selection = ModelsOnlySelection();
        var scanner = Scanner(vaePresent: true);
        var module = new ModelSelectionModule(scanner.Object, Estimator());
        await module.InitializeAsync(selection);
        var cut = RenderPanel(module, selection);
        cut.WaitForAssertion(() => cut.FindAll(".tag").Should().NotBeEmpty());

        selection.SortModelsIntoSubfolders = false;
        cut.Render();

        cut.WaitForAssertion(() =>
            scanner.Verify(s => s.Scan(It.Is<ModelScanRequest>(r => r.DownloadOnly && !r.SortIntoSubfolders)), Times.AtLeastOnce()));
    }

    [Fact]
    public async Task Models_only_speaks_of_the_download_drive()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(10, 0, true, [], AvailableKnown: false));
        var selection = ModelsOnlySelection();
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, estimator.Object);
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() => cut.Find(".disk-space").TextContent.Should().Contain("free space on the download drive"));
    }

    [Fact]
    public async Task Lists_every_model_ticked_under_its_folder_and_marks_the_one_already_on_disk()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: true).Object, Estimator()) { ScanWait = TimeSpan.FromSeconds(30) };
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
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator()) { ScanWait = TimeSpan.FromSeconds(30) };
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
        var module = new ModelSelectionModule(Scanner(false).Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".disk-space").TextContent.Should().Contain("Needs about").And.Contain("could not read the free space");
            cut.Find(".disk-space").ClassList.Should().NotContain("disk-space-bad");
        });
    }

    [Fact]
    public async Task An_unreadable_library_drive_is_reported_next_to_the_install_drive_figure()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(70, 40, true, [], AvailableKnown: true, LibraryAvailableBytes: null, ModelBytes: 60, HasLibrary: true));
        var selection = Selection();
        var module = new ModelSelectionModule(Scanner(false).Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".disk-space").TextContent;
            text.Should().Contain("free on the install drive").And.Contain("library folder").And.Contain("could not be read");
            cut.Find(".disk-space").ClassList.Should().NotContain("disk-space-bad");
        });
    }

    [Fact]
    public async Task A_tier_moved_while_a_scan_is_running_is_scanned_again_not_dropped()
    {
        // Review finding: a parameter-set arriving mid-scan was discarded and nothing re-checked,
        // so markers and the estimate stayed on the old tier with no further trigger.
        var scanner = new Mock<IModelPresenceScanner>();
        var selection = Selection("8,12,16");
        selection.SelectedVramProfile = 8;
        var scans = 0;
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>()))
            .Returns((ModelScanRequest r) =>
            {
                // The second scan (first rescan) sees the tier move underneath it.
                if (++scans == 2) selection.SelectedVramProfile = 16;
                return [];
            });
        var module = new ModelSelectionModule(scanner.Object, Estimator()) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        var cut = RenderPanel(module, selection);

        selection.SelectedVramProfile = 12;
        cut.Render();

        cut.WaitForAssertion(() => module.LastScannedTier.Should().Be(16));
    }

    [Fact]
    public async Task Shows_the_disk_space_estimate_and_flags_a_shortfall()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator(sufficient: false)) { ScanWait = TimeSpan.FromSeconds(30) };
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
        var module = new ModelSelectionModule(scanner.Object, Estimator()) { ScanWait = TimeSpan.FromSeconds(30) };
        var selection = Selection("8,12,16");
        selection.SelectedVramProfile = 8;
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);
        var scansBefore = scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan));

        selection.SelectedVramProfile = 16;
        cut.Render();

        // The rescan runs off the render thread now, so it lands a beat after the render.
        cut.WaitForAssertion(() =>
        {
            module.LastScannedTier.Should().Be(16);
            scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan)).Should().Be(scansBefore + 1);
        });
    }

    [Fact]
    public async Task A_workload_whose_models_are_all_disabled_says_so()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.ModelDownloads.Add(new ModelDownload { Name = "Off", Enabled = false });
        var selection = new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
        var module = new ModelSelectionModule(Scanner(false).Object, Estimator()) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.Markup.Should().Contain("disabled by its author");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }
}
