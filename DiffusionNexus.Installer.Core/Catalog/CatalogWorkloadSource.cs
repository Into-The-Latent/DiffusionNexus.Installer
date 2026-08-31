using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>
/// Installer-facing view of the catalog. DiffusionNexusCore workloads belong to the main app and
/// are never offered here. Uses only the async ICatalog members: the blocking Source/State
/// properties can run the first-load seed on the calling thread.
/// </summary>
public sealed class CatalogWorkloadSource(ICatalog catalog) : IWorkloadSource
{
    public async Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default)
    {
        var all = await catalog.GetWorkloadsAsync(ct).ConfigureAwait(false);
        return all.Where(w => w.WorkloadTarget == WorkloadTargetType.Installer).ToList();
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
