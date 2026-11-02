using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>A switch the user has been asked about and not answered yet.</summary>
/// <param name="Preview">
/// The check of the target channel, filtered for the installer. With shared folders Switch applies it only when
/// <paramref name="Downloads"/>, and for a channel with a folder it is a comparison of the two folders, with
/// nothing to download. With one folder Switch applies it, as that folder's update.
/// </param>
/// <param name="Warning">What the switch takes away, or null when it takes nothing away.</param>
/// <param name="Downloads">
/// The target channel has no catalog on this PC yet, so Switch downloads its release. While an install runs
/// Switch only saves the choice, and the download waits for Apply.
/// </param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning? Warning, bool Downloads = false)
{
    public CatalogChannel Target => Preview.Channel;
}
