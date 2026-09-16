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
/// The folders page shows the output folder and the "use my own model folder" switch. Only with
/// the switch on does the model library appear, and then behind an "Advanced" toggle that is
/// closed by default (issue #15).
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
        var cut = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", DefaultLorasFolder = "Lora", UseModelLibraryFolder = true }));

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
        var plain = RenderPanel(await Module(new UserSettings { UseModelLibraryFolder = true }));
        plain.Find(".advanced-toggle").TextContent.Should().NotContain("custom folders in use");

        var custom = RenderPanel(await Module(new UserSettings { DefaultLorasFolder = "Lora", UseModelLibraryFolder = true }));
        custom.Find(".advanced-toggle").TextContent.Should().Contain("custom folders in use");

        var library = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true }));
        library.Find(".advanced-toggle").TextContent.Should().Contain("custom folders in use");
    }

    [Fact]
    public async Task The_library_box_is_first_in_advanced_and_shows_the_install_default_as_grey_text()
    {
        var module = await Module(new UserSettings { UseModelLibraryFolder = true });
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
        var cut = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true }));

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
        var module = await Module(new UserSettings { UseModelLibraryFolder = true });
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
        var module = await Module(new UserSettings { DefaultLorasFolder = "Lora", UseModelLibraryFolder = true });
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
        var module = await Module(new UserSettings { UseModelLibraryFolder = true });
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

    // ---- The switch itself (issue #15) ---------------------------------------------------------

    [Fact]
    public async Task The_switch_is_visible_off_by_default_and_hides_everything_about_model_folders()
    {
        var cut = RenderPanel(await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", DefaultLorasFolder = "Lora" }));

        var toggle = cut.Find("input[data-role='use-library']");
        toggle.GetAttribute("type").Should().Be("checkbox");
        toggle.GetAttribute("role").Should().Be("switch");
        toggle.HasAttribute("checked").Should().BeFalse();

        cut.FindAll(".advanced-toggle").Should().BeEmpty("off means ComfyUI's own folders: nothing to configure");
        cut.FindAll("[data-role='library']").Should().BeEmpty();
        cut.FindAll("[data-folder-key]").Should().BeEmpty();
        cut.FindAll("[data-role='output']").Should().ContainSingle("the output folder is not a model folder");
        cut.Markup.Should().Contain("extra_model_paths.yaml", "the user is told what off means");
        cut.Find(".switch-text").TextContent.Should().Contain("remembered library").And.Contain(@"D:\Models",
            "an upgrading user must see WHY models they used to have are not found any more");
    }

    [Fact]
    public async Task Without_a_remembered_library_the_off_hint_does_not_mention_one()
    {
        var cut = RenderPanel(await Module());

        cut.Find(".switch-text").TextContent.Should().Contain("Off:").And.NotContain("remembered library");
    }

    [Fact]
    public async Task Turning_the_switch_on_reveals_the_advanced_section_and_raises_Changed()
    {
        var module = await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models" });
        var changed = false;
        var cut = RenderPanel(module, () => changed = true);

        cut.Find("input[data-role='use-library']").Change(true);

        module.UseModelLibraryFolder.Should().BeTrue();
        changed.Should().BeTrue();
        cut.Find(".advanced-toggle").TextContent.Should().Contain("custom folders in use", "the remembered library is applied again");
        cut.Find(".advanced-toggle").Click();
        cut.Find("[data-role='library']").GetAttribute("value").Should().Be(@"D:\Models");
    }

    [Fact]
    public async Task Turning_the_switch_off_again_hides_the_section_but_keeps_the_folder()
    {
        var module = await Module(new UserSettings { DefaultModelBaseFolder = @"D:\Models", UseModelLibraryFolder = true });
        var cut = RenderPanel(module);
        cut.Find("input[data-role='use-library']").HasAttribute("checked").Should().BeTrue();

        cut.Find("input[data-role='use-library']").Change(false);

        module.UseModelLibraryFolder.Should().BeFalse();
        cut.FindAll(".advanced-toggle").Should().BeEmpty();
        module.ModelBaseFolder.Should().Be(@"D:\Models");
    }
}
