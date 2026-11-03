namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>Position within one wizard run. Navigation only ever visits planned stages.</summary>
public sealed class WizardRun
{
    private readonly WizardPlan _plan;
    private int _index;

    /// <param name="startAt">
    /// Stage to open on. Used to resume a run whose install is already under way: a Blazor circuit
    /// reconnect rebuilds this component, and opening on the first stage would hide a running
    /// install behind screens the user already filled in. Ignored when the plan has no such stage.
    /// </param>
    public WizardRun(WizardPlan plan, WizardStage? startAt = null)
    {
        _plan = plan;

        if (startAt is not { } stage) return;

        for (var i = 0; i < plan.Stages.Count; i++)
        {
            if (plan.Stages[i] != stage) continue;
            _index = i;
            return;
        }
    }

    public WizardPlan Plan => _plan;

    public WizardStage CurrentStage => _plan.Stages[_index];

    public IReadOnlyList<IWizardModule> CurrentModules => _plan.Modules(CurrentStage);

    public IReadOnlyList<string> ValidationErrors =>
        CurrentModules.Select(m => m.Validate())
            .Where(v => !v.IsValid)
            .Select(v => v.ErrorMessage!)
            .ToList();

    public bool CanGoNext => _index < _plan.Stages.Count - 1 && ValidationErrors.Count == 0;

    /// <summary>Once the install has started there is nothing to go back to.</summary>
    public bool CanGoBack => _index > 0 && CurrentStage != WizardStage.Install;

    public bool TryNext()
    {
        if (!CanGoNext) return false;

        if (_plan.Selection.Mode == WizardMode.UpdateExisting)
        {
            // By stage, not by position: the list may have just lost or gained a stage, even this one.
            var current = CurrentStage;
            _plan.RefreshStages();
            _index = _plan.Stages.TakeWhile(s => s <= current).Count();
            if (CurrentStage == WizardStage.Install) _plan.Selection.Existing?.Freeze();
            return true;
        }

        _index++;
        return true;
    }

    public void Back()
    {
        if (CanGoBack) _index--;
    }
}
