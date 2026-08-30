using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One card in the workload gallery.</summary>
public sealed record GalleryEntry(
    InstallationConfiguration Workload,
    bool IsInstallable,
    WorkloadCapability MissingCapabilities);
