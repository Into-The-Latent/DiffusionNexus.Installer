using DiffusionNexus.Installer.Core.Content;
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
/// The model folder writes extra_model_paths.yaml and the output folder becomes
/// --output-directory in the generated launcher script. Both are ComfyUI mechanisms, which is why
/// this is a ComfyUI-only module -- see <see cref="AppliesTo"/>.
/// </para>
/// <para>
/// Everything about model folders sits behind <see cref="UseModelLibraryFolder"/>, and the output
/// folder behind its own <see cref="UseOwnOutputFolder"/>; both are off unless a folder is saved.
/// </para>
/// </summary>
public sealed class ComfyFoldersModule(IUserSettingsRepository settings) : IWizardModule
{
    public string Id => "comfy-folders";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.ComfyFolders;

    /// <summary>The panel shows each switch's problem under its own box.</summary>
    public bool ShowsOwnValidation => true;

    private WizardSelection? _selection;
    private UserSettings? _user;
    private bool _useModelLibraryFolder;
    private string _modelBaseFolder = string.Empty;
    private readonly List<FolderTypeRow> _folderTypes = [];
    private readonly List<AdditionalFolderRow> _additionalFolders = [];

    // What the advanced section held when it was loaded. Persist only rewrites the per-type and
    // additional folders when they differ from this: a user who never opened Advanced must not
    // have stored fields blanked by a page they never saw.
    private Dictionary<string, string> _loadedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private List<(string BaseName, string MapsTo)> _loadedAdditional = [];

    /// <summary>
    /// The "use my own model folder" switch (issue #15). Its state is the remembered library: a
    /// saved library folder means on, none means off -- the owner's rule, and no separate flag.
    /// Off means ComfyUI's own models folders: the library folder, per-type names and additional
    /// folders below reach neither the selection nor the install and no extra_model_paths.yaml is
    /// written. Off also persists an EMPTY library folder (see <see cref="PersistAsync"/>): the
    /// switch derives from that field, so a folder left saved would switch it back on next start.
    /// For the same reason on needs a library (<see cref="ModelFolderProblem"/>): on with an empty
    /// box writes no YAML and is saved as off, and the next start would quietly drop any renamed
    /// folder types along with it.
    /// </summary>
    public bool UseModelLibraryFolder
    {
        get => _useModelLibraryFolder;
        set { _useModelLibraryFolder = value; SyncSelection(); }
    }

    public string ModelBaseFolder
    {
        get => _modelBaseFolder;
        set { _modelBaseFolder = value; SyncSelection(); }
    }

    /// <summary>
    /// The "use my own output folder" switch (issue #27), the same rule as
    /// <see cref="UseModelLibraryFolder"/>: a saved output folder means on, none means off. Off
    /// means ComfyUI's own output folder, no --output-directory in the launcher, and an EMPTY
    /// output folder persisted; the typed path is kept within the run. On needs a folder the
    /// launcher can carry (see <see cref="OutputFolderProblem"/>): on with an empty box would
    /// behave, and be saved, as off.
    /// </summary>
    public bool UseOwnOutputFolder { get; set; }

    public string OutputFolder { get; set; } = string.Empty;
    public bool OverwriteExtraModelPaths { get; set; }

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

    /// <summary>
    /// Whether anything in the advanced section changes the install; the panel flags it on the
    /// closed toggle. The library folder no longer counts: it sits under the switch, in plain sight.
    /// Nothing counts while the switch is off: none of it reaches the install.
    /// </summary>
    public bool HasCustomFolders =>
        UseModelLibraryFolder
        && (_folderTypes.Any(r => r.Override is not null)
            || _additionalFolders.Any(r => r.IsComplete));

    /// <summary>
    /// Where models land with the switch off: the repository's own models folder, which is what
    /// ModelDestinationResolver falls back to. Empty until an install folder is chosen. Named in the
    /// switch's off hint.
    /// </summary>
    public string DefaultModelsFolder => InstallSubfolder("models");

    /// <summary>ComfyUI's own output folder, the fallback with the output switch off.</summary>
    public string DefaultOutputFolder => InstallSubfolder("output");

    private string InstallSubfolder(string name) =>
        InstallRoot is { } root ? Path.Combine(root, name) : string.Empty;

    /// <summary>The folder the install creates (E:\Installer\9\ComfyUI), or null until an install folder is chosen.</summary>
    private string? InstallRoot =>
        _selection is not null && FolderInput.Clean(_selection.TargetFolder) is { } target
            ? RepositoryPaths.Resolve(_selection.Workload, target)
            : null;

