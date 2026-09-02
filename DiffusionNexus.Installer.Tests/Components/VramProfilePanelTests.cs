using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class VramProfilePanelTests : BunitContext
{
    private static async Task<(VramProfileModule Module, WizardSelection Selection)> ModuleAsync(string profiles)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Vram.VramProfiles = profiles;
        var selection = new WizardSelection { Workload = w };
        var module = new VramProfileModule();
        await module.InitializeAsync(selection);
        return (module, selection);
    }

    [Fact]
    public async Task Renders_only_the_declared_tiers_with_the_lowest_selected()
    {
        var (module, _) = await ModuleAsync("24,32");

        var cut = Render<VramProfilePanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("option").Select(o => o.TextContent.Trim()).Should().Equal("24 GB", "32 GB");
        cut.Find("select").GetAttribute("value").Should().Be("24");
    }

    [Fact]
    public async Task Picking_a_tier_updates_the_module_and_raises_Changed()
    {
        var (module, selection) = await ModuleAsync("8,12,16");
        var changed = false;

        var cut = Render<VramProfilePanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        cut.Find("select").Change("16");

        module.SelectedTier.Should().Be(16);
        selection.SelectedVramProfile.Should().Be(16);
        changed.Should().BeTrue("the sibling model panel rescans only when the page re-renders");
    }

    [Fact]
    public async Task A_value_outside_the_declared_tiers_is_ignored()
    {
        var (module, _) = await ModuleAsync("24,32");
        var cut = Render<VramProfilePanel>(p => p.Add(x => x.Module, module));

        cut.Find("select").Change("8");

        module.SelectedTier.Should().Be(24);
    }
}
