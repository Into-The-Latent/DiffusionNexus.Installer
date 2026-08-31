using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Where the workload gets installed. Applies to everything.
/// <para>
/// Also the folder pre-flight. The Avalonia installer ran this through
/// IInstallationCoordinator.RunPreChecksAsync just before starting; a wizard can do better by
/// asking at the stage that owns the folder, so a non-empty target is a disabled Next rather than
/// an error two screens later. It matters: GitService treats an existing clone as success and logs
/// "Repository already exists ... Skipping clone", so without this check an install aimed at a
/// live installation quietly proceeds to reinstall torch into its venv and overwrite its launcher.
/// </para>
/// </summary>
public sealed class InstallFolderModule(
    IUserSettingsRepository settings,
    IPreInstallationService preInstallation) : IWizardModule
{
    private WizardSelection? _selection;

    // Validate() is called on every render, and the check touches the filesystem. Cached against
    // the exact path it was computed for, so typing a folder name is not one directory enumeration
    // per keystroke.
    private string? _validatedPath;
    private string? _validationError;

    public string Id => "install-folder";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public string TargetFolder { get; set; } = string.Empty;

    public bool AppliesTo(WizardSelection selection) => true;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        _validatedPath = null;
        _validationError = null;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        TargetFolder = user.DefaultTargetInstallFolder;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        // The target folder is not an InstallationOptions field — the orchestrator takes it as a
        // separate argument — so it lands on the selection instead.
        if (_selection is not null)
            _selection.TargetFolder = TargetFolder;
    }

    public ModuleValidation Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetFolder))
            return ModuleValidation.Error("Choose a folder to install into.");

        if (_selection is null)
            return ModuleValidation.Ok();

        if (!string.Equals(_validatedPath, TargetFolder, StringComparison.Ordinal))
        {
            _validatedPath = TargetFolder;
            _validationError = CheckTargetFolder(TargetFolder);
        }

        return _validationError is null
            ? ModuleValidation.Ok()
            : ModuleValidation.Error(_validationError);
    }

    private string? CheckTargetFolder(string folder)
    {
        try
        {
            var result = preInstallation.ValidateTargetFolder(
                _selection!.Workload, folder, InstallationType.FullInstall);

            if (result.CanProceed) return null;

            // ShouldSuggestModelsNodesOnly is the SDK's "there is already an install here, offer to
            // only add models/nodes to it" path. Slice 1 has no models-only mode to offer, so the
            // honest answer is to name the folder and let the user pick another one rather than
            // silently installing on top of a working installation.
            var where = result.FullTargetPath ?? folder;
            return result.ErrorMessage
                ?? $"'{where}' already exists and is not empty. Choose an empty or new folder.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A path the OS refuses to even look at is still a bad answer, but the message should
            // say so rather than take the wizard down.
            return $"That folder cannot be used: {ex.Message}";
        }
    }
}
