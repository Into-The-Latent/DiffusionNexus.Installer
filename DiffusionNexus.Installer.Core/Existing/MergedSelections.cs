using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Existing;

/// <summary>Builds the wizard's selection for an update, or for several workloads in one run.</summary>
public static class MergedSelections
{
    private const string UpdateOnlyName = "ComfyUI update";

    /// <summary>What Select multiple picked. Null when none of the picks is in the catalog any more.</summary>
    /// <param name="mode">The path the picks were made on.</param>
    /// <param name="picks">The picks, each at its own version.</param>
    /// <param name="workloads">The whole catalog: a missing Manager comes from any workload's list.</param>
    /// <param name="catalog">The catalog line for the install report.</param>
    public static WizardSelection? ForPicks(
        WizardMode mode,
        IReadOnlyList<WorkloadPick> picks,
        IReadOnlyList<InstallationConfiguration> workloads,
        InstalledCatalogReading? catalog)
    {
        var choices = picks
            .Select(p => (Pick: p, Workload: workloads.FirstOrDefault(w => w.Id == p.WorkloadId)))
            .Where(p => p.Workload is not null)
            .Select(p => new WorkloadChoice(p.Workload!, p.Pick.VramGb))
            .ToList();
        if (choices.Count == 0) return null;

        var name = choices.Count == 1 ? choices[0].Workload.Name : $"{choices.Count} workloads";
        return Build(mode, choices, choices, name, updateOnly: false, workloads, catalog);
    }

    /// <summary>One workload on the update path. The memory panel still chooses its version.</summary>
    public static WizardSelection ForUpdate(
        InstallationConfiguration workload,
        IReadOnlyList<InstallationConfiguration> workloads,
        InstalledCatalogReading? catalog) =>
        Build(WizardMode.UpdateExisting, [new WorkloadChoice(workload, 0)], [], workload.Name, updateOnly: false, workloads, catalog);

    /// <summary>The tile that updates ComfyUI and adds no workload.</summary>
    public static WizardSelection ForUpdateOnly(
        IReadOnlyList<InstallationConfiguration> workloads,
        InstalledCatalogReading? catalog) =>
        Build(WizardMode.UpdateExisting, [], [], UpdateOnlyName, updateOnly: true, workloads, catalog);

    private static WizardSelection Build(
        WizardMode mode,
        IReadOnlyList<WorkloadChoice> merged,
        IReadOnlyList<WorkloadChoice> shownAsSeveral,
        string name,
        bool updateOnly,
        IReadOnlyList<InstallationConfiguration> workloads,
        InstalledCatalogReading? catalog)
    {
        var union = ExtendConfigurationMerger.Union(merged, name);
        var selection = new WizardSelection
        {
            Workload = union.Configuration,
            Catalog = catalog,
            Mode = mode,
            Choices = shownAsSeveral,
            UpdateOnly = updateOnly,
            PackNameByModelId = shownAsSeveral.Count > 0 ? union.PackNameByModelId : new Dictionary<Guid, string>(),
        };

        if (mode == WizardMode.UpdateExisting)
            selection.Existing = new ExistingRun(selection, union, merged.Select(c => c.Workload).ToList(), FindManager(workloads));

        return selection;
    }

    /// <summary>The catalog's ComfyUI-Manager entry, so the SDK needn't hardcode where it lives.</summary>
    private static GitRepository? FindManager(IEnumerable<InstallationConfiguration> workloads) =>
        workloads.SelectMany(w => w.GitRepositories)
            .FirstOrDefault(r => ExtendConfigurationMerger.HasManagerRepository(new InstallationConfiguration { GitRepositories = [r] }));
}