    /// <summary>
    /// ComfyUI only.
    ///
    /// AI-Toolkit used to be here, for the model-folder half: the wizard collected a library
    /// folder and AIToolkitPostInstallHandler wrote an extra_model_paths.yaml into the clone.
    /// It did nothing. extra_model_paths.yaml is a ComfyUI convention and ostris/ai-toolkit does
    /// not read it -- the name does not occur anywhere in that repository. It resolves models
    /// through the MODELS_PATH environment variable (toolkit/paths.py), falling back to
    /// &lt;toolkit&gt;/models, and nothing in the SDK sets that variable. Nor was there a second
    /// route by which the answer could matter: the AI-Toolkit workload declares no model
    /// downloads, so there is nothing whose destination it could change.
    ///
    /// A control that changes nothing is worse than no control, so the panel is gone until the
    /// mechanism exists. Putting AI-Toolkit back means teaching the generated start script to
    /// export MODELS_PATH -- an SDK change, tracked separately.
    /// </summary>
    public bool AppliesTo(WizardSelection selection) =>
        selection.Workload.Repository.Type is RepositoryType.ComfyUI;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;

        // Reset everything this module owns, including the fields below that are not read from
        // settings, so a re-initialized instance never carries a previous workload's answer.
        OverwriteExtraModelPaths = false;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        _user = user;
        _useModelLibraryFolder = !string.IsNullOrWhiteSpace(user.DefaultModelBaseFolder);
        // ?? as for the additional folders below: System.Text.Json loads "OutputFolder": null as
        // null despite the non-nullable property, and the text boxes and setters expect a string.
        ModelBaseFolder = user.DefaultModelBaseFolder ?? string.Empty;
        OutputFolder = user.OutputFolder ?? string.Empty;
        UseOwnOutputFolder = !string.IsNullOrWhiteSpace(user.OutputFolder);

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

        _loadedOverrides = new Dictionary<string, string>(FolderPathOverrides, StringComparer.OrdinalIgnoreCase);
        _loadedAdditional = CompleteAdditionalFolders();

