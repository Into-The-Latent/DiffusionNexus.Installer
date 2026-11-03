using Bunit;
using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The output and model folder panels, filled in from an installed ComfyUI.</summary>
public class ComfyFoldersUpdatePathTests : BunitContext
{
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly UserSettings _user = new() { DefaultModelBaseFolder = @"E:\FreshLibrary", OutputFolder = @"E:\FreshOutput" };

    public ComfyFoldersUpdatePathTests()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_user);
    }

    private static ExistingComfyUIProfile Profile(string? output = @"E:\Out", string? models = @"E:\Lib", string folder = @"E:\ComfyUI") =>
        new(folder, ExistingInstallSupport.Supported, folder, folder + @"\venv", "3.12.7", OutputFolder: output, ModelBaseFolder: models);

    private async Task<(ComfyFoldersModule Module, WizardSelection Selection)> ModuleAsync(ExistingComfyUIProfile? profile = null)
    {
        var workload = new InstallationConfiguration { Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI } };
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        var module = new ComfyFoldersModule(_settings.Object);
        await module.InitializeAsync(selection);
        selection.Existing.Profile = profile ?? Profile();
        return (module, selection);
    }

    [Fact]
    public async Task The_panels_start_from_the_install_not_from_a_fresh_installs_defaults()
    {
        var (module, selection) = await ModuleAsync();

        module.UseOwnOutputFolder.Should().BeTrue();
        module.OutputFolder.Should().Be(@"E:\Out");
        module.UseModelLibraryFolder.Should().BeTrue();
        module.ModelBaseFolder.Should().Be(@"E:\Lib");
        selection.ModelBaseFolder.Should().Be(@"E:\Lib");
    }

    [Fact]
    public async Task An_install_with_neither_starts_with_both_switches_off()
    {
        var (module, selection) = await ModuleAsync(Profile(output: null, models: null));

        module.UseOwnOutputFolder.Should().BeFalse();
        module.UseModelLibraryFolder.Should().BeFalse();
        selection.ModelBaseFolder.Should().BeNull();
    }

    [Fact]
    public async Task Before_a_folder_is_chosen_nothing_is_on()
    {
        var workload = new InstallationConfiguration { Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI } };
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        var module = new ComfyFoldersModule(_settings.Object);

        await module.InitializeAsync(selection);

        module.UseModelLibraryFolder.Should().BeFalse();
        selection.ModelBaseFolder.Should().BeNull();
    }

    [Fact]
    public async Task Another_install_fills_them_in_again()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\Typed";

        selection.Existing!.Profile = Profile(output: @"E:\Other", models: null, folder: @"E:\Second");

        module.OutputFolder.Should().Be(@"E:\Other");
        module.UseModelLibraryFolder.Should().BeFalse();
    }

    [Fact]
    public async Task Unticking_the_update_does_not_undo_what_was_typed()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\Typed";

        selection.Existing!.RunUpdate = false;

        module.OutputFolder.Should().Be(@"E:\Typed");
    }

    [Theory]
    [InlineData(@"E:\Out")]
    [InlineData(@"e:\out\")]
    [InlineData(@"E:\Out\")]
    public async Task The_same_folder_written_another_way_does_not_differ(string typed)
    {
        var (module, _) = await ModuleAsync();

        module.OutputFolder = typed;

        module.OutputFolderDiffers.Should().BeFalse();
    }

    [Fact]
    public async Task The_installs_own_type_folders_decide_where_models_go()
    {
        var (module, selection) = await ModuleAsync(Profile() with
        {
            ModelFolderMappings = new Dictionary<string, string> { ["loras"] = "Lora", ["checkpoints"] = "models/checkpoints" },
        });

        selection.FolderPathOverrides.Should().Contain("loras", "Lora").And.Contain("checkpoints", "models/checkpoints");
        module.ModelFolderDiffers.Should().BeFalse("reading them changes nothing in the file");
    }

    [Fact]
    public async Task Changing_a_type_folder_rewrites_the_yaml_even_with_the_same_base()
    {
        var (module, _) = await ModuleAsync();

        module.SetFolderType("loras", "MyLoras");

        module.ModelFolderDiffers.Should().BeTrue();
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.GenerateExtraModelPaths.Should().BeTrue();
    }

    [Theory]
    [InlineData("%~dp0renders")]
    [InlineData("output2")]
    public async Task An_output_folder_that_is_already_working_does_not_block_next(string folder)
    {
        var (module, _) = await ModuleAsync(Profile(output: folder));

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_folder_is_still_checked()
    {
        var (module, _) = await ModuleAsync(Profile(output: "output2"));

        module.OutputFolder = "output3";

        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_recheck_with_an_equal_answer_keeps_what_was_typed()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\Typed";

        selection.Existing!.GpuUsable = false;

        module.OutputFolder.Should().Be(@"E:\Typed");
    }

    private static InstallationConfiguration WithModel(string destination)
    {
        var workload = new InstallationConfiguration { Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI } };
        workload.ModelDownloads.Add(new SDK.Models.Entities.ModelDownload { Name = "m", Url = "https://h/m.safetensors", Destination = destination });
        return workload;
    }

    private async Task<ComfyFoldersModule> WithWorkloadAsync(InstallationConfiguration workload, ExistingComfyUIProfile profile)
    {
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        var module = new ComfyFoldersModule(_settings.Object);
        await module.InitializeAsync(selection);
        selection.Existing.Profile = profile;
        return module;
    }

    [Fact]
    public async Task A_type_the_yaml_does_not_list_warns_and_leaves_the_file_alone()
    {
        var module = await WithWorkloadAsync(WithModel("models/vae"), Profile() with
        {
            ModelFolderMappings = new Dictionary<string, string> { ["checkpoints"] = "checkpoints" },
        });
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        module.MissingModelTypes.Should().Equal("vae");
        draft.GenerateExtraModelPaths.Should().BeFalse("rewriting would drop whatever else the file holds");
        Panel(module).Find("[data-role=model-missing-types]").TextContent.Should()
            .Be("Your extra_model_paths.yaml doesn't list vae, so ComfyUI won't find these models there until you add it. We leave that file as it is.");
    }

    [Theory]
    [InlineData("models/diffusion_models", "unet")]
    [InlineData("models/text_encoders", "clip")]
    [InlineData("models/clip", "text_encoders")]
    public async Task ComfyUIs_other_name_for_a_type_counts_as_listing_it(string destination, string yamlKey)
    {
        var module = await WithWorkloadAsync(WithModel(destination), Profile() with
        {
            ModelFolderMappings = new Dictionary<string, string> { [yamlKey] = "models/" + yamlKey },
        });

        module.MissingModelTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_model_goes_where_the_yaml_says_under_its_other_name()
    {
        var (_, selection) = await ModuleAsync(Profile() with
        {
            ModelFolderMappings = new Dictionary<string, string> { ["unet"] = "models/unet" },
        });

        selection.FolderPathOverrides.Should().Contain("diffusion_models", "models/unet");
    }

    [Fact]
    public async Task A_type_the_yaml_lists_needs_nothing()
    {
        var module = await WithWorkloadAsync(WithModel("models/vae"), Profile() with
        {
            ModelFolderMappings = new Dictionary<string, string> { ["vae"] = "vae" },
        });

        module.MissingModelTypes.Should().BeEmpty();
        module.ModelFolderDiffers.Should().BeFalse();
    }

    [Fact]
    public async Task A_yaml_with_no_comfyui_block_is_not_said_to_be_added()
    {
        var folder = Directory.CreateTempSubdirectory("dn-yaml-").FullName;
        File.WriteAllText(Path.Combine(folder, "extra_model_paths.yaml"), "a111:\n  base_path: x\n");
        var (module, _) = await ModuleAsync(Profile(models: null, folder: folder));
        module.UseModelLibraryFolder = true;
        module.ModelBaseFolder = @"E:\NewLib";

        Panel(module).Find("[data-role=model-differs]").TextContent.Should()
            .Be("Your ComfyUI has an extra_model_paths.yaml. Continuing rewrites that file, and anything else in it stops applying; a backup is kept.");

        Directory.Delete(module.InstallFolder, recursive: true);
    }

    [Fact]
    public async Task Unchanged_folders_write_nothing()
    {
        var (module, _) = await ModuleAsync();
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.GenerateExtraModelPaths.Should().BeFalse();
        draft.OverwriteExtraModelPaths.Should().BeFalse();
        draft.ChangeOutputFolder.Should().BeFalse();
        draft.ModelBaseFolder.Should().Be(@"E:\Lib", "models still have to land in the library");
    }

    [Fact]
    public async Task A_changed_model_folder_rewrites_the_yaml()
    {
        var (module, _) = await ModuleAsync();
        module.ModelBaseFolder = @"E:\NewLib";
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.GenerateExtraModelPaths.Should().BeTrue();
        draft.OverwriteExtraModelPaths.Should().BeTrue();
    }

    [Fact]
    public async Task A_changed_output_folder_edits_the_launcher()
    {
        var (module, _) = await ModuleAsync();
        module.OutputFolder = @"E:\NewOut";
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.ChangeOutputFolder.Should().BeTrue();
        draft.OutputFolder.Should().Be(@"E:\NewOut");
    }

    [Fact]
    public async Task Turning_the_output_switch_off_takes_the_flag_out()
    {
        var (module, _) = await ModuleAsync();
        module.UseOwnOutputFolder = false;
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.ChangeOutputFolder.Should().BeTrue();
        draft.OutputFolder.Should().BeNull();
    }

    [Fact]
    public async Task An_output_folder_inside_comfyui_is_fine_here()
    {
        var (module, _) = await ModuleAsync(Profile(output: @"E:\ComfyUI\my-output"));

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_is_remembered_for_a_fresh_install()
    {
        var (module, _) = await ModuleAsync();
        module.OutputFolder = @"E:\Typed";

        await module.PersistAsync();

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _user.OutputFolder.Should().Be(@"E:\FreshOutput");
    }

    [Fact]
    public async Task The_default_folders_are_the_installs_own()
    {
        var (module, _) = await ModuleAsync(Profile(folder: @"E:\MyComfy"));

        module.DefaultOutputFolder.Should().Be(@"E:\MyComfy\output");
    }

    private IRenderedComponent<ComfyFoldersPanel> Panel(ComfyFoldersModule module) =>
        Render<ComfyFoldersPanel>(p => p.Add(x => x.Module, module));

    [Fact]
    public async Task No_red_box_while_the_folders_are_the_installs()
    {
        var (module, _) = await ModuleAsync();

        var cut = Panel(module);

        cut.FindAll("[data-role=output-differs]").Should().BeEmpty();
        cut.FindAll("[data-role=model-differs]").Should().BeEmpty();
    }

    [Fact]
    public async Task A_changed_output_folder_says_what_it_replaces()
    {
        var (module, _) = await ModuleAsync();
        module.OutputFolder = @"E:\NewOut";

        Panel(module).Find("[data-role=output-differs]").TextContent
            .Should().Be(@"Your ComfyUI currently sends output to E:\Out. Continuing changes that in run_nvidia.bat.");
    }

    [Fact]
    public async Task A_changed_model_folder_says_what_it_replaces()
    {
        var (module, _) = await ModuleAsync();
        module.ModelBaseFolder = @"E:\NewLib";

        Panel(module).Find("[data-role=model-differs]").TextContent
            .Should().Be(@"Your ComfyUI currently loads models from E:\Lib (extra_model_paths.yaml). Continuing rewrites that file, and anything else in it stops applying; a backup is kept.");
    }

    [Fact]
    public async Task Turning_the_library_off_says_the_file_stays()
    {
        var (module, _) = await ModuleAsync();
        module.UseModelLibraryFolder = false;

        Panel(module).Find("[data-role=model-differs]").TextContent
            .Should().Contain("That file stays");
    }

    [Fact]
    public async Task The_overwrite_choice_is_not_offered_here_because_the_run_decides_it()
    {
        var (module, _) = await ModuleAsync();

        Panel(module).FindAll("[data-role=overwrite-yaml]").Should().BeEmpty();
    }

    [Fact]
    public async Task Checking_the_same_install_again_keeps_what_was_typed()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\TypedOut";
        module.ModelBaseFolder = @"E:\TypedLib";

        selection.Existing!.Profile = Profile();

        module.OutputFolder.Should().Be(@"E:\TypedOut");
        module.ModelBaseFolder.Should().Be(@"E:\TypedLib");
    }

    [Fact]
    public async Task Another_install_starts_from_its_own_folders()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\TypedOut";

        selection.Existing!.Profile = Profile(output: @"F:\TheirOut", folder: @"F:\ComfyUI");

        module.OutputFolder.Should().Be(@"F:\TheirOut");
    }

    [Fact]
    public async Task Another_install_does_not_inherit_an_additional_folder()
    {
        var (module, selection) = await ModuleAsync();
        var row = module.AddAdditionalFolder();
        row.BaseName = "extra";
        row.MapsTo = @"E:\Extra";

        selection.Existing!.Profile = Profile(folder: @"F:\ComfyUI", models: @"F:\Lib");

        module.AdditionalFolders.Should().BeEmpty();
        module.ModelFolderDiffers.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_with_a_space_and_hash_is_refused_once_the_file_gets_rewritten()
    {
        var (module, _) = await ModuleAsync(Profile(models: @"D:\AI #2\Models"));
        module.ModelFolderProblem.Should().BeNull("nothing rewrites the file yet");

        module.SetFolderType("loras", "Lora");

        module.ModelFolderProblem.Should().Contain("space followed by #");
    }

    [Fact]
    public async Task Until_the_folder_is_checked_there_is_nothing_to_edit()
    {
        var (module, selection) = await ModuleAsync();

        selection.Existing!.Profile = null;
        var cut = Panel(module);

        cut.FindAll("[data-role=output-panel]").Should().BeEmpty();
        cut.FindAll("[data-role=model-panel]").Should().BeEmpty();
        cut.Find("[data-role=folders-wait]").TextContent.Should().Contain("once its folder is checked");
    }

    [Fact]
    public async Task A_folder_we_cannot_update_has_nothing_to_edit_either()
    {
        var (module, selection) = await ModuleAsync();

        selection.Existing!.Profile = new ExistingComfyUIProfile(@"E:\Empty", ExistingInstallSupport.NotComfyUI);

        Panel(module).Find("[data-role=folders-wait]");
    }

    [Fact]
    public async Task On_with_a_blank_box_is_refused_even_when_the_install_has_none()
    {
        var (module, _) = await ModuleAsync(Profile(output: null, models: null));

        module.UseOwnOutputFolder = true;
        module.OutputFolderProblem.Should().Contain("Choose an output folder");

        module.UseOwnOutputFolder = false;
        module.UseModelLibraryFolder = true;
        module.ModelFolderProblem.Should().Contain("Choose your model library folder");
    }

    [Fact]
    public async Task The_run_knows_what_this_page_will_change()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\NewOut";
        module.ModelBaseFolder = @"E:\NewLib";

        selection.Existing!.FolderChangeLines.Should().HaveCount(2);
        selection.Existing.FolderChangeLines.Should().Contain(l => l.Contains(@"E:\NewOut") && l.Contains("run_nvidia.bat"));
        selection.Existing.FolderChangeLines.Should().Contain(l => l.Contains(@"E:\NewLib") && l.Contains("extra_model_paths.yaml"));
    }

    [Fact]
    public async Task Turning_off_the_output_folder_is_a_change_and_turning_off_the_library_is_not()
    {
        var (module, selection) = await ModuleAsync();
        module.UseOwnOutputFolder = false;
        module.UseModelLibraryFolder = false;

        // The yaml stays when the library goes off, so nothing in the install changes for it.
        selection.Existing!.FolderChangeLines.Should().ContainSingle().Which.Should().Contain("ComfyUI's own");
    }

    [Fact]
    public async Task Unchanged_folders_are_not_listed()
    {
        var (_, selection) = await ModuleAsync();

        selection.Existing!.FolderChangeLines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_changed_during_a_side_trip_is_followed_when_nothing_was_typed()
    {
        var (module, selection) = await ModuleAsync();

        selection.Existing!.Profile = Profile(output: @"E:\EditedOut", models: @"E:\EditedLib");

        module.OutputFolder.Should().Be(@"E:\EditedOut");
        module.ModelBaseFolder.Should().Be(@"E:\EditedLib");
        module.OutputFolderDiffers.Should().BeFalse();
        module.ModelFolderDiffers.Should().BeFalse("nobody asked for that file to be rewritten back");
        selection.Existing.FolderChangeLines.Should().BeEmpty();
    }

    [Fact]
    public async Task What_was_typed_stays_when_the_other_folder_follows_the_file()
    {
        var (module, selection) = await ModuleAsync();
        module.OutputFolder = @"E:\Typed";

        selection.Existing!.Profile = Profile(output: @"E:\EditedOut", models: @"E:\EditedLib");

        module.OutputFolder.Should().Be(@"E:\Typed");
        module.ModelBaseFolder.Should().Be(@"E:\EditedLib");
        selection.Existing.FolderChangeLines.Should().ContainSingle().Which.Should().Contain(@"E:\Typed");
    }

    [Fact]
    public async Task A_fresh_install_has_no_red_boxes()
    {
        var workload = new InstallationConfiguration { Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI } };
        var module = new ComfyFoldersModule(_settings.Object);
        await module.InitializeAsync(new WizardSelection { Workload = workload, TargetFolder = @"E:\Fresh" });

        var cut = Panel(module);

        cut.FindAll("[data-role=output-differs]").Should().BeEmpty();
        cut.FindAll("[data-role=model-differs]").Should().BeEmpty();
    }
}
