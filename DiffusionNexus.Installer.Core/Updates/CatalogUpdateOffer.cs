using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// The one rule for what the home page offers, as pure functions of the coordinator's state -- used by
/// <see cref="CatalogUpdateCoordinator"/> and by the test stub, so page tests see the real rule (#53).
/// </summary>
public static class CatalogUpdateOffer
{
    /// <summary>
    /// The followed channel's folder is behind its release, whoever the changes are for. A switch
    /// preview whose apply failed counts too, whatever it said: the SDK found the target folder needs
    /// the release (it tried to fill it), and applying the preview again is the retry. Not while a
    /// switch waits for an answer (the check describes the channel being left), nor once applied.
    /// </summary>
    public static bool ReleaseAvailable(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed) =>
        !pendingSwitch && phase != CatalogUpdatePhase.Applied && check is not null
        && (check.Outcome == CatalogUpdateOutcome.UpdatesAvailable || (check.IsSwitchPreview && lastApplyFailed));

    /// <summary>
    /// Announced and offered: the release changes something the installer shows; or a failed apply is
    /// retried whoever the changes were for; or the release completes a switch that did not land.
    /// <paramref name="switchIncomplete"/> is a function: it is the costliest term and rarely needed.
    /// </summary>
    public static bool Offered(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed, Func<bool> switchIncomplete)
    {
        ArgumentNullException.ThrowIfNull(switchIncomplete);
        return ReleaseAvailable(check, phase, pendingSwitch, lastApplyFailed)
            && (check!.HasChanges || lastApplyFailed || switchIncomplete());
    }
}
