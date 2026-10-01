using DiffusionNexus.Installer.Core.Updates;
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

    /// <summary>
    /// The catalog <see cref="Workload"/> was read from, taken right after reading it (spec 7.2).
    /// The install report names this, not what is installed when Install is pressed: a channel
    /// switch can apply another catalog while the wizard is open. Null: read at install start.
    /// </summary>
    public InstalledCatalogReading? Catalog { get; init; }

    /// <summary>Where the workload gets installed. Set by the InstallFolder module.</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Chosen VRAM tier in GB, 0 when the workload has no profiles.</summary>
    public int SelectedVramProfile { get; set; }

    /// <summary>Custom model library root, or null for the install's own models folder. Written by ComfyFoldersModule.</summary>
    public string? ModelBaseFolder { get; set; }

    /// <summary>Per-type folder overrides in effect — empty when the user opted out. Written by ComfyFoldersModule.</summary>
    public IReadOnlyDictionary<string, string> FolderPathOverrides { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public WorkloadCapability Capabilities => WorkloadCapabilities.Detect(Workload);
}
