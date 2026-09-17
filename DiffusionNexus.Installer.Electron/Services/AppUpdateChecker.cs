using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The app self-update check, on the channel the catalog follows (issue #19).
///
/// Stable is electron-updater's default: GitHub's <c>releases/latest</c>, which never names a
/// pre-release. Preview sets <c>allowPrerelease</c>, which makes it take the newest entry of the
/// releases feed instead, pre-release or not. That is the whole mechanism, and it holds only
/// while app versions stay plain <c>X.Y.Z</c>: a version with a suffix (<c>3.1.0-beta.1</c>)
/// switches electron-updater to matching releases by that suffix, and an app built with one
/// allows pre-releases by default. Scripts/New-Release.ps1 refuses such a version for that
/// reason; a Preview build is a plain version published as a GitHub pre-release, and promoting
/// it is un-ticking "pre-release" on GitHub -- no rebuild.
///
/// Downgrades stay off. A machine switched back to Stable while on a newer Preview build keeps
/// that build until Stable overtakes it, rather than reinstalling an older app over itself.
/// </summary>
public sealed class AppUpdateChecker(ICatalogUpdateCoordinator catalog, IAppUpdaterShell shell, UpdaterLog log)
{
    /// <summary>Never throws: a failed update check must not take the app down, which works fine on an older version.</summary>
    public async Task CheckAsync()
    {
        if (!shell.IsAvailable) return;

        try
        {
            // Resolved here, not read from the property: the startup check runs alongside the
            // catalog's, and until one of them has read the settings the property says Stable.
            var channel = await catalog.ResolveChannelAsync().ConfigureAwait(false);

            // Sent on every check. The updater lives in Electron's main process and keeps what it
            // was last told, so a switch back to Stable has to be said, not assumed.
            shell.SetAllowPrerelease(channel == CatalogChannel.Preview);
            log.Append($"Following {channel} app releases.");

            await shell.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Append($"Update check failed: {ex.Message}");
        }
    }
}
