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

public class InstallFolderModuleTests
{
    private static WizardSelection Selection(string url = "https://github.com/comfyanonymous/ComfyUI")
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = url;
        return new WizardSelection { Workload = w };
    }

    private static async Task<InstallFolderModule> Module(WizardSelection selection, string remembered = "")
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = remembered });
        var module = new InstallFolderModule(settings.Object, new PreInstallationService());
        await module.InitializeAsync(selection);
        return module;
    }

    [Fact]
    public async Task The_destination_is_the_repo_folder_under_the_chosen_folder()
    {
        // The page shows this line so the user sees "E:\Installer\9\ComfyUI" before pressing Next,
        // instead of learning it from the "already exists" error.
        var module = await Module(Selection());

        module.TargetFolder = @"E:\Installer\9";

        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task There_is_no_destination_while_no_folder_is_chosen()
    {
        var module = await Module(Selection());

        module.TargetFolder = "   ";

        module.DestinationFolder.Should().BeNull();
    }

    [Fact]
    public async Task The_destination_follows_the_remembered_folder_right_after_initialization()
    {
        var module = await Module(Selection(), remembered: @"C:\AI");

        module.DestinationFolder.Should().Be(@"C:\AI\ComfyUI");
    }
}
