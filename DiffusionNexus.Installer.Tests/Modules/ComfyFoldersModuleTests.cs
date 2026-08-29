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
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.A1111, false)]
    [InlineData(RepositoryType.Forge, false)]
    [InlineData(RepositoryType.Fooocus, false)]
    [InlineData(RepositoryType.AceStep, false)]
    public void Applies_to_comfyui_and_aitoolkit_only(RepositoryType type, bool expected)
        => Module().AppliesTo(Selection(type)).Should().Be(expected);

    [Fact]
    public async Task Output_folder_is_offered_for_comfyui_but_not_aitoolkit()
    {
        var comfy = Module();
        await comfy.InitializeAsync(Selection(RepositoryType.ComfyUI));

        var toolkit = Module();
        await toolkit.InitializeAsync(Selection(RepositoryType.AIToolkit));

        comfy.SupportsOutputFolder.Should().BeTrue();
        toolkit.SupportsOutputFolder.Should().BeFalse();
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
    public async Task Output_folder_is_not_contributed_for_aitoolkit_even_if_set()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.AIToolkit));
        module.OutputFolder = @"D:\Out";

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.OutputFolder.Should().BeNull();
    }
}
