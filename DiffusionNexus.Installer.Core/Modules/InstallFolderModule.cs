using DiffusionNexus.Installer.Core.Content;
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

    /// <summary>The panel shows the message under the folder box.</summary>
    public bool ShowsOwnValidation => true;

    private string _targetFolder = string.Empty;

    public string TargetFolder
    {
        get => _targetFolder;
        set
        {
            // Raw here so the text box never fights a keystroke; CLEANED everywhere it is acted
            // on. A pasted trailing space once made the destination line, the presence scan and
            // the pipeline disagree on which folder they meant.
            _targetFolder = value ?? string.Empty;
            // Pushed eagerly, not only from Contribute: the Content stage scans the install folder
            // for models already on disk before Confirm ever runs ToOptions.
            if (_selection is not null) _selection.TargetFolder = Folder;
        }
    }

    /// <summary>
    /// The folder as acted on: cleaned the way the output and library boxes clean theirs
    /// (FolderInput.Clean), so a path pasted with Explorer's "Copy as path" quotes is the same
    /// folder here as there -- they are checked against this one. Empty while none is chosen.
    /// </summary>
    private string Folder => FolderInput.Clean(_targetFolder) ?? string.Empty;

    /// <summary>
    /// The folder the install will actually create: the chosen folder plus the repository's own
    /// folder name, derived the way the pipeline derives it. Null while no folder is chosen. Shown
    /// under the box so the user sees "E:\Installer\9\ComfyUI" before Next, not from an error.
    /// Null too for a folder <see cref="ShapeProblem"/> refuses: "Will be created: AI\ComfyUI"
    /// under "Enter the full path" would contradict the message.
    /// </summary>
    public string? DestinationFolder =>
        _selection is null || Folder.Length == 0 || ShapeProblem(Folder) is not null
            ? null
            : RepositoryPaths.Resolve(_selection.Workload, Folder);

    /// <summary>
    /// Why the folder cannot be used, judged on its text alone: the checks every folder box runs,
    /// then what the start scripts cannot carry. run_nvidia.bat runs setlocal
    /// enabledelayedexpansion before call "%~dp0venv\...": a lone ! in the expanded install
    /// folder is dropped, so E:\AI!new\... becomes E:\AInew\... and the venv is never found.
    /// &amp; % ^ are harmless there (quoted, and not expanded a second time).
    /// </summary>
    private static string? ShapeProblem(string folder) =>
        FolderInput.ShapeProblem(folder, @"Enter the full path of the install folder, for example D:\AI.")
        ?? (folder.Contains('!')
            ? "The start scripts cannot work in a folder whose path contains !. Choose a folder without it."
            : null);

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
            _selection.TargetFolder = Folder;
    }

    /// <summary>Remembers the install folder for the next run. Re-reads settings first: another module may have just saved.</summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        var folder = Folder;
        if (folder.Length == 0) return;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        user.DefaultTargetInstallFolder = folder;
        await settings.SaveAsync(user, ct).ConfigureAwait(false);
    }

    public ModuleValidation Validate()
    {
        var folder = Folder;
        if (folder.Length == 0)
            return ModuleValidation.Error("Choose a folder to install into.");

        // Before any disk access: a relative folder installed under the app's working directory,
        // and a bare "\\nas" made the folder check wait on the network for it.
        if (ShapeProblem(folder) is { } problem)
            return ModuleValidation.Error(problem);

        if (_selection is null)
            return ModuleValidation.Ok();

        if (!string.Equals(_validatedPath, folder, StringComparison.Ordinal))
        {
            _validatedPath = folder;
            _validationError = CheckTargetFolder(folder);
        }

        return _validationError is null
            ? ModuleValidation.Ok()
            : ModuleValidation.Error(_validationError);
    }

    private string? CheckTargetFolder(string folder)
    {
        try
        {
            // The validator appends the repository name itself, so it gets the folder that name is
            // appended to by the pipeline too: "E:\AI\ComfyUI" installs into E:\AI\ComfyUI, and
            // checking E:\AI\ComfyUI\ComfyUI instead let Next through onto a live installation.
            var result = preInstallation.ValidateTargetFolder(
                _selection!.Workload,
                RepositoryPaths.NormalizedTarget(_selection.Workload, folder),
                InstallationType.FullInstall);

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
