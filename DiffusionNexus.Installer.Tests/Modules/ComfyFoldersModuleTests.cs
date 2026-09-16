using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ComfyFoldersModuleTests
{
    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static ComfyFoldersModule Module(string modelFolder = "", string outputFolder = "")
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings
            {
                DefaultModelBaseFolder = modelFolder,
                OutputFolder = outputFolder,
            });
        return new ComfyFoldersModule(repo.Object);
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, false)]
    [InlineData(RepositoryType.A1111, false)]
    [InlineData(RepositoryType.Forge, false)]
    [InlineData(RepositoryType.Fooocus, false)]
    [InlineData(RepositoryType.AceStep, false)]
    public void Applies_to_comfyui_only(RepositoryType type, bool expected)
        => Module().AppliesTo(Selection(type)).Should().Be(expected);

    [Fact]
    public void AI_Toolkit_gets_no_folder_panel_at_all()
    {
        // It used to get the model-folder half of this module, which wrote an
        // extra_model_paths.yaml that ostris/ai-toolkit never reads -- the name does not occur
        // anywhere in that repository. It resolves models through the MODELS_PATH environment
        // variable instead, which nothing in the SDK sets, and the workload declares no model
        // downloads either. The control changed nothing, and a control that changes nothing is
        // worse than no control.
        Module().AppliesTo(Selection(RepositoryType.AIToolkit)).Should().BeFalse();
    }

    [Fact]
    public async Task Seeds_from_remembered_settings()
    {
        var module = Module(modelFolder: @"D:\Models", outputFolder: @"D:\Out");

        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.ModelBaseFolder.Should().Be(@"D:\Models");
        module.OutputFolder.Should().Be(@"D:\Out");
    }

    [Fact]
    public async Task A_model_base_folder_turns_on_extra_model_paths_generation()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = @"D:\Models";

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ModelBaseFolder.Should().Be(@"D:\Models");
        draft.GenerateExtraModelPaths.Should().BeTrue();
    }

    [Fact]
    public async Task No_model_base_folder_leaves_generation_off_and_the_value_null()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ModelBaseFolder.Should().BeNull();
        draft.GenerateExtraModelPaths.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_output_folder_contributes_nothing()
    {
        var module = Module(outputFolder: string.Empty);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.OutputFolder.Should().BeNull("blank means ComfyUI's own output folder, not an empty path");
    }

    // ---- The "use my own model folder" switch (issue #15) ---------------------------------------
    // A remembered library used to be applied on every ComfyUI install, out of sight behind the
    // collapsed Advanced section. Now nothing about model folders reaches the install unless the
    // switch is on; off means ComfyUI's own folders and no extra_model_paths.yaml.

    private static ComfyFoldersModule ModuleWith(UserSettings settings)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        repo.Setup(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        return new ComfyFoldersModule(repo.Object);
    }

    [Fact]
    public async Task The_switch_is_off_unless_the_user_turned_it_on_last_time()
    {
        var off = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models" });
        await off.InitializeAsync(Selection(RepositoryType.ComfyUI));
        off.UseModelLibraryFolder.Should().BeFalse("a remembered folder alone must not switch it on");

        var on = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true });
        await on.InitializeAsync(Selection(RepositoryType.ComfyUI));
        on.UseModelLibraryFolder.Should().BeTrue();
    }

    [Fact]
    public async Task With_the_switch_off_a_remembered_library_reaches_neither_the_install_nor_the_selection()
    {
        var module = ModuleWith(new UserSettings
        {
            DefaultModelBaseFolder = @"D:\Models",
            DefaultLorasFolder = "Lora",
            OutputFolder = @"D:\Out",
            additionalFolders = [new AdditionalFolder { BaseName = "extra", MapsTo = @"G:\Extra" }],
        });
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.ModelBaseFolder.Should().Be(@"D:\Models", "the folder is remembered, just not applied");
        module.HasCustomFolders.Should().BeFalse();
        selection.ModelBaseFolder.Should().BeNull();
        selection.FolderPathOverrides.Should().BeEmpty();

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ModelBaseFolder.Should().BeNull();
        draft.GenerateExtraModelPaths.Should().BeFalse("off means no extra_model_paths.yaml at all");
        draft.FolderPathOverrides.Should().BeEmpty();
        draft.AdditionalFolders.Should().BeEmpty();
        draft.OutputFolder.Should().Be(@"D:\Out", "the output folder is not a model folder and stays outside the switch");
    }

    [Fact]
    public async Task Turning_the_switch_on_applies_the_remembered_library_again()
    {
        var module = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models", DefaultLorasFolder = "Lora" });
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.UseModelLibraryFolder = true;

        module.HasCustomFolders.Should().BeTrue();
        selection.ModelBaseFolder.Should().Be(@"D:\Models");
        selection.FolderPathOverrides.Should().Contain("loras", "Lora");

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ModelBaseFolder.Should().Be(@"D:\Models");
        draft.GenerateExtraModelPaths.Should().BeTrue();
        draft.FolderPathOverrides.Should().Contain("loras", "Lora");
    }

    [Fact]
    public async Task Turning_the_switch_off_again_clears_the_selection_without_forgetting_the_folder()
    {
        var module = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true });
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        selection.ModelBaseFolder.Should().Be(@"D:\Models");

        module.UseModelLibraryFolder = false;

        selection.ModelBaseFolder.Should().BeNull();
        module.ModelBaseFolder.Should().Be(@"D:\Models");
    }

    [Fact]
    public async Task Persist_saves_the_switch_and_keeps_the_remembered_folder_when_it_is_off()
    {
        var stored = new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true };
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseModelLibraryFolder = false;
        await module.PersistAsync();

        stored.UseModelLibraryFolder.Should().BeFalse();
        stored.DefaultModelBaseFolder.Should().Be(@"D:\Models", "off is not forget: turning it back on next time must restore the folder");
    }

    [Fact]
    public async Task Persist_saves_the_switch_when_it_was_turned_on()
    {
        var stored = new UserSettings();
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = @"D:\Models";
        await module.PersistAsync();

        stored.UseModelLibraryFolder.Should().BeTrue();
        stored.DefaultModelBaseFolder.Should().Be(@"D:\Models");
    }
}
