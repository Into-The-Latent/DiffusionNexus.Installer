using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

/// <summary>
/// The draft is the only thing standing between the wizard's answers and the SDK's init-only
/// options record. Every field it forgets defaults silently — no compiler error, no test failure,
/// just a different install. These pin the ones that were being dropped.
/// </summary>
public class OptionsFidelityTests
{
    private static ComfyFoldersModule ComfyFolders(UserSettings settings)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);
        return new ComfyFoldersModule(repo.Object);
    }

    private static WizardSelection Selection()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public void Uv_is_the_package_manager_unless_something_says_otherwise()
    {
        // SDK.Services.InstallationOptions.UseUvPackageManager is an init bool with no
        // initializer, so omitting it means pip: a different resolver from the one the catalog's
        // pins were validated against, and considerably slower. Both shipping front-ends set true.
        new InstallationOptionsDraft().ToOptions().UseUvPackageManager.Should().BeTrue();
    }

    [Fact]
    public async Task Per_type_model_folders_reach_the_options()
    {
        // A user with LoRAs on E: and checkpoints on F: was silently getting neither: only the
        // base folder was carried across, so extra_model_paths.yaml pointed ComfyUI at empty
        // subfolders of the base path.
        var settings = new UserSettings
        {
            DefaultModelBaseFolder = @"D:\Models",
            DefaultLorasFolder = @"E:\Loras",
            DefaultCheckpointsFolder = @"F:\Checkpoints",
            additionalFolders = [new AdditionalFolder { BaseName = "extra", MapsTo = @"G:\Extra" }],
        };

        var module = ComfyFolders(settings);
        await module.InitializeAsync(Selection());

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        var options = draft.ToOptions();

        options.FolderPathOverrides.Should().Contain("loras", @"E:\Loras");
        options.FolderPathOverrides.Should().Contain("checkpoints", @"F:\Checkpoints");
        options.AdditionalFolders.Should().ContainSingle().Which.MapsTo.Should().Be(@"G:\Extra");
    }

    [Fact]
    public async Task Declining_the_saved_folders_sends_only_the_library_folder()
    {
        // The opt-out has to actually reach the options, not just the panel: with it unticked the
        // YAML must be generated from the base path alone.
        var module = ComfyFolders(new UserSettings
        {
            DefaultModelBaseFolder = @"D:\Models",
            DefaultLorasFolder = @"E:\Loras",
            additionalFolders = [new AdditionalFolder { BaseName = "extra", MapsTo = @"G:\Extra" }],
        });

        await module.InitializeAsync(Selection());
        module.SavedFolderCount.Should().Be(2);
        module.UseSavedFolderDefaults = false;

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        var options = draft.ToOptions();

        options.ModelBaseFolder.Should().Be(@"D:\Models");
        options.FolderPathOverrides.Should().BeEmpty();
        options.AdditionalFolders.Should().BeEmpty();
    }

    [Fact]
    public void The_legacy_singular_folder_setting_is_used_when_the_plural_one_is_empty()
    {
        // An older user's stored folders live in the singular fields. Dropping the fallback would
        // ignore them without a word.
        var overrides = UserModelFolderMap.Build(new UserSettings
        {
            DefaultLoraFolder = @"E:\Loras",
            DefaultCheckpointFolder = @"F:\Checkpoints",
        });

        overrides.Should().Contain("loras", @"E:\Loras");
        overrides.Should().Contain("checkpoints", @"F:\Checkpoints");
    }

    [Fact]
    public void The_plural_folder_setting_wins_over_the_legacy_singular_one()
    {
        var overrides = UserModelFolderMap.Build(new UserSettings
        {
            DefaultLorasFolder = @"E:\New",
            DefaultLoraFolder = @"E:\Old",
        });

        overrides.Should().Contain("loras", @"E:\New");
    }

    [Fact]
    public void Blank_folder_settings_produce_no_override_at_all()
    {
        // An empty string is not a folder choice. Writing it through would point ComfyUI at "".
        UserModelFolderMap.Build(new UserSettings()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_can_override_the_seeded_vram_profile()
    {
        // Ordering guard for slice 2: the seed must happen BEFORE the Contribute loop, or a
        // VramProfileModule writing through the documented contract is silently overwritten.
        var selection = Selection();
        selection.SelectedVramProfile = 0;

        var registry = new WizardModuleRegistry(() => [new VramWritingModule()]);
        var plan = await registry.BuildPlanAsync(selection);

        plan.ToOptions().SelectedVramProfile.Should().Be(16);
    }

    /// <summary>Stand-in for the slice-2 VRAM module: contributes a tier the selection does not carry.</summary>
    private sealed class VramWritingModule : IWizardModule
    {
        public string Id => "vram-stub";
        public WizardStage Stage => WizardStage.Content;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.VramProfile;
        public bool AppliesTo(WizardSelection selection) => true;
        public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft draft) => draft.SelectedVramProfile = 16;
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }
}