        SyncSelection();
    }

    /// <summary>Whether the per-type names or additional folders differ from what was loaded.</summary>
    public bool AdvancedEdited =>
        !DictionaryEquals(_loadedOverrides, FolderPathOverrides) || !_loadedAdditional.SequenceEqual(CompleteAdditionalFolders());

    private List<(string BaseName, string MapsTo)> CompleteAdditionalFolders() =>
        _additionalFolders.Where(r => r.IsComplete).Select(r => (r.BaseName.Trim(), r.MapsTo.Trim())).ToList();

    private static bool DictionaryEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));

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
        if (_selection is null || !AppliesTo(_selection)) return;

        // Re-read, never the copy from InitializeAsync: the install-folder module saves just
        // before this one, and writing a stale object back would undo it.
        // Off saves an empty library folder. The switch IS that field on the next start, so a
        // folder left saved behind an "off" would be on again; within this run the typed path is
        // kept so flipping the switch back on does not lose it. The output folder's switch works
        // the same way.
        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        user.DefaultModelBaseFolder = EffectiveModelBaseFolder ?? string.Empty;
        user.OutputFolder = EffectiveOutputFolder ?? string.Empty;

        if (AdvancedEdited)
        {
            UserModelFolderMap.Apply(user, FolderPathOverrides);
            user.additionalFolders = _additionalFolders
                .Where(r => r.IsComplete)
                .Select(r => r.ToModel(user.UserId))
                .ToList();
        }

        await settings.SaveAsync(user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the answers the Content stage needs onto the selection. Only when this module applies:
    /// the registry initializes every module, and a saved library pushed into a Fooocus selection
    /// would make the model scan look in a folder that install never reads.
    /// </summary>
    private void SyncSelection()
    {
        if (_selection is null || !AppliesTo(_selection)) return;

        _selection.ModelBaseFolder = EffectiveModelBaseFolder;
        _selection.FolderPathOverrides = UseModelLibraryFolder
            ? FolderPathOverrides
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // What the install sees and what is remembered, from one place each: null when the switch is
    // off or the box is blank, otherwise cleaned by FolderInput.Clean. The problem properties check
    // the same cleaned value, so what is refused is exactly what would have been used.

    /// <summary>The library folder as the install sees it: null when blank or when the switch is off.</summary>
    private string? EffectiveModelBaseFolder => Effective(UseModelLibraryFolder, _modelBaseFolder);

    /// <summary>The output folder as the install sees it: null when blank or when the switch is off.</summary>
    private string? EffectiveOutputFolder => Effective(UseOwnOutputFolder, OutputFolder);

    private static string? Effective(bool on, string folder) => on ? FolderInput.Clean(folder) : null;

    /// <summary>
    /// Characters a Windows folder name can hold but the generated run_nvidia.bat cannot carry in
    /// --output-directory. BatchScriptGenerator quotes the path only when it holds a space and runs
    /// the script under enabledelayedexpansion: an unquoted &amp; ends the command, ^ escapes the
    /// next character, and ! and % are expanded away. Refused here until the SDK's launcher quotes
    /// and escapes properly; the install would otherwise succeed and ComfyUI start writing
    /// somewhere else.
    /// </summary>
    private static readonly char[] LauncherBreakers = ['&', '!', '%', '^'];

    /// <summary>
    /// Why the output folder cannot be used, or null. Beyond the checks every folder gets (see
    /// <see cref="FolderProblem"/>), some characters break the start script.
    /// </summary>
    public string? OutputFolderProblem => FolderProblem(
        UseOwnOutputFolder, OutputFolder,
        blank: "Choose an output folder, or turn off \"Use my own output folder\".",
        notFull: @"Enter the full path of the output folder, for example D:\Renders.",
        carrierProblem: folder => folder.IndexOfAny(LauncherBreakers) >= 0
            ? "ComfyUI's start script cannot pass & ! % or ^ in a folder name. Choose a folder without them."
            : null);

    /// <summary>
    /// Why the library folder cannot be used, or null. Beyond the checks every folder gets (see
    /// <see cref="FolderProblem"/>): ExtraModelPathsYamlGenerator writes base_path unquoted, and
    /// YAML reads a space followed by # as the start of a comment, so "D:\AI #2\Models" would send
    /// ComfyUI to D:/AI -- refused until the SDK quotes the value.
    /// </summary>
    public string? ModelFolderProblem => FolderProblem(
        UseModelLibraryFolder, _modelBaseFolder,
        blank: "Choose your model library folder, or turn off \"Use my own model folder\".",
        notFull: @"Enter the full path of your model library, for example D:\Models.",
        carrierProblem: folder => folder.Contains(" #", StringComparison.Ordinal)
            ? "ComfyUI's model paths file cannot hold a space followed by # in a folder name. Choose a folder without it."
            : null);

    /// <summary>
    /// The checks both switches share, in order. Only with the switch on: on with an empty box
    /// would install and be saved as off while the screen says on. A relative path resolves where
    /// ComfyUI runs, not where the user meant; a Windows-invalid name is no folder at all; then
    /// whatever the folder's carrier (start script or YAML) cannot hold; and a folder inside the
    /// install is deleted with it.
    /// </summary>
    private string? FolderProblem(bool on, string typed, string blank, string notFull, Func<string, string?> carrierProblem)
    {
        if (!on) return null;
        if (FolderInput.Clean(typed) is not { } folder) return blank;
        if ((FolderInput.ShapeProblem(folder, notFull) ?? carrierProblem(folder)) is { } problem) return problem;
        if (InstallContaining(folder) is { } install)
            return $"This folder is inside the ComfyUI install ({install}) and would be deleted with it. Choose a folder outside it.";
        return null;
    }

    /// <summary>
    /// The install folder when <paramref name="folder"/> is that folder or inside it, otherwise
    /// null. It is the folder the install-folder question requires to be empty or new, so a
    /// reinstall means clearing it, and everything inside goes with it.
    /// </summary>
    private string? InstallContaining(string folder)
    {
        if (InstallRoot is not { } root) return null;

        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(folder);

        return full.Equals(install, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? install
            : null;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        var model = EffectiveModelBaseFolder;
        draft.ModelBaseFolder = model;

        // Generating the YAML without a base folder would write an empty mapping, so the two
        // travel together. With the switch off there is no base folder, so no YAML.
        draft.GenerateExtraModelPaths = model is not null;
        draft.OverwriteExtraModelPaths = model is not null && OverwriteExtraModelPaths;

        draft.FolderPathOverrides.Clear();
        draft.AdditionalFolders.Clear();

        if (UseModelLibraryFolder)
        {
            foreach (var (key, value) in FolderPathOverrides)
                draft.FolderPathOverrides[key] = value;

            draft.AdditionalFolders.AddRange(_additionalFolders
                .Where(r => r.IsComplete)
                .Select(r => r.ToModel(_user?.UserId ?? Guid.Empty)));
        }

        draft.OutputFolder = EffectiveOutputFolder;
    }

    /// <summary>
    /// The first of the two switches' problems. The panel shows each one under its own box, so the
    /// page leaves this module out of its list under the buttons.
    /// </summary>
    public ModuleValidation Validate() =>
        (OutputFolderProblem ?? ModelFolderProblem) is { } problem
            ? ModuleValidation.Error(problem)
            : ModuleValidation.Ok();
}
