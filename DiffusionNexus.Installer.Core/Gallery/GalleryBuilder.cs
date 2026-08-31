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

                // Asked even when nothing is missing: a fully covered workload can still carry a
                // torch/CUDA pairing the pipeline refuses before step 1, and that refusal is not a
                // capability gap any module can close.
                var incompatibility = WorkloadCapabilities.DetectIncompatibility(w);

                // registry.IsInstallable is the single answer; the two values above only explain it.
                return new GalleryEntry(w, registry.IsInstallable(w), missing, incompatibility);
            })
            .OrderByDescending(e => e.IsInstallable)
            .ThenBy(e => e.Workload.IsLegacy)
            .ThenByDescending(e => e.Workload.IsReleaseConfig)
            .ThenBy(e => e.Workload.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
