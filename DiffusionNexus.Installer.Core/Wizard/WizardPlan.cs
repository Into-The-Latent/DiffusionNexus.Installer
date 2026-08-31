using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>The screens and modules one wizard run will actually show.</summary>
public sealed class WizardPlan
{
    private readonly Dictionary<WizardStage, IReadOnlyList<IWizardModule>> _byStage;

    internal WizardPlan(WizardSelection selection, Dictionary<WizardStage, IReadOnlyList<IWizardModule>> byStage)
    {
        Selection = selection;
        _byStage = byStage;
        Stages = byStage.Keys.OrderBy(s => (int)s).ToList();
    }

    public WizardSelection Selection { get; }

    /// <summary>Ordered, and containing only stages that have something to show.</summary>
    public IReadOnlyList<WizardStage> Stages { get; }

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

        foreach (var module in AllModules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            module.Contribute(draft);

        return draft.ToOptions();
    }
}
