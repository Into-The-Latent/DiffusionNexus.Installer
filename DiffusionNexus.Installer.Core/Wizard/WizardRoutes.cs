namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The wizard's address for a workload on each path. Its own route rather than a query, because the
/// way back from a side trip keeps paths only (ReturnTarget): a query would lose the path.
/// </summary>
public static class WizardRoutes
{
    /// <summary>The update path's tile that adds no workload.</summary>
    public const string UpdateOnly = "/existing/update/only";

    public static string Install(Guid workloadId, WizardMode mode) => mode switch
    {
        WizardMode.FreshInstall => $"/install/{workloadId}",
        WizardMode.ModelsOnly => $"/existing/models/{workloadId}",
        WizardMode.UpdateExisting => $"/existing/update/{workloadId}",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    /// <summary>The workload screen of a path that has its own.</summary>
    public static string Workloads(WizardMode mode) => mode switch
    {
        WizardMode.ModelsOnly => "/existing/models",
        WizardMode.UpdateExisting => "/existing/update",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    /// <summary>The wizard for what Select multiple picked. The picks are in <see cref="WorkloadPicks"/>.</summary>
    public static string Selected(WizardMode mode) => Workloads(mode) + "/selected";

    /// <summary>Which wizard a selection belongs to.</summary>
    public static string For(WizardSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.UpdateOnly) return UpdateOnly;
        return selection.IsMultiple ? Selected(selection.Mode) : Install(selection.Workload.Id, selection.Mode);
    }
}
