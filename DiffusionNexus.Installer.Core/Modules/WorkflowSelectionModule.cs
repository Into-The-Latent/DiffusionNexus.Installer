using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

public sealed class WorkflowRow(Guid id, string name, string version)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// Which of the workload's workflows to write into the install. Non-blocking: with no module the
/// pipeline exports every declared workflow, which is correct. This exists so the user can see and
/// skip them, as 1.x allowed.
/// </summary>
public sealed class WorkflowSelectionModule : IWizardModule
{
    public string Id => "workflow-selection";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 20;
    public WorkloadCapability Satisfies => WorkloadCapability.Workflows;

    public IReadOnlyList<WorkflowRow> Rows { get; private set; } = [];
    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public bool AppliesTo(WizardSelection selection) => selection.Workload.Workflows.Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        Rows = selection.Workload.Workflows
            .Select(w => new WorkflowRow(w.Id, w.Name, $"v{w.Version}.{w.SubVersion}"))
            .ToList();
        return Task.CompletedTask;
    }

    public void SetSelected(Guid id, bool selected)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row) row.IsSelected = selected;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.ExcludedWorkflowIds.Clear();
        foreach (var row in Rows.Where(r => !r.IsSelected)) draft.ExcludedWorkflowIds.Add(row.Id);
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
