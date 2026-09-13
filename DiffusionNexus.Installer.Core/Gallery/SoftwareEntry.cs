using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// One card on the welcome screen: a software and the workloads the catalog offers for it.
///
/// Carries no artwork path: the files live in the Electron project's wwwroot and naming them from
/// here would make this UI-agnostic half depend on another project's folder layout. The component
/// resolves the logo from <see cref="Type"/> (Electron/Services/SoftwareLogos.cs).
/// </summary>
public sealed record SoftwareEntry(
    RepositoryType Type,
    string DisplayName,
    IReadOnlyList<GalleryEntry> Workloads)
{
    public int WorkloadCount => Workloads.Count;

    /// <summary>The only workload, when there is exactly one. Null otherwise.</summary>
    public GalleryEntry? SingleWorkload => Workloads.Count == 1 ? Workloads[0] : null;

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
