using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WorkflowSelectionPanelTests : BunitContext
{
    private static async Task<WorkflowSelectionModule> ModuleAsync()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Workflows.Add(new ComfUIWorkflow { Name = "1.Text2Image", Version = 1, SubVersion = 1 });
        w.Workflows.Add(new ComfUIWorkflow { Name = "2.Upscale", Version = 2 });
        var module = new WorkflowSelectionModule();
        await module.InitializeAsync(new WizardSelection { Workload = w });
        return module;
    }

    [Fact]
    public async Task Lists_every_workflow_ticked_with_its_version()
    {
        var module = await ModuleAsync();

        var cut = Render<WorkflowSelectionPanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("input[type=checkbox]").Should().HaveCount(2).And.OnlyContain(i => i.HasAttribute("checked"));
        cut.Markup.Should().Contain("1.Text2Image").And.Contain("v1.1").And.Contain("v2.0");
    }

    [Fact]
    public async Task Unticking_a_workflow_updates_the_module_and_raises_Changed()
    {
        var module = await ModuleAsync();
        var changed = false;

        var cut = Render<WorkflowSelectionPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed = true)));
        cut.FindAll("input[type=checkbox]")[1].Change(false);

        module.SelectedCount.Should().Be(1);
        changed.Should().BeTrue();
    }
}
