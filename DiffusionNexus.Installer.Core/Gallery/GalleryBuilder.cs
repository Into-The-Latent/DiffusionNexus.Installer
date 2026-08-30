using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Turns the catalog into cards. Workloads whose capabilities are not yet covered stay visible but
/// disabled: hiding them would misrepresent what the catalog actually contains.
/// </summary>
public sealed class GalleryBuilder(IWorkloadSource source, WizardModuleRegistry registry)
{
    public async Task<IReadOnlyList<GalleryEntry>> BuildAsync(CancellationToken ct = default)
    {
        var workloads = await source.GetInstallerWorkloadsAsync(ct).ConfigureAwait(false);

        return workloads
            .Select(w =>
            {
                var needed = WorkloadCapabilities.DetectBlocking(w);
                var missing = needed & ~registry.SatisfiedCapabilities;
                return new GalleryEntry(w, missing == WorkloadCapability.None, missing);
            })
            .OrderByDescending(e => e.IsInstallable)
            .ThenBy(e => e.Workload.IsLegacy)
            .ThenByDescending(e => e.Workload.IsReleaseConfig)
            .ThenBy(e => e.Workload.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
