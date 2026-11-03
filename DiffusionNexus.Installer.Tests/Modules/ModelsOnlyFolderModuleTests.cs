using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ModelsOnlyFolderModuleTests
{
    private static InstallationConfiguration Workload()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.ModelDownloads =
        [
            new ModelDownload { Name = "The VAE", Destination = "models/vae", Enabled = true, Url = "https://example.com/a.safetensors" },
            new ModelDownload { Name = "Switched off", Destination = "models/loras", Enabled = false, Url = "https://example.com/b.safetensors" },
        ];
        return w;
    }

    private static WizardSelection Selection() => new() { Workload = Workload(), Mode = WizardMode.ModelsOnly };

    private static async Task<(ModelsOnlyFolderModule Module, WizardSelection Selection)> Module()
    {
        var selection = Selection();
        var module = new ModelsOnlyFolderModule();
        await module.InitializeAsync(selection);
        return (module, selection);
    }

    [Fact]
    public async Task Starts_empty_and_sorted()
    {
        var (module, selection) = await Module();

        module.Folder.Should().BeEmpty();
        module.SortIntoSubfolders.Should().BeTrue();
        selection.SortModelsIntoSubfolders.Should().BeTrue();
        module.Validate().ErrorMessage.Should().Be("Choose a folder to download into.");
    }

    [Fact]
    public async Task Any_full_path_is_valid()
    {
        var (module, _) = await Module();

        // Not a ComfyUI, not there yet, and on a drive that may not exist: the user is in charge.
        module.Folder = @"Q:\Somewhere\Not\Yet\Made";

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Relative_path_is_refused()
    {
        var (module, _) = await Module();

        module.Folder = @"AI\models";

        module.Validate().IsValid.Should().BeFalse();
        module.Validate().ErrorMessage.Should().Contain(@"D:\AI\models");
    }

    [Fact]
    public async Task Pasted_quotes_are_cleaned()
    {
        var (module, selection) = await Module();

        module.Folder = "\"E:\\AI\\models\\\"";

        selection.TargetFolder.Should().Be(@"E:\AI\models");
    }

    [Fact]
    public async Task Switch_reaches_selection_and_options()
    {
        var (module, selection) = await Module();
        var draft = new InstallationOptionsDraft();

        module.SortIntoSubfolders = false;
        module.Contribute(draft);

        selection.SortModelsIntoSubfolders.Should().BeFalse();
        draft.SortModelsIntoSubfolders.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_is_empty_until_a_usable_folder_is_chosen()
    {
        var (module, _) = await Module();

        module.Preview.Should().BeEmpty();

        module.Folder = @"AI\models";
        module.Preview.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_sorted_names_the_subfolder_of_each_enabled_model()
    {
        var (module, _) = await Module();

        module.Folder = @"E:\AI\models";

        module.Preview.Should().Equal(new ModelsOnlyPreviewRow("vae", "The VAE"));
    }

    [Fact]
    public async Task Preview_flat_has_no_subfolder()
    {
        var (module, _) = await Module();

        module.Folder = @"E:\AI\models";
        module.SortIntoSubfolders = false;

        module.Preview.Should().Equal(new ModelsOnlyPreviewRow(string.Empty, "The VAE"));
    }

    /// <summary>
    /// Every real module, wired to strict mocks: a fresh-install module initialized on this path
    /// would read the settings or probe the machine, and fail here. This is also what proves nothing
    /// on this path writes the remembered install or library folder of the next fresh install.
    /// </summary>
    private static WizardModuleRegistry RealRegistry(bool strict = true)
    {
        var behaviour = strict ? MockBehavior.Strict : MockBehavior.Loose;
        var settings = new Mock<IUserSettingsRepository>(behaviour).Object;
        return new WizardModuleRegistry(() =>
        [
            new InstallFolderModule(settings, Mock.Of<IPreInstallationService>()),
            new ComfyFoldersModule(settings),
            new ModelsOnlyFolderModule(),
            new VramProfileModule(),
            new ModelSelectionModule(Mock.Of<IModelPresenceScanner>(), Mock.Of<IDiskSpaceEstimator>()),
            new WorkflowSelectionModule(),
            new GpuPreflightModule(new Mock<IGpuDetectionService>(behaviour).Object),
            new VcRuntimeModule(new Mock<IVcRuntimeDetectionService>(behaviour).Object),
            new LlamaCppModule(new Mock<IWorkloadSource>(behaviour).Object),
            new ShortcutsModule(),
            new DisclaimerModule(),
        ]);
    }

    [Fact]
    public async Task Models_only_plan_opens_on_location_with_only_its_modules()
    {
        var workload = Workload();
        workload.Vram.VramProfiles = "8,16";
        workload.Workflows = [new ComfUIWorkflow { Name = "workflow" }];
        workload.GitRepositories = [new GitRepository { Url = "https://example.com/node.git" }];

        var plan = await RealRegistry().BuildPlanAsync(new WizardSelection { Workload = workload, Mode = WizardMode.ModelsOnly });

        plan.Stages.Should().Equal(WizardStage.Location, WizardStage.Content, WizardStage.Confirm, WizardStage.Install);
        plan.AllModules.Select(m => m.Id).Should().BeEquivalentTo("models-only-folder", "vram-profile", "model-selection", "disclaimer");
        plan.Selection.TargetFolder.Should().BeEmpty("the remembered install folder is not this path's folder");
    }

    [Fact]
    public void Absent_from_a_fresh_install()
    {
        new ModelsOnlyFolderModule().UsedIn(WizardMode.FreshInstall).Should().BeFalse();
    }
}
