using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class ModelsOnlyFolderPanelTests : BunitContext
{
    private const string FlatWarning = "ComfyUI won't find these models until you move them into the right folders yourself.";

    private readonly Mock<IFolderPicker> _picker = new();

    public ModelsOnlyFolderPanelTests() => Services.AddSingleton(_picker.Object);

    private static async Task<ModelsOnlyFolderModule> Module()
    {
        var workload = new InstallationConfiguration();
        workload.ModelDownloads = [new ModelDownload { Name = "The VAE", Destination = "models/vae", Enabled = true }];
        var module = new ModelsOnlyFolderModule();
        await module.InitializeAsync(new WizardSelection { Workload = workload, Mode = WizardMode.ModelsOnly });
        return module;
    }

    [Fact]
    public async Task Says_the_folder_is_not_checked()
    {
        var module = await Module();
        var cut = Render<ModelsOnlyFolderPanel>(p => p.Add(x => x.Module, module));

        cut.Markup.Should().Contain("Download folder").And.Contain("Sort models into subfolders");
        cut.Find(".panel-hint").TextContent.Should().Contain("We don't check this folder, so make sure it's the right one.");
    }

    [Fact]
    public async Task Red_box_only_when_subfolders_are_off()
    {
        var module = await Module();
        module.Folder = @"E:\AI\models";
        var cut = Render<ModelsOnlyFolderPanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("[data-role=flat-warning]").Should().BeEmpty();

        cut.Find("[data-role=sort-subfolders]").Change(false);

        cut.Find("[data-role=flat-warning]").TextContent.Should().Be(FlatWarning);
        module.SortIntoSubfolders.Should().BeFalse();
    }

    [Fact]
    public async Task Typing_a_folder_tells_the_page_and_shows_the_preview()
    {
        var changed = 0;
        var module = await Module();
        var cut = Render<ModelsOnlyFolderPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed++)));

        cut.Find("input[type=text]").Input(@"E:\AI\models");

        changed.Should().Be(1, "the parent owns Next");
        cut.Find(".models-preview").TextContent.Should().Contain("vae").And.Contain("The VAE");
    }

    [Fact]
    public async Task A_folder_problem_shows_next_to_the_box()
    {
        var module = await Module();
        var cut = Render<ModelsOnlyFolderPanel>(p => p.Add(x => x.Module, module));

        cut.Find("[data-role=folder-problem]").TextContent.Should().Be("Choose a folder to download into.");
    }

    [Fact]
    public async Task Browse_fills_the_box()
    {
        _picker.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"E:\Picked");
        var module = await Module();
        var cut = Render<ModelsOnlyFolderPanel>(p => p.Add(x => x.Module, module));

        cut.Find("button.btn-secondary").Click();

        cut.WaitForAssertion(() => module.Folder.Should().Be(@"E:\Picked"));
    }
}
