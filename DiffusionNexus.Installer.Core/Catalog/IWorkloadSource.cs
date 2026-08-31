using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>Reads the workloads this installer may offer. Hides ICatalog from the UI.</summary>
public interface IWorkloadSource
{
    Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default);
    Task<byte[]?> GetThumbnailAsync(Guid workloadId, CancellationToken ct = default);

    /// <summary>
    /// Wheels a workload's SelectedLamaCppWheelId can point at. The install step fails on a null
    /// URL, so somebody has to turn the id into a URL and the catalog is the only place that has it.
    /// </summary>
    Task<IReadOnlyList<LamaCppWheel>> GetLamaCppWheelsAsync(CancellationToken ct = default);

    /// <summary>
    /// What the catalog layer has to say about the load. The SDK reports a missing or malformed
    /// catalog as Error diagnostics on a successfully-returned empty snapshot rather than as an
    /// exception, so without this the UI cannot tell "nothing installed" from "nothing to install".
    /// </summary>
    IReadOnlyList<CatalogDiagnostic> Diagnostics { get; }
}
