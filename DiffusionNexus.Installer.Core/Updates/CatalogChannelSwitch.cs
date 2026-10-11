using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>A switch the user has been warned about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel, filtered for the installer; Switch applies exactly this.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning)
{
    public CatalogChannel Target => Preview.Channel;
}
