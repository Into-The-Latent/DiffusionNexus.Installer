using DiffusionNexus.Installer.SDK.Services;
using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// Proves the Fix-1 wiring end to end through a real render: Blazor calls StateHasChanged only on
/// the component that owns a handler, so without the panel invoking a parent-supplied Changed
/// callback after every edit, the parent's Next button goes stale and the wizard cannot be
/// completed. This renders the actual InstallFolderPanel from the Electron project, not a stand-in.
/// </summary>
public class InstallFolderPanelTests : BunitContext
{
    private static InstallFolderModule Module()
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = string.Empty });
        return new InstallFolderModule(settings.Object, new PreInstallationService());
    }

    private static async Task<InstallFolderModule> InitializedModule()
    {
        var module = Module();
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(new WizardSelection { Workload = w });
        return module;
    }

    [Fact]
    public async Task The_panel_says_software_and_shows_the_folder_that_will_be_created()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        var module = await InitializedModule();
        var cut = Render<InstallFolderPanel>(p => p.Add(x => x.Module, module));

        cut.Markup.Should().Contain("Where the software gets installed").And.NotContain("workload");
        cut.FindAll(".destination").Should().BeEmpty("nothing is created while the box is empty");

        cut.Find("input").Input(@"E:\Installer\9");

        cut.Find(".destination").TextContent.Should().Contain(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public void Typing_a_folder_raises_Changed_and_updates_the_module()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());

        var module = Module();
        module.TargetFolder.Should().BeEmpty();
        var changed = false;

        var cut = Render<InstallFolderPanel>(parameters => parameters
            .Add(p => p.Module, module)
            .Add(p => p.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        cut.Find("input").Input(@"D:\Installs\Fooocus");

        changed.Should().BeTrue("the parent's disabled state can only refresh if it is notified");
        module.TargetFolder.Should().Be(@"D:\Installs\Fooocus");
    }

    [Fact]
    public async Task Dismissing_the_folder_dialog_leaves_the_field_unchanged_and_does_not_raise_Changed()
    {
        var picker = new Mock<IFolderPicker>();
        picker.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(picker.Object);

        var module = Module();
        module.TargetFolder = @"C:\Existing";
        var changed = false;

        var cut = Render<InstallFolderPanel>(parameters => parameters
            .Add(p => p.Module, module)
            .Add(p => p.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        changed.Should().BeFalse("a dismissed dialog must not be treated as an edit");
        module.TargetFolder.Should().Be(@"C:\Existing");
    }
}
