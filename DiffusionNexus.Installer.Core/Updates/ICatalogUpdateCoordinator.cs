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
    /// <summary>The channel the next check reads. Resolved env var → saved setting → Stable.</summary>
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

    /// <summary>Never throws. A check already in flight is joined, not repeated; while an apply runs it is a no-op that returns at once.</summary>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>Never throws. A no-op unless <see cref="CanApply"/>.</summary>
    Task ApplyAsync(CancellationToken ct = default);

    /// <summary>Saves the preference and forgets the last check. Refused (no-op) while checking or applying. Settings I/O errors propagate.</summary>
    Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default);
}
