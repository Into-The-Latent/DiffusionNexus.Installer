using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>The screens and modules one wizard run will actually show.</summary>
public sealed class WizardPlan
{
    private readonly IReadOnlyList<IWizardModule> _modules;
    private Dictionary<WizardStage, IReadOnlyList<IWizardModule>> _byStage = [];

    /// <param name="selection">What the wizard has learned.</param>
    /// <param name="modules">Every initialized module of this path, applicable or not.</param>
    internal WizardPlan(WizardSelection selection, IReadOnlyList<IWizardModule> modules)
    {
        Selection = selection;
        _modules = modules;
        RefreshStages();
    }

    public WizardSelection Selection { get; }

    /// <summary>Ordered, and containing only stages that have something to show.</summary>
    public IReadOnlyList<WizardStage> Stages { get; private set; } = [];

    /// <summary>
    /// Asks the modules again whether they apply. For the update path, where the answers that decide
    /// it (the install's torch, the Update checkbox) come after the plan is built. Other paths decide
    /// once, at build, and <see cref="WizardRun"/> never calls this on them.
    /// </summary>
    public void RefreshStages()
    {
        var byStage = _modules
            .Where(m => m.AppliesTo(Selection))
            .GroupBy(m => m.Stage)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IWizardModule>)g.OrderBy(m => m.Order).ToList());

        // Confirm and Install always run. TryAdd so a module targeting one of them is kept.
        byStage.TryAdd(WizardStage.Confirm, []);
        byStage.TryAdd(WizardStage.Install, []);

        _byStage = byStage;
        Stages = byStage.Keys.OrderBy(s => (int)s).ToList();
    }

    public IReadOnlyList<IWizardModule> Modules(WizardStage stage) =>
        _byStage.TryGetValue(stage, out var modules) ? modules : [];

    public IEnumerable<IWizardModule> AllModules => _byStage.Values.SelectMany(m => m);

    public IReadOnlyList<ModuleValidation> Validate() =>
        AllModules.Select(m => m.Validate()).Where(v => !v.IsValid).ToList();

    /// <summary>Folds every module's answers into the SDK options record. Called once, at Confirm.</summary>
    public InstallationOptions ToOptions()
    {
        var draft = new InstallationOptionsDraft();

        // Seeded BEFORE the loop, not after it. A module contributes through Contribute -- that is
        // the whole contract -- so a slice-2 VramProfileModule writing draft.SelectedVramProfile
        // would be overwritten by this line if it ran last, silently reinstating the exact wrong
        // install (every tier's variant at no tier) that the VramProfile gate exists to prevent.
        draft.SelectedVramProfile = Selection.SelectedVramProfile;
        draft.DownloadModelsOnly = Selection.Mode == WizardMode.ModelsOnly;

        foreach (var module in AllModules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            module.Contribute(draft);

        return draft.ToOptions();
    }
}
