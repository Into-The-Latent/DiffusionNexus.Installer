// DiffusionNexus.Installer.Core/Modules/ModelSelectionModule.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>One model the workload declares. <see cref="Group"/> is the catalog destination, for display only.</summary>
public sealed class ModelRow(Guid id, string name, string group)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public bool IsSelected { get; set; } = true;
    public bool IsExisting { get; internal set; }
    public string? ExistingPath { get; internal set; }
}

public sealed record ModelGroup(string Name, IReadOnlyList<ModelRow> Rows);

/// <summary>
/// Which of the workload's models to download. 1.x behaviour: every enabled model listed and
/// ticked, grouped by destination, marked when already on disk, with a live disk-space estimate.
/// No tier annotation on the rows -- the tier silently drives the existence check and the
/// estimate, exactly as 1.x did (spec decision 2).
/// </summary>
public sealed class ModelSelectionModule(IModelPresenceScanner scanner, IDiskSpaceEstimator estimator) : IWizardModule
{
    public const string NotAssignedGroup = "Not assigned";

    private WizardSelection? _selection;
    private IReadOnlyList<ModelPresence> _presence = [];
    private readonly HashSet<string> _forceRedownloadUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trustedUrls = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "model-selection";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.ModelDownloads;

    /// <summary>Models and workflows are all ticked by default; they live behind Advanced.</summary>
    public bool IsAdvanced => true;

    public IReadOnlyList<ModelRow> Rows { get; private set; } = [];

    /// <summary>Grouped once in <see cref="InitializeAsync"/>: a row's group never changes, and the panel reads this on every render.</summary>
    public IReadOnlyList<ModelGroup> Groups { get; private set; } = [];

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public DiskSpaceEstimate? Estimate { get; private set; }
    public string? EstimateError { get; private set; }

    /// <summary>Tier the last presence scan used; -1 before any scan. The panel rescans when the selection's tier differs.</summary>
    public int LastScannedTier { get; private set; } = -1;

    /// <summary>Install folder the last presence scan used; the panel rescans only when tier or folder moved.</summary>
    public string? LastScannedFolder { get; private set; }

    /// <summary>Mirrors Detect (Count > 0), NOT Any(Enabled): the gate and the module must agree.</summary>
    public bool AppliesTo(WizardSelection selection) => selection.Workload.ModelDownloads.Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        // A repeated model id is a hand-authored catalog mistake. First entry wins HERE, at the
        // rows, not only at the presence lookup: two rows sharing an id would flip each other's
        // checkbox (SetSelected finds the first) and both show the first one's presence.
        Rows = selection.Workload.ModelDownloads
            .Where(m => m.Enabled)
            .DistinctBy(m => m.Id)
            .Select(m => new ModelRow(m.Id, m.Name, string.IsNullOrWhiteSpace(m.Destination) ? NotAssignedGroup : m.Destination))
            .ToList();
        Groups = Rows
            .GroupBy(r => r.Group)
            .OrderBy(g => g.Key == NotAssignedGroup)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelGroup(g.Key, g.ToList()))
            .ToList();
        _presence = [];
        _forceRedownloadUrls.Clear();
        _trustedUrls.Clear();
        Estimate = null;
        EstimateError = null;
        LastScannedTier = -1;
        LastScannedFolder = null;

        RefreshPresence();
        return Task.CompletedTask;
    }

    public void SetSelected(Guid id, bool selected)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row) row.IsSelected = selected;
    }

    /// <summary>Filesystem only, synchronous. Nothing to scan until an install folder is known.</summary>
    public void RefreshPresence()
    {
        if (_selection is null) return;

        var tier = _selection.SelectedVramProfile;
        LastScannedTier = tier;
        LastScannedFolder = _selection.TargetFolder;

        if (string.IsNullOrWhiteSpace(_selection.TargetFolder))
        {
            _presence = [];
            foreach (var row in Rows) { row.IsExisting = false; row.ExistingPath = null; }
            return;
        }

        _presence = scanner.Scan(new ModelScanRequest(
            _selection.Workload,
            RepositoryPaths.Resolve(_selection.Workload, _selection.TargetFolder),
            _selection.ModelBaseFolder,
            _selection.FolderPathOverrides,
            tier));

        // A repeated model id is a hand-authored catalog mistake, not an impossibility; the first
        // entry wins rather than the whole Content stage throwing.
        var byId = _presence.GroupBy(p => p.ModelId).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in Rows)
        {
            var found = byId.TryGetValue(row.Id, out var presence) && presence.AllPartsPresent;
            row.IsExisting = found;
            row.ExistingPath = found ? presence!.ExistingPath : null;
        }
    }

    /// <summary>Network-bound (HEAD per URL). Failure is reported through <see cref="EstimateError"/>, never thrown.</summary>
    public async Task RefreshEstimateAsync(CancellationToken ct = default)
    {
        if (_selection is null || string.IsNullOrWhiteSpace(_selection.TargetFolder))
        {
            Estimate = null;
            return;
        }

        try
        {
            Estimate = await estimator.EstimateAsync(new DiskSpaceRequest(
                _selection.Workload,
                _selection.TargetFolder,
                _selection.SelectedVramProfile,
                Rows.Where(r => !r.IsSelected).Select(r => r.Id).ToHashSet(),
                Rows.Where(r => r.IsExisting).Select(r => r.Id).ToHashSet(),
                _selection.ModelBaseFolder), ct).ConfigureAwait(false);
            EstimateError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Estimate = null;
            EstimateError = $"Could not estimate disk space: {ex.Message}";
        }
    }

    /// <summary>Files already on disk for ticked models — the pre-install verification's input.</summary>
    public IReadOnlyList<ModelFileTarget> ExistingTargetsForSelectedModels()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        return _presence
            .Where(p => selected.Contains(p.ModelId))
            .SelectMany(p => p.Targets)
            .Where(t => t.ExistingPath is not null)
            .ToList();
    }

    /// <summary>Records the mismatch dialog's answers. Keyed by URL, never by model id: a model can have several links and only some mismatch.</summary>
    public void ApplyVerification(IEnumerable<string> forceRedownloadUrls, IEnumerable<string> trustedUrls)
    {
        _forceRedownloadUrls.Clear();
        _trustedUrls.Clear();
        _forceRedownloadUrls.UnionWith(forceRedownloadUrls);
        _trustedUrls.UnionWith(trustedUrls);
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.ExcludedModelIds.Clear();
        foreach (var row in Rows.Where(r => !r.IsSelected)) draft.ExcludedModelIds.Add(row.Id);

        draft.ForceRedownloadUrls.Clear();
        draft.ForceRedownloadUrls.UnionWith(_forceRedownloadUrls);
        draft.TrustedUrls.Clear();
        draft.TrustedUrls.UnionWith(_trustedUrls);
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
