using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class UnconditionalModuleTests
{
    private static WizardSelection Selection(RepositoryType type = RepositoryType.Fooocus)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static Mock<IUserSettingsRepository> Settings(string defaultFolder)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = defaultFolder });
        return repo;
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.AceStep)]
    public void InstallFolder_applies_to_every_workload(RepositoryType type)
    {
        var module = new InstallFolderModule(Settings("").Object);

        module.AppliesTo(Selection(type)).Should().BeTrue();
        module.Satisfies.Should().Be(WorkloadCapability.None);
        module.Stage.Should().Be(WizardStage.Location);
    }

    [Fact]
    public async Task InstallFolder_seeds_from_the_remembered_default()
    {
        var module = new InstallFolderModule(Settings(@"D:\AI").Object);
        var selection = Selection();

        await module.InitializeAsync(selection);

        module.TargetFolder.Should().Be(@"D:\AI");
    }

    [Fact]
    public async Task InstallFolder_writes_its_answer_back_to_the_selection()
    {
        var module = new InstallFolderModule(Settings("").Object);
        var selection = Selection();
        await module.InitializeAsync(selection);

        module.TargetFolder = @"E:\Installs\Fooocus";
        module.Contribute(new InstallationOptionsDraft());

        selection.TargetFolder.Should().Be(@"E:\Installs\Fooocus");
    }

    [Fact]
    public async Task InstallFolder_rejects_an_empty_path()
    {
        var module = new InstallFolderModule(Settings("").Object);
        await module.InitializeAsync(Selection());

        module.TargetFolder = "   ";

        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public void Shortcuts_contributes_both_flags_and_the_conflict_callback()
    {
        var module = new ShortcutsModule { CreateDesktopShortcut = false, CustomName = "Fooocus (test)" };
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.CreateDesktopShortcut.Should().BeFalse();
        draft.CreateStartMenuShortcut.Should().BeTrue();
        draft.DesktopShortcutName.Should().Be("Fooocus (test)");
        draft.StartMenuShortcutName.Should().Be("Fooocus (test)");
    }

    [Fact]
    public void Shortcuts_leaves_names_null_when_the_user_did_not_rename()
    {
        var draft = new InstallationOptionsDraft();

        new ShortcutsModule().Contribute(draft);

        draft.DesktopShortcutName.Should().BeNull();
        draft.StartMenuShortcutName.Should().BeNull();
    }
}
