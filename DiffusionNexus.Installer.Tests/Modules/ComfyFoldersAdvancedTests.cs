using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>
/// The "Advanced: custom model folders" section of the folders page. It replaces the old
/// "Use my N saved model folders" switch: the per-type folder names and the additional folders are
/// shown and editable, applied to the install, and saved back to settings like the classic 1.x
/// Folder Settings window did.
/// </summary>
public class ComfyFoldersAdvancedTests
{
    private static WizardSelection Selection(RepositoryType type = RepositoryType.ComfyUI)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static (ComfyFoldersModule Module, Mock<IUserSettingsRepository> Repo) Module(UserSettings? settings = null)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new UserSettings());
        repo.Setup(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        return (new ComfyFoldersModule(repo.Object), repo);
    }

    [Fact]
    public async Task Folder_types_are_offered_in_the_classic_order_with_their_standard_names()
    {
        var (module, _) = Module();
        await module.InitializeAsync(Selection());

        module.FolderTypes.Should().HaveCount(21);
        module.FolderTypes[0].Key.Should().Be("checkpoints");
        module.FolderTypes[0].Label.Should().Be("Checkpoints");
        module.FolderTypes[0].Standard.Should().Be("checkpoints");
        module.FolderTypes[0].Value.Should().Be("checkpoints", "an unsaved type shows the standard name");
        module.FolderTypes.Select(t => t.Key).Should().StartWith(["checkpoints", "diffusion_models", "loras", "vae"]);
    }

    [Fact]
    public async Task A_saved_custom_name_prefills_its_row_and_counts_as_an_override()
    {
        var (module, _) = Module(new UserSettings { DefaultLorasFolder = "Lora" });
        await module.InitializeAsync(Selection());

        module.FolderTypes.Single(t => t.Key == "loras").Value.Should().Be("Lora");
        module.FolderPathOverrides.Should().Equal(new Dictionary<string, string> { ["loras"] = "Lora" });
        module.HasCustomFolders.Should().BeTrue();
    }

    [Fact]
    public async Task A_saved_value_equal_to_the_standard_name_is_not_an_override()
    {
        // The old switch counted this as one of the "9 saved folders" although it changes nothing.
        var (module, _) = Module(new UserSettings { DefaultCheckpointsFolder = "checkpoints" });
        await module.InitializeAsync(Selection());

        module.FolderPathOverrides.Should().BeEmpty();
        module.HasCustomFolders.Should().BeFalse();
    }

    [Fact]
    public async Task Editing_a_folder_type_updates_the_overrides_and_the_selection()
    {
        var (module, _) = Module();
        var selection = Selection();
        await module.InitializeAsync(selection);

        module.SetFolderType("loras", "MyLoras");

        module.FolderPathOverrides.Should().Contain("loras", "MyLoras");
        selection.FolderPathOverrides.Should().Contain("loras", "MyLoras");
    }

    [Fact]
    public async Task Blanking_a_folder_type_falls_back_to_the_standard_name()
    {
        var (module, _) = Module(new UserSettings { DefaultLorasFolder = "Lora" });
        var selection = Selection();
        await module.InitializeAsync(selection);

        module.SetFolderType("loras", "  ");

        module.FolderPathOverrides.Should().BeEmpty();
        selection.FolderPathOverrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_to_standard_clears_every_override()
    {
        var (module, _) = Module(new UserSettings { DefaultLorasFolder = "Lora", DefaultVAEFolder = "VAEs" });
        var selection = Selection();
        await module.InitializeAsync(selection);

        module.ResetFolderTypesToStandard();

        module.FolderTypes.Should().OnlyContain(t => t.Value == t.Standard);
        module.FolderPathOverrides.Should().BeEmpty();
        selection.FolderPathOverrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Added_additional_folders_reach_the_options_and_blank_rows_are_dropped()
    {
        var (module, _) = Module();
        await module.InitializeAsync(Selection());

        var row = module.AddAdditionalFolder();
        row.BaseName = "extra";
        row.MapsTo = @"G:\Extra";
        module.AddAdditionalFolder(); // left blank

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.AdditionalFolders.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { BaseName = "extra", MapsTo = @"G:\Extra" });
        module.HasCustomFolders.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_an_additional_folder_takes_it_out_of_the_options()
    {
        var (module, _) = Module(new UserSettings
        {
            additionalFolders = [new AdditionalFolder { BaseName = "extra", MapsTo = @"G:\Extra" }],
        });
        await module.InitializeAsync(Selection());
        var row = module.AdditionalFolders.Single();

        module.RemoveAdditionalFolder(row);

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        module.AdditionalFolders.Should().BeEmpty();
        draft.AdditionalFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task The_default_models_and_output_folders_follow_the_install_folder()
    {
        // Shown as grey placeholder text: an empty box means "inside the install", and this is
        // where the SDK actually puts them (ModelDestinationResolver / ComfyUI's own default).
        var (module, _) = Module();
        var selection = Selection();
        selection.Workload.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(selection);

        selection.TargetFolder = @"E:\Installer\9";

        module.DefaultModelsFolder.Should().Be(@"E:\Installer\9\ComfyUI\models");
        module.DefaultOutputFolder.Should().Be(@"E:\Installer\9\ComfyUI\output");
    }

    [Fact]
    public async Task The_defaults_are_empty_until_an_install_folder_is_chosen()
    {
        var (module, _) = Module();
        await module.InitializeAsync(Selection());

        module.DefaultModelsFolder.Should().BeEmpty();
        module.DefaultOutputFolder.Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_folder_counts_as_custom_because_it_now_lives_in_the_advanced_section()
    {
        var (module, _) = Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models" });
        await module.InitializeAsync(Selection());

        module.HasCustomFolders.Should().BeTrue("a saved library applied out of sight must still be flagged");
    }

    [Fact]
    public async Task Persist_writes_the_folders_back_to_settings_without_touching_other_fields()
    {
        var stored = new UserSettings
        {
            UserName = "chris",
            DefaultTargetInstallFolder = @"C:\AI",
            DefaultLorasFolder = "Lora",
            DefaultLoraFolder = "Lora",
        };
        var (module, repo) = Module(stored);
        await module.InitializeAsync(Selection());
        module.ModelBaseFolder = @"D:\Models";
        module.OutputFolder = @"D:\Out";
        module.SetFolderType("loras", "MyLoras");
        module.SetFolderType("checkpoints", "checkpoints");
        var extra = module.AddAdditionalFolder();
        extra.BaseName = "extra";
        extra.MapsTo = @"G:\Extra";

        await module.PersistAsync();

        repo.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Once);
        var saved = (UserSettings)repo.Invocations.Single(i => i.Method.Name == nameof(IUserSettingsRepository.SaveAsync)).Arguments[0];
        saved.UserName.Should().Be("chris");
        saved.DefaultTargetInstallFolder.Should().Be(@"C:\AI", "the install folder is not this page's to change");
        saved.DefaultModelBaseFolder.Should().Be(@"D:\Models");
        saved.OutputFolder.Should().Be(@"D:\Out");
        saved.DefaultLorasFolder.Should().Be("MyLoras");
        saved.DefaultLoraFolder.Should().Be("MyLoras", "the 1.x apps still read the legacy singular field");
        saved.DefaultCheckpointsFolder.Should().BeEmpty("a standard name is stored as blank so settings stay clean");
        saved.additionalFolders.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { BaseName = "extra", MapsTo = @"G:\Extra" });
    }

    [Fact]
    public async Task Persist_leaves_per_type_folders_alone_when_the_advanced_section_was_never_edited()
    {
        // Review finding: a returning 1.x user with "checkpoints"/"loras" stored (standard names,
        // hence not overrides) pressed Next without opening Advanced and had both fields blanked.
        var stored = new UserSettings
        {
            DefaultCheckpointsFolder = "checkpoints",
            DefaultLoraFolder = "loras",
            additionalFolders = [new AdditionalFolder { BaseName = "extra", MapsTo = @"G:\Extra" }],
        };
        var (module, repo) = Module(stored);
        await module.InitializeAsync(Selection());
        module.ModelBaseFolder = @"D:\Models";

        await module.PersistAsync();

        repo.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Once);
        stored.DefaultModelBaseFolder.Should().Be(@"D:\Models", "the visible answer is always saved");
        stored.DefaultCheckpointsFolder.Should().Be("checkpoints");
        stored.DefaultLoraFolder.Should().Be("loras");
        stored.additionalFolders.Should().ContainSingle();
    }

    [Fact]
    public async Task Persist_rereads_the_settings_so_it_never_clobbers_what_another_module_just_saved()
    {
        // Two Location modules save in a row. Each must start from the file as it is NOW, not
        // from the copy it loaded at initialization, or the second save undoes the first.
        var atInit = new UserSettings { DefaultTargetInstallFolder = @"C:\Old" };
        var atPersist = new UserSettings { DefaultTargetInstallFolder = @"C:\JustSavedByInstallFolderModule" };
        var repo = new Mock<IUserSettingsRepository>();
        repo.SetupSequence(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(atInit)
            .ReturnsAsync(atPersist);
        repo.Setup(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        var module = new ComfyFoldersModule(repo.Object);
        await module.InitializeAsync(Selection());
        module.ModelBaseFolder = @"D:\Models";

        await module.PersistAsync();

        repo.Verify(r => r.SaveAsync(atPersist, It.IsAny<CancellationToken>()), Times.Once);
        atPersist.DefaultTargetInstallFolder.Should().Be(@"C:\JustSavedByInstallFolderModule");
        atPersist.DefaultModelBaseFolder.Should().Be(@"D:\Models");
    }

    [Fact]
    public async Task Persist_does_nothing_for_a_workload_the_module_does_not_apply_to()
    {
        // The registry initializes every module. A Fooocus install must not rewrite the ComfyUI
        // folder settings with whatever this module was seeded with.
        var (module, repo) = Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models" });
        await module.InitializeAsync(Selection(RepositoryType.Fooocus));

        await module.PersistAsync();

        repo.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
