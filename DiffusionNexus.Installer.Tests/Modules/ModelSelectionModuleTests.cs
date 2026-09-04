// DiffusionNexus.Installer.Tests/Modules/ModelSelectionModuleTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ModelSelectionModuleTests
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };
    private static readonly ModelDownload Unet = new() { Name = "UNet", Destination = @"models\unet", Url = "https://h.invalid/unet.gguf" };
    private static readonly ModelDownload Loose = new() { Name = "Loose", Url = "https://h.invalid/loose.bin" };

    private static WizardSelection Selection(params ModelDownload[] models)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.ModelDownloads.AddRange(models);
        return new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
    }

    private static Mock<IModelPresenceScanner> Scanner(params ModelPresence[] presences)
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(presences);
        return scanner;
    }

    private static ModelPresence Present(ModelDownload m, string path) =>
        new(m.Id, true, path, [new ModelFileTarget(m, m.Url, Path.GetDirectoryName(path)!, Path.GetFileName(path), path)]);

    private static ModelPresence Absent(ModelDownload m) =>
        new(m.Id, false, null, [new ModelFileTarget(m, m.Url, @"C:\AI\ComfyUI\models", "x", null)]);

    private static ModelSelectionModule Module(Mock<IModelPresenceScanner>? scanner = null, IDiskSpaceEstimator? estimator = null) =>
        new((scanner ?? Scanner()).Object, estimator ?? Mock.Of<IDiskSpaceEstimator>());

    [Fact]
    public async Task Every_enabled_model_is_a_ticked_row_grouped_by_destination_with_unassigned_last()
    {
        var disabled = new ModelDownload { Name = "Off", Enabled = false };
        var module = Module();

        await module.InitializeAsync(Selection(Unet, Loose, Vae, disabled));

        module.Rows.Select(r => r.Name).Should().Equal("UNet", "Loose", "VAE");
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        module.Groups.Select(g => g.Name).Should().Equal(@"models\unet", @"models\vae", ModelSelectionModule.NotAssignedGroup);
        module.SelectedCount.Should().Be(3);
    }

    [Fact]
    public async Task Applies_whenever_the_workload_declares_models_even_if_all_are_disabled()
    {
        // Must mirror WorkloadCapabilities.Detect (Count > 0), or the gate demands a module that
        // then declines to render. The Enabled filter lives on the rows, not on applicability.
        var module = Module();
        var allDisabled = new ModelDownload { Name = "Off", Enabled = false };

        module.AppliesTo(Selection(allDisabled)).Should().BeTrue();
        module.AppliesTo(Selection()).Should().BeFalse();

        await module.InitializeAsync(Selection(allDisabled));
        module.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Unticked_rows_become_excluded_ids_and_nothing_else_does()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae, Unet));

        module.SetSelected(Unet.Id, false);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ExcludedModelIds.Should().BeEquivalentTo([Unet.Id]);
        module.SelectedCount.Should().Be(1);
        module.Validate().IsValid.Should().BeTrue("installing without some models is a legitimate choice");
    }

    [Fact]
    public async Task Rows_are_marked_from_the_scan_and_the_scan_uses_the_selections_folders_and_tier()
    {
        var scanner = Scanner(Present(Vae, @"D:\Models\vae\ae.safetensors"), Absent(Unet));
        var module = Module(scanner);
        var selection = Selection(Vae, Unet);
        selection.ModelBaseFolder = @"D:\Models";
        selection.FolderPathOverrides = new Dictionary<string, string> { ["loras"] = @"E:\Loras" };
        selection.SelectedVramProfile = 12;

        await module.InitializeAsync(selection);

        module.Rows.Single(r => r.Id == Vae.Id).IsExisting.Should().BeTrue();
        module.Rows.Single(r => r.Id == Vae.Id).ExistingPath.Should().Be(@"D:\Models\vae\ae.safetensors");
        module.Rows.Single(r => r.Id == Unet.Id).IsExisting.Should().BeFalse();
        module.LastScannedTier.Should().Be(12);
        scanner.Verify(s => s.Scan(It.Is<ModelScanRequest>(r =>
            r.RepositoryPath == @"C:\AI\ComfyUI"
            && r.ModelBaseFolder == @"D:\Models"
            && r.FolderPathOverrides.ContainsKey("loras")
            && r.SelectedVramGb == 12)), Times.Once);
    }

    [Fact]
    public async Task No_install_folder_means_no_scan_and_no_markers()
    {
        var scanner = Scanner(Present(Vae, @"C:\x\ae.safetensors"));
        var module = Module(scanner);
        var selection = Selection(Vae);
        selection.TargetFolder = string.Empty;

        await module.InitializeAsync(selection);

        scanner.Verify(s => s.Scan(It.IsAny<ModelScanRequest>()), Times.Never);
        module.Rows.Single().IsExisting.Should().BeFalse();
    }

    [Fact]
    public async Task The_estimate_excludes_unticked_models_and_does_not_count_files_already_on_disk()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        DiskSpaceRequest? seen = null;
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DiskSpaceRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(new DiskSpaceEstimate(10, 20, true, []));
        var module = Module(Scanner(Present(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors"), Absent(Unet)), estimator.Object);
        var selection = Selection(Vae, Unet);
        selection.SelectedVramProfile = 8;
        await module.InitializeAsync(selection);
        module.SetSelected(Unet.Id, false);

        await module.RefreshEstimateAsync();

        module.Estimate!.IsSufficient.Should().BeTrue();
        seen!.TargetFolder.Should().Be(@"C:\AI");
        seen.SelectedVramGb.Should().Be(8);
        seen.ExcludedModelIds.Should().BeEquivalentTo([Unet.Id]);
        seen.ExistingModelIds.Should().BeEquivalentTo([Vae.Id]);
    }

    [Fact]
    public async Task A_failing_estimate_is_reported_not_thrown()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var module = Module(estimator: estimator.Object);
        await module.InitializeAsync(Selection(Vae));

        await module.RefreshEstimateAsync();

        module.Estimate.Should().BeNull();
        module.EstimateError.Should().Contain("offline");
    }

    [Fact]
    public async Task Existing_targets_for_selected_models_drive_the_preflight()
    {
        var module = Module(Scanner(Present(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors"), Present(Unet, @"C:\AI\ComfyUI\models\unet\unet.gguf")));
        await module.InitializeAsync(Selection(Vae, Unet));
        module.SetSelected(Unet.Id, false);

        module.ExistingTargetsForSelectedModels().Select(t => t.Url).Should().Equal([Vae.Url],
            "an unticked model is never downloaded, so its file is never verified");
    }

    [Fact]
    public async Task Verification_decisions_reach_the_draft_keyed_by_url()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae));

        module.ApplyVerification(["https://h.invalid/ae.safetensors"], ["https://h.invalid/other.bin"]);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ForceRedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/ae.safetensors"]);
        draft.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/other.bin"]);
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_starts_clean()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae, Unet));
        module.SetSelected(Unet.Id, false);
        module.ApplyVerification(["https://h.invalid/ae.safetensors"], []);

        await module.InitializeAsync(Selection(Loose));

        module.Rows.Select(r => r.Name).Should().Equal("Loose");
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ExcludedModelIds.Should().BeEmpty();
        draft.ForceRedownloadUrls.Should().BeEmpty();
        module.Estimate.Should().BeNull();
    }
}
