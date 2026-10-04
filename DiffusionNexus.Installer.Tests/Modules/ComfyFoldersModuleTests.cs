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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_output_folder_contributes_nothing_even_with_the_switch_on(string blank)
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;     // past the switch, so the blank guard itself is tested
        module.OutputFolder = blank;

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.OutputFolder.Should().BeNull("an empty --output-directory must never reach the launcher");
    }

    [Fact]
    public async Task Folders_reach_the_install_trimmed()
    {
        // A pasted " D:\Out" is not rooted: ComfyUI would write under its working directory.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = @" D:\Out ";
        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = @" D:\Models ";

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.OutputFolder.Should().Be(@"D:\Out");
        draft.ModelBaseFolder.Should().Be(@"D:\Models");
    }

    [Theory]
    [InlineData(true, "", false)]
    [InlineData(true, "   ", false)]
    [InlineData(true, @"D:\Out", true)]
    [InlineData(false, "", true)]
    public async Task The_output_switch_on_needs_a_folder(bool on, string folder, bool valid)
    {
        // On with an empty box would quietly behave as off -- and be saved as off.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = on;
        module.OutputFolder = folder;

        var result = module.Validate();

        result.IsValid.Should().Be(valid);
        if (!valid) result.ErrorMessage.Should().Contain("output folder");
    }

    // ---- What the launcher can carry (PR #47 review round 2) -----------------------------------
    // BatchScriptGenerator quotes the path only when it holds a space and runs the script under
    // enabledelayedexpansion, so some folders that look fine start ComfyUI somewhere else.

    [Theory]
    [InlineData("Renders")]
    [InlineData(@"Pictures\Comfy")]
    [InlineData("D:Out")]
    public async Task A_relative_output_folder_is_refused(string folder)
    {
        // It would resolve under <install>\ComfyUI -- inside the install, deleted with it.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = folder;

        module.OutputFolderProblem.Should().Contain("full path");
        module.Validate().IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"D:\Art&Out")]
    [InlineData(@"D:\Wow!Renders")]
    [InlineData(@"D:\100%Out")]
    [InlineData(@"D:\A^B")]
    public async Task An_output_folder_the_start_script_would_mangle_is_refused(string folder)
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = folder;

        module.OutputFolderProblem.Should().Contain("cannot");
        module.Validate().IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"D:\My Output\", @"D:\My Output")]
    [InlineData(@"D:\Out/", @"D:\Out")]
    [InlineData(@"D:\", @"D:\")]
    public async Task A_trailing_separator_is_dropped_but_a_drive_root_is_kept(string typed, string expected)
    {
        // "D:\My Output\" quoted becomes "D:\My Output\" -- and \" is a literal quote in argv.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = typed;

        module.Validate().IsValid.Should().BeTrue();
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.OutputFolder.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"D:\Renders|old")]
    [InlineData(@"D:\Out>log")]
    [InlineData(@"D:\In<put")]
    [InlineData(@"D:\Say""Cheese")]
    [InlineData(@"D:\What?")]
    [InlineData(@"D:\St*r")]
    [InlineData(@"D:\Out:Stream")]
    [InlineData("D:\\Tab\tOut")]
    public async Task An_output_folder_Windows_cannot_name_is_refused(string folder)
    {
        // IsPathFullyQualified checks only the shape: "D:\Renders|old" on the launcher line pipes
        // ComfyUI into a command named "old" and it writes to D:\Renders.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = folder;

        module.OutputFolderProblem.Should().Contain("Windows folder name");
        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_path_copied_with_Copy_as_path_is_used_without_its_quotes()
    {
        // Explorer's "Copy as path" wraps the path in quotes; the user did enter the full path.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.UseModelLibraryFolder = true;
        module.OutputFolder = @"""D:\My Output""";
        module.ModelBaseFolder = @" ""D:\My Models\"" ";

        module.Validate().IsValid.Should().BeTrue();
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.OutputFolder.Should().Be(@"D:\My Output");
        draft.ModelBaseFolder.Should().Be(@"D:\My Models");
    }

    [Fact]
    public async Task Null_folders_in_the_settings_file_load_as_empty_and_the_switches_still_work()
    {
        // System.Text.Json loads "OutputFolder": null as null despite the non-nullable property.
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultModelBaseFolder = null!, OutputFolder = null! });
        var module = new ComfyFoldersModule(repo.Object);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseModelLibraryFolder = true;
        module.UseOwnOutputFolder = true;

        module.ModelBaseFolder.Should().BeEmpty();
        module.OutputFolder.Should().BeEmpty();
        module.ModelFolderProblem.Should().StartWith("Choose your model library folder");
        module.OutputFolderProblem.Should().StartWith("Choose an output folder");
    }

    [Theory]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\")]
    public async Task A_network_path_without_a_share_is_not_a_full_path(string folder)
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.UseModelLibraryFolder = true;
        module.OutputFolder = folder;
        module.ModelBaseFolder = folder;

        module.OutputFolderProblem.Should().Contain("full path");
        module.ModelFolderProblem.Should().Contain("full path");

        module.OutputFolder = module.ModelBaseFolder = @"\\nas\share\AI";
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task An_install_folder_pasted_with_quotes_still_guards_the_output_folder()
    {
        // The install folder box cleans a "Copy as path" paste the same way, so the inside-the-
        // install check compares against E:\Installer\9\ComfyUI, not <cwd>\"E:\Installer\9"\ComfyUI.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        var selection = new WizardSelection { Workload = w };
        var installSettings = new Mock<IUserSettingsRepository>();
        installSettings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        var install = new InstallFolderModule(installSettings.Object, new SDK.Services.PreInstallationService());
        await install.InitializeAsync(selection);
        var module = Module();
        await module.InitializeAsync(selection);

        install.TargetFolder = @"""E:\Installer\9""";
        module.UseOwnOutputFolder = true;
        module.OutputFolder = @"E:\Installer\9\ComfyUI\renders";

        selection.TargetFolder.Should().Be(@"E:\Installer\9");
        install.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
        module.OutputFolderProblem.Should().Contain("inside the ComfyUI install");
        module.DefaultOutputFolder.Should().Be(@"E:\Installer\9\ComfyUI\output");
    }

    [Fact]
    public async Task A_pair_of_empty_quotes_is_a_blank_folder()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.OutputFolder = "\"\"";

        module.OutputFolderProblem.Should().StartWith("Choose an output folder");
    }

    [Theory]
    [InlineData(@"D:\AI #2\Models", false)]
    [InlineData(@"D:\Models|old", false)]
    [InlineData(@"D:\AI#2\Models", true)]
    [InlineData(@"D:\Art & Models", true)]
    public async Task A_library_the_model_paths_file_cannot_carry_is_refused(string folder, bool usable)
    {
        // base_path is written unquoted: YAML reads " #2/Models/" as a comment and ComfyUI looks
        // in D:/AI. A # without a space before it is fine, and the launcher never sees the library.
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = folder;

        (module.ModelFolderProblem is null).Should().Be(usable, module.ModelFolderProblem);
    }

    [Theory]
    [InlineData(@"E:\Installer\9\ComfyUI\renders", false)]
    [InlineData(@"E:\Installer\9\comfyui", false)]
    [InlineData(@"E:\Installer\9\renders", true)]
    [InlineData(@"E:\Installer\9\ComfyUI-renders", true)]
    public async Task A_folder_inside_the_install_is_refused_since_it_is_deleted_with_it(string folder, bool usable)
    {
        // The install-folder question wants E:\Installer\9\ComfyUI empty or new, so a reinstall
        // means clearing it -- and a folder inside goes with it. Next to it is fine.
        var module = Module();
        var selection = Selection(RepositoryType.ComfyUI);
        selection.Workload.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(selection);
        selection.TargetFolder = @"E:\Installer\9";
        module.UseOwnOutputFolder = true;
        module.UseModelLibraryFolder = true;
        module.OutputFolder = folder;
        module.ModelBaseFolder = folder;

        (module.OutputFolderProblem is null).Should().Be(usable, module.OutputFolderProblem);
        (module.ModelFolderProblem is null).Should().Be(usable, module.ModelFolderProblem);
        if (!usable) module.OutputFolderProblem.Should().Contain(@"E:\Installer\9\ComfyUI");
    }

    // ---- The model switch on needs a library too (PR #47 review round 2) -----------------------
    // With no library there is no extra_model_paths.yaml, and Persist saves an empty folder, so the
    // switch is off at the next start and any renamed folder types quietly stop applying.

    [Theory]
    [InlineData(true, "", false)]
    [InlineData(true, "   ", false)]
    [InlineData(true, "Models", false)]
    [InlineData(true, @"D:\Models", true)]
    [InlineData(false, "", true)]
    public async Task The_model_switch_on_needs_a_full_library_path(bool on, string folder, bool valid)
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseModelLibraryFolder = on;
        module.ModelBaseFolder = folder;

        (module.ModelFolderProblem is null).Should().Be(valid);
        module.Validate().IsValid.Should().Be(valid);
        if (!valid) module.ModelFolderProblem.Should().Contain("model library");
    }

    [Fact]
    public async Task Each_switch_reports_its_own_problem()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.UseOwnOutputFolder = true;
        module.UseModelLibraryFolder = true;

        module.OutputFolderProblem.Should().NotBeNull();
        module.ModelFolderProblem.Should().NotBeNull();

        module.OutputFolder = @"D:\Out";
        module.OutputFolderProblem.Should().BeNull();
        module.Validate().ErrorMessage.Should().Be(module.ModelFolderProblem);
    }

    [Fact]
    public async Task On_the_Location_stage_an_unanswered_switch_keeps_Next_disabled_until_a_folder_is_typed()
    {
        // The rule only matters through WizardRun: Next reads Validate() for the current stage.
        var module = Module();
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        var plan = await new WizardModuleRegistry(() => [module]).BuildPlanAsync(new WizardSelection { Workload = w });
        var run = new WizardRun(plan);
        run.CurrentStage.Should().Be(WizardStage.Location);
        run.CanGoNext.Should().BeTrue();

        module.UseOwnOutputFolder = true;

        run.CanGoNext.Should().BeFalse();
        run.ValidationErrors.Should().ContainSingle().Which.Should().Contain("output folder");

        module.OutputFolder = @"D:\Out";

        run.CanGoNext.Should().BeTrue();
        run.ValidationErrors.Should().BeEmpty();
    }

    // ---- The "use my own output folder" switch (issue #27) --------------------------------------
    // Same rule as the model folder's: a saved output folder means on, none means off. Off means
    // ComfyUI's own output folder and persists an empty one.

    [Fact]
    public async Task The_output_switch_follows_the_remembered_output_folder()
    {
        var on = Module(outputFolder: @"D:\Out");
        await on.InitializeAsync(Selection(RepositoryType.ComfyUI));
        on.UseOwnOutputFolder.Should().BeTrue("a saved output folder is the user's standing answer");

        var off = Module();
        await off.InitializeAsync(Selection(RepositoryType.ComfyUI));
        off.UseOwnOutputFolder.Should().BeFalse();
    }

    [Fact]
    public async Task With_the_output_switch_off_the_typed_folder_does_not_reach_the_install()
    {
        var module = Module(outputFolder: @"D:\Out");
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseOwnOutputFolder = false;

        module.OutputFolder.Should().Be(@"D:\Out", "kept for this run so flipping back on loses nothing");
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.OutputFolder.Should().BeNull("off means ComfyUI's own output folder");

        module.UseOwnOutputFolder = true;
        module.Contribute(draft);
        draft.OutputFolder.Should().Be(@"D:\Out");
    }

    [Fact]
    public async Task Persist_forgets_the_output_folder_when_its_switch_is_off()
    {
        var stored = new UserSettings { DefaultModelBaseFolder = @"D:\Models", OutputFolder = @"D:\Out" };
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseOwnOutputFolder = false;
        await module.PersistAsync();

        stored.OutputFolder.Should().BeEmpty("a folder left saved would switch it back on at the next start");
        stored.DefaultModelBaseFolder.Should().Be(@"D:\Models", "the model folder has its own switch");
    }

    [Fact]
    public async Task Persist_saves_the_output_folder_when_its_switch_is_on()
    {
        var stored = new UserSettings();
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseOwnOutputFolder = true;
        module.OutputFolder = @" D:\Out ";
        await module.PersistAsync();

        stored.OutputFolder.Should().Be(@"D:\Out");
    }

    // ---- The "use my own model folder" switch (issue #15) ---------------------------------------
    // The switch IS the remembered library: a saved folder means on, none means off. Off means
    // ComfyUI's own folders and no extra_model_paths.yaml, and persists an empty folder -- a
    // folder left saved behind an "off" would switch it back on at the next start.

    private static ComfyFoldersModule ModuleWith(UserSettings settings)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        repo.Setup(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        return new ComfyFoldersModule(repo.Object);
    }

    [Fact]
    public async Task The_switch_follows_the_remembered_library()
    {
        var on = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models" });
        await on.InitializeAsync(Selection(RepositoryType.ComfyUI));
        on.UseModelLibraryFolder.Should().BeTrue("a saved library is the user's standing answer");

        var off = ModuleWith(new UserSettings { DefaultLorasFolder = "Lora" });
        await off.InitializeAsync(Selection(RepositoryType.ComfyUI));
        off.UseModelLibraryFolder.Should().BeFalse("per-type names without a library are not a library");
    }

    [Fact]
    public async Task With_the_switch_off_nothing_about_model_folders_reaches_the_install_or_the_selection()
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

        module.UseModelLibraryFolder = false;

        module.ModelBaseFolder.Should().Be(@"D:\Models", "kept for this run so flipping back on loses nothing");
        module.HasCustomFolders.Should().BeFalse();
        selection.ModelBaseFolder.Should().BeNull();
        selection.FolderPathOverrides.Should().BeEmpty();

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ModelBaseFolder.Should().BeNull();
        draft.GenerateExtraModelPaths.Should().BeFalse("off means no extra_model_paths.yaml at all");
        draft.FolderPathOverrides.Should().BeEmpty();
        draft.AdditionalFolders.Should().BeEmpty();
        draft.OutputFolder.Should().Be(@"D:\Out", "the output folder has its own switch, not this one");
    }

    [Fact]
    public async Task Turning_the_switch_back_on_applies_the_library_again()
    {
        var module = ModuleWith(new UserSettings { DefaultModelBaseFolder = @"D:\Models", DefaultLorasFolder = "Lora" });
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        module.UseModelLibraryFolder = false;
        selection.ModelBaseFolder.Should().BeNull();

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
    public async Task Persist_forgets_the_library_when_the_switch_is_off()
    {
        // The switch derives from the saved folder, so "off" has to save an empty one -- otherwise
        // the next start would find the folder and be on again.
        var stored = new UserSettings { DefaultModelBaseFolder = @"D:\Models", OutputFolder = @"D:\Out" };
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseModelLibraryFolder = false;
        await module.PersistAsync();

        stored.DefaultModelBaseFolder.Should().BeEmpty();
        stored.OutputFolder.Should().Be(@"D:\Out", "the output folder has its own switch");
    }

    [Fact]
    public async Task Persist_saves_the_library_when_the_switch_is_on()
    {
        var stored = new UserSettings();
        var module = ModuleWith(stored);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = @"D:\Models";
        await module.PersistAsync();

        stored.DefaultModelBaseFolder.Should().Be(@"D:\Models");
    }
}
