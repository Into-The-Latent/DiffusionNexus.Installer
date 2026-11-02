using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>A switch the user has been asked about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel, filtered for the installer; Switch applies exactly this.</param>
/// <param name="Warning">What the switch takes away, or null when it takes nothing away.</param>
/// <param name="Downloads">The target channel has no catalog on this PC yet, so Switch downloads its release.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning? Warning, bool Downloads = false)
{
    public CatalogChannel Target => Preview.Channel;
}
