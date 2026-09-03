using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>One editable row of the "Advanced: custom model folders" list.</summary>
public sealed class FolderTypeRow
{
    internal FolderTypeRow(FolderTypeDefinition definition)
    {
        Key = definition.Key;
        Label = definition.Label;
        Standard = definition.Standard;
        Value = definition.Standard;
    }

    public string Key { get; }
    public string Label { get; }
    public string Standard { get; }

    /// <summary>What the user typed, untrimmed so the text box never fights their keystrokes.</summary>
    public string Value { get; internal set; }

    /// <summary>The name this row contributes, or null when it is blank or just the standard name.</summary>
    internal string? Override
    {
        get
        {
            var trimmed = Value.Trim();
            return trimmed.Length == 0 || string.Equals(trimmed, Standard, StringComparison.Ordinal) ? null : trimmed;
        }
    }
}

/// <summary>One editable "additional folder": a ComfyUI folder name and the folder it maps to.</summary>
public sealed class AdditionalFolderRow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string BaseName { get; set; } = string.Empty;
    public string MapsTo { get; set; } = string.Empty;

    /// <summary>A row is only usable with both halves; a name without a path would write a dangling YAML entry.</summary>
    internal bool IsComplete => !string.IsNullOrWhiteSpace(BaseName) && !string.IsNullOrWhiteSpace(MapsTo);

    internal AdditionalFolder ToModel(Guid ownerId) => new()
    {
        Id = Id,
        BaseName = BaseName.Trim(),
        MapsTo = MapsTo.Trim(),
        UserSettingsId = ownerId,
    };
}

