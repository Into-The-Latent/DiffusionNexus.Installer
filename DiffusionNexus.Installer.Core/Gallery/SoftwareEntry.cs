using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One card on the welcome screen: a software and the workloads the catalog offers for it.</summary>
public sealed record SoftwareEntry(
    RepositoryType Type,
    string DisplayName,
    string? LogoPath,
    IReadOnlyList<GalleryEntry> Workloads)
{
    public int WorkloadCount => Workloads.Count;

    /// <summary>The only workload, when there is exactly one. Null otherwise.</summary>
    public GalleryEntry? SingleWorkload => Workloads.Count == 1 ? Workloads[0] : null;

    /// <summary>
    /// True when picking this card should open the wizard directly. A screen that asks the user
    /// to choose from a list of one is a screen that should not exist.
    /// </summary>
    public bool GoesStraightToSetup => SingleWorkload is not null;
}
