using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>Reads the workloads this installer may offer. Hides ICatalog from the UI.</summary>
public interface IWorkloadSource
{
    Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default);
    Task<byte[]?> GetThumbnailAsync(Guid workloadId, CancellationToken ct = default);
}
