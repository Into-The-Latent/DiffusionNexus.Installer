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
    public async Task A_null_install_folder_in_the_settings_file_loads_as_none_chosen()
    {
        // System.Text.Json loads "DefaultTargetInstallFolder": null as null.
        var module = await Module(Selection(), remembered: null!);

        module.TargetFolder.Should().BeEmpty();
        module.DestinationFolder.Should().BeNull();
        module.Validate().ErrorMessage.Should().Be("Choose a folder to install into.");
    }

    [Fact]
    public async Task A_path_pasted_with_Copy_as_path_quotes_is_the_same_folder()
    {
        var selection = Selection();
        var module = await Module(selection);

        module.TargetFolder = @" ""E:\Installer\9\"" ";

        selection.TargetFolder.Should().Be(@"E:\Installer\9");
        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task The_selection_and_the_destination_use_the_trimmed_folder()
    {
        // Review finding: a pasted trailing space made "Will be created" show one folder while
        // the pipeline created another and the presence scan walked a third.
        var selection = Selection();
        var module = await Module(selection);

        module.TargetFolder = @"E:\Installer\9 ";

        module.TargetFolder.Should().Be(@"E:\Installer\9 ", "the text box must not fight the user's keystrokes");
        selection.TargetFolder.Should().Be(@"E:\Installer\9");
        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task Persist_remembers_the_install_folder_for_the_next_run()
    {
        // Review finding: DefaultTargetInstallFolder was read at initialization and written
        // nowhere, so every install started from a stale default.
        var stored = new UserSettings { DefaultTargetInstallFolder = @"C:\Old" };
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings u, CancellationToken _) => u);
        var module = new InstallFolderModule(settings.Object, new PreInstallationService());
        await module.InitializeAsync(Selection());
        module.TargetFolder = @"E:\Installer\9 ";

        await module.PersistAsync();

        settings.Verify(s => s.SaveAsync(It.Is<UserSettings>(u => u.DefaultTargetInstallFolder == @"E:\Installer\9"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task There_is_no_destination_while_no_folder_is_chosen()
    {
        var module = await Module(Selection());

        module.TargetFolder = "   ";

        module.DestinationFolder.Should().BeNull();
    }

    [Theory]
    [InlineData("ComfyUI")]
    [InlineData(@"ComfyUI\")]
    [InlineData("")]
    public async Task An_existing_install_is_refused_however_the_folder_is_spelled(string tail)
    {
        // The pipeline drops a trailing folder named after the repository (PathNormalizer), so
        // "X\ComfyUI" installs into X\ComfyUI. The pre-flight must look there, not at
        // X\ComfyUI\ComfyUI, or Next is enabled on top of a live installation.
        var root = Path.Combine(Path.GetTempPath(), $"dn-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), "");
        try
        {
            var module = await Module(Selection());

            module.TargetFolder = Path.Combine(root, tail);

            module.DestinationFolder.Should().Be(Path.Combine(root, "ComfyUI"));
            module.Validate().IsValid.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("AI")]
    [InlineData(@"""E:\x")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\?\E:\AI")]
    public async Task An_install_folder_that_is_not_a_full_path_is_refused(string folder)
    {
        // A relative folder installed under the app's working directory.
        var module = await Module(Selection());

        module.TargetFolder = folder;

        module.Validate().ErrorMessage.Should().Contain("full path");
    }

    [Fact]
    public async Task An_install_folder_Windows_cannot_name_is_refused()
    {
        var module = await Module(Selection());

        module.TargetFolder = @"C:\a<b";

        module.Validate().ErrorMessage.Should().Be(FolderInput.InvalidNameMessage);
    }

    [Fact]
    public async Task The_destination_follows_the_remembered_folder_right_after_initialization()
    {
        var module = await Module(Selection(), remembered: @"C:\AI");

        module.DestinationFolder.Should().Be(@"C:\AI\ComfyUI");
    }
}
