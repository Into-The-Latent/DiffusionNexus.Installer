using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Everything the wizard has learned so far. Modules read from here rather than from each other:
/// a downstream module that needs an upstream answer (ModelSelection needs the VRAM tier) reads
/// the value, not the module that produced it.
/// </summary>
public sealed class WizardSelection
{
    /// <summary>
    /// Settable for the update path only, where <see cref="ExistingRun"/> rewrites it as answers come
    /// in. Torch and the Manager depend on a folder that is chosen after the workload.
    /// </summary>
    public required InstallationConfiguration Workload { get; set; }

    /// <summary>What Select multiple picked, each at its own version. Empty for a single workload.</summary>
    public IReadOnlyList<WorkloadChoice> Choices { get; init; } = [];

    /// <summary>Several workloads merged into <see cref="Workload"/>.</summary>
    public bool IsMultiple => Choices.Count > 0;

    /// <summary>The update path's tile that adds no workload.</summary>
    public bool UpdateOnly { get; init; }

    /// <summary>The update path's answers. Null on the other paths.</summary>
    public ExistingRun? Existing { get; set; }

    /// <summary>Which workload brought each model, for the labels on a merged list.</summary>
    public IReadOnlyDictionary<Guid, string> PackNameByModelId { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// The catalog <see cref="Workload"/> was read from, taken right after reading it (spec 7.2).
    /// The install report names this, not what is installed when Install is pressed: a channel
    /// switch can apply another catalog while the wizard is open. Null: read at install start.
    /// </summary>
    public InstalledCatalogReading? Catalog { get; init; }

    /// <summary>Which of the app's paths this run is on. It decides which modules run at all (<see cref="IWizardModule.UsedIn"/>).</summary>
    public WizardMode Mode { get; init; } = WizardMode.FreshInstall;

    /// <summary>Where the workload gets installed. Set by the InstallFolder module.</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Chosen VRAM tier in GB, 0 when the workload has no profiles.</summary>
    public int SelectedVramProfile { get; set; }

    /// <summary>Custom model library root, or null for the install's own models folder. Written by ComfyFoldersModule.</summary>
    public string? ModelBaseFolder { get; set; }

    /// <summary>Per-type folder overrides in effect — empty when the user opted out. Written by ComfyFoldersModule.</summary>
    public IReadOnlyDictionary<string, string> FolderPathOverrides { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Models only: false puts every file directly into <see cref="TargetFolder"/>. On the selection
    /// because the Content stage's scan needs it before Confirm. Written by ModelsOnlyFolderModule.
    /// </summary>
    public bool SortModelsIntoSubfolders { get; set; } = true;

    public WorkloadCapability Capabilities => WorkloadCapabilities.Detect(Workload);
}
