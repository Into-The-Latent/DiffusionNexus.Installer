using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The folders page shows only the output folder; the model library and everything per-type live
/// behind an "Advanced" toggle that is closed by default.
/// </summary>
public class ComfyFoldersPanelTests : BunitContext
{
    public ComfyFoldersPanelTests() => Services.AddSingleton(Mock.Of<IFolderPicker>());

    private static async Task<ComfyFoldersModule> Module(UserSettings? settings = null)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new UserSettings());
        var module = new ComfyFoldersModule(repo.Object);
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(new WizardSelection { Workload = w, TargetFolder = @"E:\Installer\9" });
        return module;
    }

    private IRenderedComponent<ComfyFoldersPanel> RenderPanel(ComfyFoldersModule module, Action? changed = null) =>
        Render<ComfyFoldersPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed?.Invoke())));

    [Fact]
    public async Task Only_the_output_folder_shows_until_advanced_is_opened()
    {
        var cut = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", DefaultLorasFolder = "Lora" }));

        cut.Markup.Should().NotContain("saved model folder");
        cut.FindAll("[data-folder-key]").Should().BeEmpty("the per-type list is advanced");
        cut.FindAll("[data-role='library']").Should().BeEmpty("the model library moved into advanced");
        cut.FindAll(".checkbox").Should().BeEmpty("the overwrite choice is advanced too");
        cut.FindAll(".path-row input").Should().ContainSingle("output only");
        cut.Find(".advanced-toggle").TextContent.Should().Contain("Advanced");
    }

    [Fact]
    public async Task The_output_box_shows_the_install_default_as_grey_text()
    {
        var cut = RenderPanel(await Module());

        var output = cut.Find("[data-role='output']");
        output.GetAttribute("value").Should().BeNullOrEmpty();
        output.GetAttribute("placeholder").Should().Be(@"E:\Installer\9\ComfyUI\output");
    }

    [Fact]
    public async Task The_toggle_says_when_custom_folders_are_in_use()
    {
        var plain = RenderPanel(await Module());
        plain.Find(".advanced-toggle").TextContent.Should().NotContain("custom folders in use");

        var custom = RenderPanel(await Module(new UserSettings { DefaultLorasFolder = "Lora" }));
        custom.Find(".advanced-toggle").TextContent.Should().Contain("custom folders in use");

        var library = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));
        library.Find(".advanced-toggle").TextContent.Should().Contain("custom folders in use");
    }

    [Fact]
    public async Task The_library_box_is_first_in_advanced_and_shows_the_install_default_as_grey_text()
    {
        var module = await Module();
        var changed = false;
        var cut = RenderPanel(module, () => changed = true);
        cut.Find(".advanced-toggle").Click();

        var library = cut.Find(".advanced input");
        library.GetAttribute("data-role").Should().Be("library");
        library.GetAttribute("placeholder").Should().Be(@"E:\Installer\9\ComfyUI\models");

        library.Input(@"D:\Models");

        module.ModelBaseFolder.Should().Be(@"D:\Models");
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Opening_advanced_shows_every_folder_type_with_reset_and_add()
    {
        var cut = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));

        cut.Find(".advanced-toggle").Click();

        cut.FindAll("[data-folder-key]").Should().HaveCount(21);
        cut.Find("[data-folder-key='checkpoints']").GetAttribute("value").Should().Be("checkpoints");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Reset to standard");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "+ Add folder");
        cut.Find(".checkbox").TextContent.Should().Contain("Overwrite");
    }

    [Fact]
    public async Task Typing_a_folder_name_updates_the_module_and_raises_Changed()
    {
        var module = await Module();
        var changed = false;
        var cut = RenderPanel(module, () => changed = true);
        cut.Find(".advanced-toggle").Click();

        cut.Find("[data-folder-key='loras']").Input("MyLoras");

        module.FolderPathOverrides.Should().Contain("loras", "MyLoras");
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_puts_the_standard_names_back()
    {
        var module = await Module(new UserSettings { DefaultLorasFolder = "Lora" });
        var cut = RenderPanel(module);
        cut.Find(".advanced-toggle").Click();
        cut.Find("[data-folder-key='loras']").GetAttribute("value").Should().Be("Lora");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reset to standard").Click();

        cut.Find("[data-folder-key='loras']").GetAttribute("value").Should().Be("loras");
        module.FolderPathOverrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Additional_folders_can_be_added_edited_and_removed()
    {
        var module = await Module();
        var cut = RenderPanel(module);
        cut.Find(".advanced-toggle").Click();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add folder").Click();
        cut.Find(".additional-folder input[data-role='name']").Input("extra");
        cut.Find(".additional-folder input[data-role='path']").Input(@"G:\Extra");

        module.AdditionalFolders.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { BaseName = "extra", MapsTo = @"G:\Extra" });

        cut.Find(".additional-folder button[data-role='remove']").Click();

        module.AdditionalFolders.Should().BeEmpty();
        cut.FindAll(".additional-folder").Should().BeEmpty();
    }
}
