using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Custom model base folder and custom output folder.
/// <para>
/// The model folder writes extra_model_paths.yaml, which both ComfyUI and AI-Toolkit post-install
/// handlers honour. The output folder becomes --output-directory in the generated ComfyUI
/// launcher script, so it exists for ComfyUI only.
/// </para>
/// </summary>
public sealed class ComfyFoldersModule(IUserSettingsRepository settings) : IWizardModule
{
    public string Id => "comfy-folders";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.ComfyFolders;

    public string ModelBaseFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public bool OverwriteExtraModelPaths { get; set; }

    /// <summary>True for ComfyUI only. The UI hides the output-folder field when false.</summary>
    public bool SupportsOutputFolder { get; private set; }

    public bool AppliesTo(WizardSelection selection) =>
        selection.Workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        SupportsOutputFolder = selection.Workload.Repository.Type == RepositoryType.ComfyUI;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        ModelBaseFolder = user.DefaultModelBaseFolder;
        OutputFolder = SupportsOutputFolder ? user.OutputFolder : string.Empty;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        var model = string.IsNullOrWhiteSpace(ModelBaseFolder) ? null : ModelBaseFolder;
        draft.ModelBaseFolder = model;

        // Generating the YAML without a base folder would write an empty mapping, so the two
        // travel together.
        draft.GenerateExtraModelPaths = model is not null;
        draft.OverwriteExtraModelPaths = model is not null && OverwriteExtraModelPaths;

        draft.OutputFolder = SupportsOutputFolder && !string.IsNullOrWhiteSpace(OutputFolder)
            ? OutputFolder
            : null;
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
