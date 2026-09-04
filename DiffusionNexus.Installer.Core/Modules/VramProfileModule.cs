using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Which VRAM tier the install targets. The tier drives which per-tier model variants the SDK
/// downloads (ModelDownloadStepHandler via VramProfileHelper.SelectBestMatchingLinks) and what
/// the disk-space estimate counts. Only the tiers the workload declares are offered, lowest first
/// and preselected -- 1.x behaviour, and the user's explicit choice: no auto-detection.
/// </summary>
public sealed class VramProfileModule : IWizardModule
{
    private WizardSelection? _selection;
    private int _selectedTier;

    public string Id => "vram-profile";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.VramProfile;

    /// <summary>Ascending, distinct, from the catalog. Empty when the workload declares none.</summary>
    public IReadOnlyList<int> Tiers { get; private set; } = [];

    /// <summary>
    /// Chosen tier in GB, 0 when there are no tiers. Written through to the selection on every
    /// change so ModelSelection reads the value, never this module.
    /// </summary>
    public int SelectedTier
    {
        get => _selectedTier;
        set
        {
            _selectedTier = value;
            if (_selection is not null) _selection.SelectedVramProfile = value;
        }
    }

    /// <summary>Stateless: the same parser the gate uses, so Detect and AppliesTo agree by construction.</summary>
    public bool AppliesTo(WizardSelection selection) =>
        VramTiers.Parse(selection.Workload.Vram.VramProfiles).Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        Tiers = VramTiers.Parse(selection.Workload.Vram.VramProfiles);
        SelectedTier = Tiers.Count > 0 ? Tiers[0] : 0;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft) => draft.SelectedVramProfile = SelectedTier;

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
