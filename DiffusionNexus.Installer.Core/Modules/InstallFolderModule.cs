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

    // Validate() is called on every render, so it only reads this: the check touches the filesystem,
    // and an unreachable network path takes about 21 seconds to answer (#60). Started when the folder
    // changes and replaced by the next one, so an answer about a folder the user has left is never read.
    private Task<FolderAnswer>? _check;
    private string? _checkedPath;

    private sealed record FolderAnswer(string? Problem, bool TimedOut = false);

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
            StartCheckIfNew();
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
    /// Null too for a folder <see cref="FolderInput.InstallFolderProblem"/> refuses: "Will be
    /// created: AI\ComfyUI" under "Enter the full path" would contradict the message.
    /// </summary>
    public string? DestinationFolder =>
        _selection is null || Folder.Length == 0 || FolderInput.InstallFolderProblem(Folder) is not null
            ? null
            : RepositoryPaths.Resolve(_selection.Workload, Folder);

    /// <summary>
    /// How long a change of folder waits for the check. An ordinary folder answers well inside this, so
    /// the render that follows already has the answer and no "checking" state flickers past.
    /// </summary>
    public TimeSpan AnswerWait { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the check may take before the folder counts as a problem.</summary>
    public TimeSpan CheckLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The folder in the box has no answer yet, so <see cref="Validate"/> holds Next back.</summary>
    public bool IsCheckingFolder => _check is { IsCompleted: false };

    /// <summary>The folder in the box did not answer within <see cref="CheckLimit"/>; <see cref="CheckFolderAgain"/> asks again.</summary>
    public bool FolderCheckTimedOut => _check is { IsCompletedSuccessfully: true, Result.TimedOut: true };

    /// <summary>The folder in the box has its answer, whatever it says.</summary>
    public bool HasFolderAnswer => _check is { IsCompleted: true };

    /// <summary>
    /// The folder in the box got the answer it did not have when it changed. Raised off the page's
    /// thread; never for an answer that was there in time for the next render.
    /// </summary>
    public event Action? FolderCheckAnswered;

    /// <summary>The network drive may be back. Nothing else asks again: the folder in the box has not changed.</summary>
    public void CheckFolderAgain()
    {
        _checkedPath = null;
        StartCheckIfNew();
    }

    /// <summary>
    /// The folder may have been emptied or filled during the trip, and the box has not changed. A
    /// check that gets no answer says nothing new, so an earlier real answer stands.
    /// </summary>
    public Task RefreshAfterResumeAsync(CancellationToken ct = default)
    {
        _checkedPath = null;
        StartCheckIfNew(_check is { IsCompletedSuccessfully: true, Result.TimedOut: false } earlier ? earlier.Result : null);
        return Task.CompletedTask;
    }

    public bool AppliesTo(WizardSelection selection) => true;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        _check = null;
        _checkedPath = null;

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

        if (ProblemFromTheTextAlone(folder) is { } problem)
            return ModuleValidation.Error(problem);

        if (_check is not { } check)
            return ModuleValidation.Ok();

        if (!check.IsCompleted)
            return ModuleValidation.Error("Checking this folder...");

        return check.Result.Problem is { } found
            ? ModuleValidation.Error(found)
            : ModuleValidation.Ok();
    }

    /// <summary>What can be refused without touching the disk.</summary>
    private string? ProblemFromTheTextAlone(string folder)
    {
        // Before any disk access: a relative folder installed under the app's working directory,
        // and a bare "\\nas" made the folder check wait on the network for it.
        if (FolderInput.InstallFolderProblem(folder) is { } problem)
            return problem;

        if (_selection is null || !RepositoryPaths.EndsInRepositoryNameTwice(_selection.Workload, folder))
            return null;

        var install = RepositoryPaths.Resolve(_selection.Workload, folder);
        return $"This folder is never created: the software installs one level up, into {install}. Choose {Path.GetDirectoryName(install)} instead.";
    }

    private void StartCheckIfNew(FolderAnswer? ifNoAnswer = null)
    {
        var folder = Folder;
        if (string.Equals(_checkedPath, folder, StringComparison.Ordinal)) return;

        _checkedPath = folder;
        if (_selection is null || folder.Length == 0 || ProblemFromTheTextAlone(folder) is not null)
        {
            _check = null;
            return;
        }

        // Not again while the last folder has no answer: typing under a server that does not answer is
        // one new path per key, and waiting on each holds up every event behind it.
        var slowAlready = _check is { IsCompleted: false };
        var check = _check = AnswerWithinLimitAsync(folder, ifNoAnswer);
        if (slowAlready || !check.Wait(AnswerWait)) _ = AnnounceAsync(check);
    }

    private async Task<FolderAnswer> AnswerWithinLimitAsync(string folder, FolderAnswer? ifNoAnswer)
    {
        try
        {
            // Its own thread rather than the pool's: this blocks for as long as Windows takes, and one
            // new path per keystroke would otherwise queue behind the ones still waiting.
            var probe = Task.Factory.StartNew(() => CheckTargetFolder(folder),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return new FolderAnswer(await probe.WaitAsync(CheckLimit).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            // Not waited out and not adopted later: when Windows gives up on a computer that is off, it
            // answers "no such folder", the same as for a folder still to be created.
            return ifNoAnswer ?? new FolderAnswer(
                $"Windows did not answer about this folder within {CheckLimit.TotalSeconds:0} seconds. If it is on a network drive, make sure the drive is on and try again, or choose another folder.",
                TimedOut: true);
        }
        catch (Exception ex)
        {
            // A path the OS refuses to even look at is still a bad answer. Nobody awaits this but the
            // page's next render, which a thrown answer would take down.
            return new FolderAnswer($"That folder cannot be used: {ex.Message}");
        }
    }

    private async Task AnnounceAsync(Task<FolderAnswer> check)
    {
        await check.ConfigureAwait(false);
        if (ReferenceEquals(_check, check)) FolderCheckAnswered?.Invoke();
    }

    private string? CheckTargetFolder(string folder)
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
}
