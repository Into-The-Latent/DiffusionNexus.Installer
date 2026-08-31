using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One card in the workload gallery.</summary>
/// <param name="MissingCapabilities">
/// Blocking capabilities with no registered module. Empty when the card is disabled for an
/// incompatibility instead.
/// </param>
/// <param name="Incompatibility">
/// Why the pipeline would refuse this workload outright, or null. Independent of module coverage:
/// no slice-2 module can make an impossible torch/CUDA pairing installable.
/// </param>
public sealed record GalleryEntry(
    InstallationConfiguration Workload,
    bool IsInstallable,
    WorkloadCapability MissingCapabilities,
    string? Incompatibility = null)
{
    /// <summary>What to tell the user about a disabled card. Null when the card is installable.</summary>
    public string? UnavailableReason => IsInstallable
        ? null
        : Incompatibility ?? $"Coming soon — needs {MissingCapabilities}";
}
