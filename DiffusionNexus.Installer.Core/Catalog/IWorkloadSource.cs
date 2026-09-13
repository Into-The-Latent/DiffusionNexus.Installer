using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>Reads the workloads this installer may offer. Hides ICatalog from the UI.</summary>
public interface IWorkloadSource
{
    Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default);

    /// <summary>
    /// One workload by id, or null when the catalog has no such workload OR it is not one this
    /// installer offers. Same filter as <see cref="GetInstallerWorkloadsAsync"/>, asked about a
    /// single id.
    ///
    /// Separate from the list member because the list member is expensive: the catalog hands back
    /// a deep copy of EVERY catalogued workload, nested model-download lists included. The
    /// thumbnail endpoint only needs to answer "is this id one of ours", and a 16-card grid asks
    /// it 16 times per render.
    /// </summary>
    Task<InstallationConfiguration?> GetInstallerWorkloadAsync(Guid id, CancellationToken ct = default);

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