/// <summary>
/// Custom model base folder and custom output folder, plus the advanced per-type folder names and
/// additional folders that the classic 1.x Folder Settings window offered.
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

    private WizardSelection? _selection;
    private UserSettings? _user;
    private string _modelBaseFolder = string.Empty;
    private readonly List<FolderTypeRow> _folderTypes = [];
    private readonly List<AdditionalFolderRow> _additionalFolders = [];

    public string ModelBaseFolder
    {
        get => _modelBaseFolder;
        set { _modelBaseFolder = value; SyncSelection(); }
    }

    public string OutputFolder { get; set; } = string.Empty;
    public bool OverwriteExtraModelPaths { get; set; }

    /// <summary>True for ComfyUI only. The UI hides the output-folder field when false.</summary>
    public bool SupportsOutputFolder { get; private set; }

    /// <summary>The per-type folder names, in display order. Edit through <see cref="SetFolderType"/>.</summary>
    public IReadOnlyList<FolderTypeRow> FolderTypes => _folderTypes;

    /// <summary>
    /// The per-type names that differ from ComfyUI's standard ones (loras -> "Lora", ...). Only
    /// these go into extra_model_paths.yaml and the model-presence scan; a row left at the standard
    /// name changes nothing and is not reported as custom.
    /// </summary>
    public IReadOnlyDictionary<string, string> FolderPathOverrides
    {
        get
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _folderTypes)
            {
                if (row.Override is { } value) overrides[row.Key] = value;
            }
            return overrides;
        }
    }

    /// <summary>Extra roots the user registered. Feeds the same YAML and ModelDestinationResolver.</summary>
    public IReadOnlyList<AdditionalFolderRow> AdditionalFolders => _additionalFolders;

    /// <summary>Whether anything in the advanced section changes the install; the panel flags it on the closed toggle.</summary>
    public bool HasCustomFolders =>
        _folderTypes.Any(r => r.Override is not null) || _additionalFolders.Any(r => r.IsComplete);

    public bool AppliesTo(WizardSelection selection) =>
        selection.Workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;

        SupportsOutputFolder = selection.Workload.Repository.Type == RepositoryType.ComfyUI;

        // Reset everything this module owns, including the fields below that are not read from
        // settings, so a re-initialized instance never carries a previous workload's answer.
        OverwriteExtraModelPaths = false;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        _user = user;
        ModelBaseFolder = user.DefaultModelBaseFolder;
        OutputFolder = SupportsOutputFolder ? user.OutputFolder : string.Empty;

        var saved = UserModelFolderMap.Build(user);
        _folderTypes.Clear();
        foreach (var type in UserModelFolderMap.FolderTypes)
        {
            var row = new FolderTypeRow(type);
            if (saved.TryGetValue(type.Key, out var value)) row.Value = value;
            _folderTypes.Add(row);
        }

        _additionalFolders.Clear();
        foreach (var folder in user.additionalFolders ?? [])
        {
            _additionalFolders.Add(new AdditionalFolderRow
            {
                Id = folder.Id == Guid.Empty ? Guid.NewGuid() : folder.Id,
                BaseName = folder.BaseName ?? string.Empty,
                MapsTo = folder.MapsTo ?? string.Empty,
            });
        }

        SyncSelection();
    }

    /// <summary>Sets one per-type folder name. Blank means "use the standard name".</summary>
    public void SetFolderType(string key, string value)
    {
        var row = _folderTypes.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown folder type '{key}'.", nameof(key));
        row.Value = value ?? string.Empty;
        SyncSelection();
    }

    /// <summary>Puts every per-type row back to ComfyUI's standard name.</summary>
    public void ResetFolderTypesToStandard()
    {
        foreach (var row in _folderTypes) row.Value = row.Standard;
        SyncSelection();
    }

    public AdditionalFolderRow AddAdditionalFolder()
    {
        var row = new AdditionalFolderRow();
        _additionalFolders.Add(row);
        return row;
    }

    public void RemoveAdditionalFolder(AdditionalFolderRow row) => _additionalFolders.Remove(row);

    /// <summary>
    /// Writes the page's answers back to user settings so the next install starts from them,
    /// as the classic Folder Settings window's Save did. No-op for a workload this module does
    /// not apply to: the registry initializes every module, and a Fooocus install must not rewrite
    /// the ComfyUI folder settings with whatever this instance was seeded with.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (_selection is null || _user is null || !AppliesTo(_selection)) return;

        _user.DefaultModelBaseFolder = ModelBaseFolder.Trim();
        if (SupportsOutputFolder) _user.OutputFolder = OutputFolder.Trim();
        UserModelFolderMap.Apply(_user, FolderPathOverrides);
        _user.additionalFolders = _additionalFolders
            .Where(r => r.IsComplete)
            .Select(r => r.ToModel(_user.UserId))
            .ToList();

        await settings.SaveAsync(_user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the answers the Content stage needs onto the selection. Only when this module applies:
    /// the registry initializes every module, and a saved library pushed into a Fooocus selection
    /// would make the model scan look in a folder that install never reads.
    /// </summary>
    private void SyncSelection()
    {
        if (_selection is null || !AppliesTo(_selection)) return;

        _selection.ModelBaseFolder = string.IsNullOrWhiteSpace(_modelBaseFolder) ? null : _modelBaseFolder;
        _selection.FolderPathOverrides = FolderPathOverrides;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        var model = string.IsNullOrWhiteSpace(ModelBaseFolder) ? null : ModelBaseFolder;
        draft.ModelBaseFolder = model;

        // Generating the YAML without a base folder would write an empty mapping, so the two
        // travel together.
        draft.GenerateExtraModelPaths = model is not null;
        draft.OverwriteExtraModelPaths = model is not null && OverwriteExtraModelPaths;

        draft.FolderPathOverrides.Clear();
        foreach (var (key, value) in FolderPathOverrides)
            draft.FolderPathOverrides[key] = value;

        draft.AdditionalFolders.Clear();
        draft.AdditionalFolders.AddRange(_additionalFolders
            .Where(r => r.IsComplete)
            .Select(r => r.ToModel(_user?.UserId ?? Guid.Empty)));

        draft.OutputFolder = SupportsOutputFolder && !string.IsNullOrWhiteSpace(OutputFolder)
            ? OutputFolder
            : null;
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
