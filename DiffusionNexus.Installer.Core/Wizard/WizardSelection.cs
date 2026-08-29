using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Everything the wizard has learned so far. Modules read from here rather than from each other:
/// a downstream module that needs an upstream answer (ModelSelection needs the VRAM tier) reads
/// the value, not the module that produced it.
/// </summary>
public sealed class WizardSelection
{
    public required InstallationConfiguration Workload { get; init; }

    /// <summary>Where the workload gets installed. Set by the InstallFolder module.</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Chosen VRAM tier in GB, 0 when the workload has no profiles.</summary>
    public int SelectedVramProfile { get; set; }

    public WorkloadCapability Capabilities => WorkloadCapabilities.Detect(Workload);
}
