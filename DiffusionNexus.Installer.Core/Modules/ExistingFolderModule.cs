using System.Text.Json;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Which installed ComfyUI an update run works on, and what we found there. Validate() runs on every
/// render, so it only reads the last answer: the check starts torch in the venv, and a folder on a
/// computer that is off takes about 21 seconds to answer (#60). It is replaced by the next folder,
/// so an answer about a folder the user has left is never used.
/// </summary>
public sealed class ExistingFolderModule(IExistingComfyUIInspector inspector, IUserSettingsRepository settings) : IWizardModule
{
    private WizardSelection? _selection;
    private string _folder = string.Empty;
    private Task _check = Task.CompletedTask;
    private CancellationTokenSource? _checkCts;
    private readonly Lock _gate = new();
    private string? _checkFailure;

    public string Id => "existing-folder";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    /// <summary>The panel shows the message under the folder box.</summary>
    public bool ShowsOwnValidation => true;

    public bool UsedIn(WizardMode mode) => mode == WizardMode.UpdateExisting;

    public bool AppliesTo(WizardSelection selection) => true;

    /// <summary>As typed, so the text box never fights a keystroke; cleaned wherever it is acted on.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            _folder = value ?? string.Empty;
            StartCheck();
        }
    }

    /// <summary>The folder in the box has no answer yet.</summary>
    public bool IsChecking => !_check.IsCompleted;

    /// <summary>Completes when the folder in the box has its answer.</summary>
    public Task WhenAnswered => _check;

    /// <summary>The answer arrived after the render that followed the change. Raised off the page's thread.</summary>
    public event Action? FolderCheckAnswered;

    /// <summary>The folder holds something else that a fresh install would serve.</summary>
    public bool ShowsFreshInstallLink => Profile is { Support: not ExistingInstallSupport.Supported };

    private ExistingComfyUIProfile? Profile => _selection?.Existing?.Profile;

    private string CleanFolder => FolderInput.Clean(_folder) ?? string.Empty;

    private string? ShapeProblem => CleanFolder.Length == 0
        ? "Choose your ComfyUI folder."
        : FolderInput.ShapeProblem(CleanFolder, @"Enter the full path of your ComfyUI folder, for example D:\AI\ComfyUI.");

    /// <summary>What was found, label and value, for the panel.</summary>
    public IReadOnlyList<(string Label, string Value)> Summary => Profile is not { Support: ExistingInstallSupport.Supported } profile
        ? []
        :
        [
            ("ComfyUI folder", profile.RepositoryPath!),
            ("Python environment", profile.VenvPath!),
            ("Python", profile.PythonVersion ?? "Not found"),
            ("Torch", profile.Torch switch
            {
                null => "Not found",
                { CudaVersion: null } torch => $"{torch.TorchVersion}, CPU build",
                var torch => $"{torch.TorchVersion} with CUDA {torch.CudaVersion}",
            }),
            ("Update script", profile.UpdateScript switch
            {
                UpdateScriptState.Current => "Current",
                UpdateScriptState.Legacy => "An old one, which we'll replace",
                _ => "None, we'll add one",
            }),
            ("ComfyUI-Manager", profile.ManagerInstalled ? "Installed" : "Missing, we'll install it"),
        ];

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        Folder = user.LastExistingComfyUIFolder ?? string.Empty;
    }

    /// <summary>The install may have changed during the trip, and the box has not.</summary>
    public Task RefreshAfterResumeAsync(CancellationToken ct = default)
    {
        StartCheck(keepAnswer: true);
        return Task.CompletedTask;
    }

    /// <param name="keepAnswer">
    /// The same folder again, after a side trip: Confirm may be on screen, and it must still have a
    /// folder. The answer only changes if the install did.
    /// </param>
    private void StartCheck(bool keepAnswer = false)
    {
        if (_selection?.Existing is not { } run) return;

        CancellationTokenSource cts;
        lock (_gate)
        {
            _checkCts?.Cancel();
            _checkCts?.Dispose();
            _checkCts = cts = new CancellationTokenSource();
            _checkFailure = null;
        }

        if (!keepAnswer) run.Profile = null;

        if (ShapeProblem is not null)
        {
            _check = Task.CompletedTask;
            run.Checking(_check);
            return;
        }

        var folder = CleanFolder;
        var token = cts.Token;
        _check = Task.Run(async () =>
        {
            ExistingComfyUIProfile? profile = null;
            string? failure = null;
            try
            {
                profile = await inspector.InspectAsync(folder, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = $"This folder could not be checked: {ex.Message}";
            }

            lock (_gate)
            {
                // Another folder took over meanwhile: this answer is about a folder the user has left.
                if (token.IsCancellationRequested) return;
                _checkFailure = failure;
                if (!SameAnswer(run.Profile, profile)) run.Profile = profile;
            }
        });
        run.Checking(_check);
        _ = _check.ContinueWith(t => FolderCheckAnswered?.Invoke(), TaskScheduler.Default);
    }

    private static bool SameAnswer(ExistingComfyUIProfile? a, ExistingComfyUIProfile? b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (Profile is not { Support: ExistingInstallSupport.Supported }) return;

        // Re-read, never a copy from initialization: other modules save just before this one.
        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        user.LastExistingComfyUIFolder = CleanFolder;
        await settings.SaveAsync(user, ct).ConfigureAwait(false);
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        // The folder is not an InstallationOptions field: the orchestrator takes it as an argument,
        // and ExistingRun has put the repository on the selection.
    }

    public ModuleValidation Validate()
    {
        if (ShapeProblem is { } shape) return ModuleValidation.Error(shape);
        if (IsChecking) return ModuleValidation.Error("Checking this folder...");
        if (_checkFailure is { } failure) return ModuleValidation.Error(failure);

        return Profile switch
        {
            null => ModuleValidation.Error("Checking this folder..."),
            { Support: ExistingInstallSupport.Supported } => ModuleValidation.Ok(),
            var profile => ModuleValidation.Error(Existing.SupportMessages.For(profile.Support)),
        };
    }
}
