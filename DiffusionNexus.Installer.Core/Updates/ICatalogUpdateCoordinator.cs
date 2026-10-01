using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>Idle → Checking → Checked → Applying → Applied. A failed or cancelled apply returns to Checked.</summary>
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

/// <summary>
/// The catalog-update state the UI binds to. One process-wide instance, like the app updater's
/// <c>UpdaterLog</c>: pages subscribe to <see cref="Changed"/> (a plain Action they can
/// unsubscribe) and read the properties; they never own update state themselves.
/// </summary>
public interface ICatalogUpdateCoordinator
{
    /// <summary>
    /// The channel the next check reads -- the catalog's AND the app updater's; one setting on
    /// purpose, because a Preview catalog on a Stable app is the RequiresNewerSoftware outcome.
    /// Resolved env var → saved setting → Stable.
    /// </summary>
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }

    /// <summary>The SDK's local override folder, for the OverrideActive message. Null when none is configured.</summary>
    string? OverridePath { get; }

    CatalogUpdatePhase Phase { get; }
    CatalogUpdateCheck? LastCheck { get; }

    /// <summary>catalog-state.json as of the last check or apply. Null until the first check finishes.</summary>
    LocalCatalogState? Installed { get; }

    /// <summary>Non-null only while <see cref="Phase"/> is Applying.</summary>
    CatalogDownloadProgress? Progress { get; }
    CatalogApplyResult? LastApply { get; }

    bool UpdateAvailable { get; }
    bool CanApply { get; }

    /// <summary>Why Apply is withheld although an update is available (an install is running). Null otherwise.</summary>
    string? ApplyBlockedReason { get; }

    /// <summary>Raised after every state change (download progress: only when the shown percent moves). Handlers marshal to their own context.</summary>
    event Action? Changed;

    /// <summary>
    /// Resolves <see cref="Channel"/> without running a check, and returns it. Never throws: a
    /// settings file that cannot be read answers Stable. For the app updater, which follows the
    /// same channel but checks on its own schedule and must not wait for a catalog check.
    /// </summary>
    Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default);

    /// <summary>Never throws. A check already in flight is joined, not repeated; while an apply runs it is a no-op that returns at once.</summary>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>Never throws. A no-op unless <see cref="CanApply"/>.</summary>
    Task ApplyAsync(CancellationToken ct = default);

    /// <summary>
    /// A switch waiting for Switch or Keep: the target channel's content removes or changes
    /// something. While it is set, checks, applies and other switches are refused, and
    /// <see cref="UpdateAvailable"/> is false (the last check describes the channel being left).
    /// </summary>
    CatalogChannelSwitch? PendingSwitch { get; }

    /// <summary>
    /// What is installed is known to come from another channel than <see cref="Channel"/> -- a
    /// section's recorded channel, SDK 2.1.0; unknown is never a mismatch -- and no check has found
    /// it current: a switch whose content did not land (the preview or the apply failed, or an
    /// install held the apply). Read from catalog-state.json, so it survives a restart. False under
    /// an active override and while a switch waits for an answer.
    /// </summary>
    bool SwitchIncomplete { get; }

    /// <summary>
    /// Previews <paramref name="target"/> without saving anything (spec 7.1). A diff that removes
    /// or changes something waits in <see cref="PendingSwitch"/>; otherwise the switch goes ahead
    /// at once, as <see cref="ConfirmSwitchAsync"/> would. Refused (no-op) while checking,
    /// applying or switching. Settings I/O errors propagate, and then nothing was changed.
    /// Completes once the choice is saved (or waits for an answer); a download it starts runs on
    /// and reports through <see cref="Changed"/>, like <see cref="ApplyAsync"/>.
    /// </summary>
    Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default);

    /// <summary>
    /// "Switch": saves the preference, then starts the apply of the previewed check and completes;
    /// the download reports through <see cref="Changed"/>. A failed apply keeps the preference and
    /// the installed content (<see cref="SwitchIncomplete"/>). A no-op without a
    /// <see cref="PendingSwitch"/>. Settings I/O errors propagate, and then nothing was changed.
    /// </summary>
    Task ConfirmSwitchAsync(CancellationToken ct = default);

    /// <summary>"Keep": drops the <see cref="PendingSwitch"/>. Saves nothing.</summary>
    void KeepChannel();
}
