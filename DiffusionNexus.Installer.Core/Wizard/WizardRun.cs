namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>Position within one wizard run. Navigation only ever visits planned stages.</summary>
public sealed class WizardRun(WizardPlan plan)
{
    private int _index;

    public WizardPlan Plan => plan;

    public WizardStage CurrentStage => plan.Stages[_index];

    public IReadOnlyList<IWizardModule> CurrentModules => plan.Modules(CurrentStage);

    public IReadOnlyList<string> ValidationErrors =>
        CurrentModules.Select(m => m.Validate())
            .Where(v => !v.IsValid)
            .Select(v => v.ErrorMessage!)
            .ToList();

    public bool CanGoNext => _index < plan.Stages.Count - 1 && ValidationErrors.Count == 0;

    /// <summary>Once the install has started there is nothing to go back to.</summary>
    public bool CanGoBack => _index > 0 && CurrentStage != WizardStage.Install;

    public bool TryNext()
    {
        if (!CanGoNext) return false;
        _index++;
        return true;
    }

    public void Back()
    {
        if (CanGoBack) _index--;
    }
}
