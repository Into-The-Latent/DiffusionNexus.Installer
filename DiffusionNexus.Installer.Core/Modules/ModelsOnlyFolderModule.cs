using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>One line of the "where your models will go" list. An empty subfolder means the chosen folder itself.</summary>
public sealed record ModelsOnlyPreviewRow(string Subfolder, string ModelName);

/// <summary>
/// The folder a models-only run downloads into, and whether models are sorted into subfolders.
/// <para>
/// The user is in full charge here, so nothing about the folder's contents is checked: only a blank
/// or malformed path is refused, because a relative one would land under the app's working directory.
/// Nothing is remembered either. The settings file has only the fresh install's folders to write to,
/// and writing either would change what the next fresh install starts from.
/// </para>
/// </summary>
public sealed class ModelsOnlyFolderModule : IWizardModule
{
    private WizardSelection? _selection;
    private string _folder = string.Empty;
    private bool _sortIntoSubfolders = true;

    public string Id => "models-only-folder";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    /// <summary>The panel shows the message under the folder box.</summary>
    public bool ShowsOwnValidation => true;

    public bool UsedIn(WizardMode mode) => mode == WizardMode.ModelsOnly;

    public bool AppliesTo(WizardSelection selection) => true;

    /// <summary>As typed, so the text box never fights a keystroke; cleaned wherever it is acted on.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            _folder = value ?? string.Empty;
            SyncSelection();
        }
    }

    public bool SortIntoSubfolders
    {
        get => _sortIntoSubfolders;
        set
        {
            _sortIntoSubfolders = value;
            SyncSelection();
        }
    }

    /// <summary>
    /// Where each enabled model will go, by model name: the file name depends on the memory size,
    /// which the next stage asks for. Empty until there is a folder the box accepts.
    /// </summary>
    public IReadOnlyList<ModelsOnlyPreviewRow> Preview
    {
        get
        {
            if (_selection is null || Problem is not null) return [];

            var folder = CleanFolder;
            return _selection.Workload.ModelDownloads
                .Where(m => m.Enabled)
                .Select(m =>
                {
                    var destination = ModelDestinationResolver.ResolveDownloadOnly(
                        _selection.Workload, m, m.DownloadLinks.FirstOrDefault(l => l.Enabled), folder, _sortIntoSubfolders);
                    var relative = Path.GetRelativePath(folder, destination);
                    return new ModelsOnlyPreviewRow(relative == "." ? string.Empty : relative, m.Name);
                })
                .ToList();
        }
    }

    private string CleanFolder => FolderInput.Clean(_folder) ?? string.Empty;

    private string? Problem => CleanFolder.Length == 0
        ? "Choose a folder to download into."
        : FolderInput.ShapeProblem(CleanFolder, @"Enter the full path of the download folder, for example D:\AI\models.");

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        _folder = string.Empty;
        _sortIntoSubfolders = true;
        SyncSelection();
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        // The folder is not an InstallationOptions field: the orchestrator takes it as an argument.
        SyncSelection();
        draft.SortModelsIntoSubfolders = _sortIntoSubfolders;
    }

    public ModuleValidation Validate() =>
        Problem is { } problem ? ModuleValidation.Error(problem) : ModuleValidation.Ok();

    /// <summary>Pushed eagerly: the Content stage scans for models already on disk before Confirm ever runs ToOptions.</summary>
    private void SyncSelection()
    {
        if (_selection is null) return;

        _selection.TargetFolder = CleanFolder;
        _selection.SortModelsIntoSubfolders = _sortIntoSubfolders;
    }
}
