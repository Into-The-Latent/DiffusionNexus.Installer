using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>
/// Installer-facing view of the catalog. What it may offer is <see cref="WorkloadVisibility"/>'s
/// decision, applied here rather than in the gallery so that the list and the by-id lookup can
/// never disagree. Uses only the async ICatalog members: the blocking Source/State properties can
/// run the first-load seed on the calling thread.
/// </summary>
public sealed class CatalogWorkloadSource(ICatalog catalog, WorkloadVisibility visibility) : IWorkloadSource
{
    public async Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default)
    {
        var all = await catalog.GetWorkloadsAsync(ct).ConfigureAwait(false);
        return all.Where(visibility.IsOffered).ToList();
    }

    public async Task<InstallationConfiguration?> GetInstallerWorkloadAsync(Guid id, CancellationToken ct = default)
    {
        // One clone, not twenty-five: ICatalog.GetWorkloadAsync copies only the match.
        var workload = await catalog.GetWorkloadAsync(id, ct).ConfigureAwait(false);
        return workload is not null && visibility.IsOffered(workload) ? workload : null;
    }

    public Task<byte[]?> GetThumbnailAsync(Guid workloadId, CancellationToken ct = default)
        => catalog.ReadThumbnailAsync(workloadId, ct);

    public Task<IReadOnlyList<LamaCppWheel>> GetLamaCppWheelsAsync(CancellationToken ct = default)
        => catalog.GetLamaCppWheelsAsync(ct);

    /// <summary>
    /// Read after a load, not before: ICatalog populates this during the resolve/load the async
    /// members above trigger, and hands back the live list rather than a copy.
    /// </summary>
    public IReadOnlyList<CatalogDiagnostic> Diagnostics => catalog.Diagnostics;
}
