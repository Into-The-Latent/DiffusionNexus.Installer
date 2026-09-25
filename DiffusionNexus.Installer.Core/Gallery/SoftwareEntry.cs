using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// One card on the welcome screen: a software and the workloads the catalog offers for it.
///
/// Carries no artwork path: the files live in the Electron project's wwwroot and naming them from
/// here would make this UI-agnostic half depend on another project's folder layout. The component
/// resolves the logo from <see cref="Type"/> (Electron/Services/SoftwareLogos.cs).
/// </summary>
/// <param name="Workloads">
/// Every offered workload, legacy ones included, in <see cref="GalleryBuilder"/>'s order, which puts
/// them after the current ones: the workload screen's "Show legacy workloads" switch reveals them
/// from this list. Everything the card itself says is counted over <see cref="CurrentWorkloads"/>
/// instead, because that switch starts off.
///
/// The switch is the only way to a legacy pack, so a legacy pack is only reachable where the card
/// opens the workload screen: a software with two or more current workloads (today, ComfyUI). One
/// current workload still goes straight to setup, and none gets no card at all
/// (<see cref="SoftwareGalleryBuilder"/>) -- the design as approved, which leaves a legacy sibling
/// of either out of reach.
/// </param>
public sealed record SoftwareEntry(
    RepositoryType Type,
    string DisplayName,
    IReadOnlyList<GalleryEntry> Workloads)
{
    /// <summary>
    /// The workloads the workload screen shows by default: every one not marked legacy.
    ///
    /// Computed on each read rather than cached. A record copies every field on <c>with</c> and
    /// compares every field in Equals, so a cached list would go stale under
    /// <c>with { Workloads = ... }</c> and make two equal entries unequal. The lists are a
    /// software's workloads -- a dozen at most -- so the re-filter costs nothing worth that.
    /// </summary>
    public IReadOnlyList<GalleryEntry> CurrentWorkloads =>
        Workloads.Where(e => !e.Workload.IsLegacy).ToList();

    /// <summary>Whether the workload screen has anything for its "Show legacy workloads" switch to reveal.</summary>
    public bool HasLegacyWorkloads => Workloads.Any(e => e.Workload.IsLegacy);

    public int WorkloadCount => CurrentWorkloads.Count;

    /// <summary>The only current workload, when there is exactly one. Null otherwise.</summary>
    public GalleryEntry? SingleWorkload => CurrentWorkloads is [var only] ? only : null;

    /// <summary>
    /// True when picking this card should open the wizard directly. A screen that asks the user
    /// to choose from a list of one is a screen that should not exist.
    ///
    /// Installability is part of the question, not a separate one: a single workload that cannot
    /// be installed goes NOWHERE -- the card has no anchor at all -- so a card claiming "straight
    /// to setup" directly above the reason it cannot be set up contradicts itself.
    /// </summary>
    public bool GoesStraightToSetup => SingleWorkload is { IsInstallable: true };
}
