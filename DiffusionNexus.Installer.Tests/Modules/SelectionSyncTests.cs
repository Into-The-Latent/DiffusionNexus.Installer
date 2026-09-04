using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>
/// The Content stage reads the install folder, model library and per-type overrides from the
/// selection while the user is still on that stage. Contribute only runs at Confirm, so the
/// Location modules must write those answers into the selection as they change.
/// </summary>
public class SelectionSyncTests
{
    private static IUserSettingsRepository Settings(UserSettings? settings = null)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new UserSettings());
        return repo.Object;
    }

    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task The_install_folder_reaches_the_selection_without_Contribute()
    {
        var module = new InstallFolderModule(Settings(), new PreInstallationService());
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.TargetFolder = @"D:\Installs\Krea";

        selection.TargetFolder.Should().Be(@"D:\Installs\Krea");
    }

    [Fact]
    public async Task The_remembered_install_folder_reaches_the_selection_at_initialization()
    {
        var module = new InstallFolderModule(
            Settings(new UserSettings { DefaultTargetInstallFolder = @"D:\Installs" }), new PreInstallationService());
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        selection.TargetFolder.Should().Be(@"D:\Installs");
    }

    [Fact]
    public async Task Model_library_and_overrides_reach_the_selection_for_a_comfy_workload()
    {
        var module = new ComfyFoldersModule(Settings(new UserSettings
        {
            DefaultModelBaseFolder = @"D:\Models",
            DefaultLorasFolder = @"E:\Loras",
        }));
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        selection.ModelBaseFolder.Should().Be(@"D:\Models");
        selection.FolderPathOverrides.Should().ContainKey("loras").WhoseValue.Should().Be(@"E:\Loras");
    }

    [Fact]
    public async Task Resetting_the_folder_types_to_standard_empties_the_selection_overrides()
    {
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultLorasFolder = "Lora" }));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        selection.FolderPathOverrides.Should().NotBeEmpty();

        module.ResetFolderTypesToStandard();

        selection.FolderPathOverrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_the_model_library_yields_null_not_empty_string()
    {
        // ModelDestinationResolver treats null and "" the same, but the SDK options record
        // documents null as "no custom library"; the selection follows the record.
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.ModelBaseFolder = "   ";

        selection.ModelBaseFolder.Should().BeNull();
    }

    [Fact]
    public async Task A_workload_the_folders_module_does_not_apply_to_gets_no_library_in_its_selection()
    {
        // The registry initializes EVERY module, applicable or not. A saved library must not leak
        // into a Fooocus selection: the pipeline will never use it there, and a scan against it
        // would mark models "already downloaded" in a folder the install never reads.
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));
        var selection = Selection(RepositoryType.Fooocus);

        await module.InitializeAsync(selection);

        selection.ModelBaseFolder.Should().BeNull();
        selection.FolderPathOverrides.Should().BeEmpty();
    }
}
