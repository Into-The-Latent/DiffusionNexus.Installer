namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The wizard's address for a workload on each path. Its own route rather than a query, because the
/// way back from a side trip keeps paths only (ReturnTarget): a query would lose the path.
/// </summary>
public static class WizardRoutes
{
    public static string Install(Guid workloadId, WizardMode mode) => mode switch
    {
        WizardMode.ModelsOnly => $"/existing/models/{workloadId}",
        _ => $"/install/{workloadId}"
    };
}
