namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>A workload ticked on the workload screen, and the version chosen for it.</summary>
public sealed record WorkloadPick(Guid WorkloadId, int VramGb);

/// <summary>
/// What Select multiple handed to the wizard. Not in the route: the way back from a side trip keeps
/// paths only (ReturnTarget). A singleton, like the session: this is a one-user desktop app, and a
/// circuit reconnect must not forget the picks.
/// </summary>
public sealed class WorkloadPicks
{
    private readonly Dictionary<WizardMode, IReadOnlyList<WorkloadPick>> _picks = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<WorkloadPick> Get(WizardMode mode)
    {
        lock (_gate) return _picks.TryGetValue(mode, out var picks) ? picks : [];
    }

    public void Set(WizardMode mode, IReadOnlyList<WorkloadPick> picks)
    {
        ArgumentNullException.ThrowIfNull(picks);
        lock (_gate) _picks[mode] = picks;
    }
}
