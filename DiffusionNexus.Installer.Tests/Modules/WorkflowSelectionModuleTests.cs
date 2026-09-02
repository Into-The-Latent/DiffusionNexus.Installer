using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class WorkflowSelectionModuleTests
{
    private static WizardSelection Selection(params ComfUIWorkflow[] workflows)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Workflows.AddRange(workflows);
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task Every_workflow_is_a_ticked_row_with_its_version()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "1.Text2Image", Version = 1, SubVersion = 2 };
        var b = new ComfUIWorkflow { Name = "2.Upscale", Version = 3 };

        await module.InitializeAsync(Selection(a, b));

        module.Rows.Select(r => (r.Name, r.Version)).Should().Equal(("1.Text2Image", "v1.2"), ("2.Upscale", "v3.0"));
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        module.SelectedCount.Should().Be(2);
    }

    [Fact]
    public void Applies_exactly_when_the_workload_has_workflows()
    {
        var module = new WorkflowSelectionModule();

        module.AppliesTo(Selection(new ComfUIWorkflow())).Should().BeTrue();
        module.AppliesTo(Selection()).Should().BeFalse();
    }

    [Fact]
    public async Task Unticked_workflows_become_excluded_ids()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "a" };
        var b = new ComfUIWorkflow { Name = "b" };
        await module.InitializeAsync(Selection(a, b));

        module.SetSelected(b.Id, false);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ExcludedWorkflowIds.Should().BeEquivalentTo([b.Id]);
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_starts_clean()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "a" };
        await module.InitializeAsync(Selection(a));
        module.SetSelected(a.Id, false);

        await module.InitializeAsync(Selection(new ComfUIWorkflow { Name = "b" }));

        module.Rows.Should().ContainSingle().Which.IsSelected.Should().BeTrue();
    }
}
