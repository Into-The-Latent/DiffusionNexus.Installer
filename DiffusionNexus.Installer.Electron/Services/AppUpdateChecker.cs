using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The app self-update check, on the channel the catalog follows (issue #19).
///
/// Stable is electron-updater's default: GitHub's <c>releases/latest</c>, which never names a
/// pre-release. Preview sets <c>allowPrerelease</c>, which makes it take the FIRST entry of the
/// releases feed instead, pre-release or not. That holds only while app versions stay plain
/// <c>X.Y.Z</c>: a version with a suffix (<c>3.1.0-beta.1</c>) switches electron-updater to
/// matching releases by that suffix, and an app built with one allows pre-releases by default.
/// Scripts/New-Release.ps1 refuses such a version for that reason; a Preview build is a plain
/// version published as a GitHub pre-release, and promoting it is un-ticking "pre-release" on
/// GitHub -- no rebuild.
///
/// "First entry" is the most recently CREATED release, not the highest version. Cut a Stable
/// hotfix 3.0.8 the day after pre-release 3.1.0 and the hotfix is first: testers would be handed
/// 3.0.8 and never offered 3.1.0 again until another pre-release came out. So on Preview this
/// reads the same feed first, and when the highest version is not the first entry it pins the
/// updater to that release through <c>updateConfigPath</c> (see <see cref="AppUpdateConfig.PinnedTo"/>).
/// Only then: the pinned path downloads in full, without electron-updater's differential
/// download, and the unpinned path is the one every other electron-updater app exercises. Any
/// failure on the way falls back to the plain check.
///
/// Downgrades stay off. A machine switched back to Stable while on a newer Preview build keeps
/// that build until Stable overtakes it, rather than reinstalling an older app over itself.
/// </summary>
public sealed class AppUpdateChecker(
    ICatalogUpdateCoordinator catalog,
    IAppUpdaterShell shell,
    UpdaterLog log,
    IAppReleaseFeed feed,
    string? pinnedConfigPath = null)
{
    private readonly string _pinnedConfigPath = pinnedConfigPath
        ?? Path.Combine(Path.GetTempPath(), "IntoTheLatent-EasyInstaller-app-update.yml");

    // Whether the updater in Electron's main process is reading our pinned config. It keeps what
    // it was last told, so every check that does not pin has to put the shipped config back.
    private bool _pinned;

    // One check at a time: the startup check is fire-and-forget and a channel switch checks by
    // itself, and every step here (allowPrerelease, the pin, the check) writes state the updater
    // keeps. A Preview check resuming after a Stable one would pin and download a pre-release
    // on Stable.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    // The channel last written to the log. Every check used to repeat it.
    private CatalogChannel? _loggedChannel;

    /// <summary>Never throws: a failed update check must not take the app down, which works fine on an older version.</summary>
    public async Task CheckAsync()
    {
        if (!shell.IsAvailable)
        {
            log.MarkUnavailable();
            return;
        }

        await _oneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            // Resolved here, not read from the property: the startup check runs alongside the
            // catalog's, and until one of them has read the settings the property says Stable.
            // And inside the turn, so a check that waited behind another runs on the channel in
            // effect when it starts.
            var channel = await catalog.ResolveChannelAsync().ConfigureAwait(false);

            // Sent on every check. The updater lives in Electron's main process and keeps what it
            // was last told, so a switch back to Stable has to be said, not assumed.
            shell.SetAllowPrerelease(channel == CatalogChannel.Preview);
            if (_loggedChannel != channel)
            {
                _loggedChannel = channel;
                log.Append($"Following {channel} app releases.");
            }

            // electron-updater skips an unpackaged app without firing a single event, so without
            // this the page would sit on its last line as if the check had hung. The check below
            // still goes out: if a packaged app's config was merely not found, its events will
            // replace this state.
            if (shell.UpdateConfigPath is null) log.MarkNotInstalledBuild();

            var pinTo = channel == CatalogChannel.Preview ? await ReleaseHiddenByANewerOneAsync().ConfigureAwait(false) : null;
            await ApplyPinAsync(pinTo).ConfigureAwait(false);

            await shell.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Append($"Update check failed: {ex.Message}");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The shipped config and the tag to pin it to, or null when the plain check already finds the highest version.</summary>
    private async Task<(AppUpdateConfig Config, string Tag)?> ReleaseHiddenByANewerOneAsync()
    {
        // No packaged app (dotnet run inside Electron): nothing says where the releases live.
        if (shell.UpdateConfigPath is not { } shippedPath) return null;

        try
        {
            var config = AppUpdateConfig.TryParse(await File.ReadAllTextAsync(shippedPath).ConfigureAwait(false));
            if (config is null) return null;

            var tags = AppReleaseTags.Parse(await feed.ReadAsync(config.FeedUrl).ConfigureAwait(false));
            return tags.Highest is { } highest && highest != tags.First ? (config, highest) : null;
        }
        catch (Exception ex)
        {
            // The plain check still runs, and is right whenever no hotfix was cut after a Preview build.
            log.Append($"The release list could not be read ({ex.Message}); checking the newest release only.");
            return null;
        }
    }

    private async Task ApplyPinAsync((AppUpdateConfig Config, string Tag)? pin)
    {
        if (pin is { } p)
        {
            try
            {
                await File.WriteAllTextAsync(_pinnedConfigPath, p.Config.PinnedTo(p.Tag)).ConfigureAwait(false);

                // Set first: a send that threw part-way may still have landed, and then the next
                // check must put the shipped config back.
                _pinned = true;
                await shell.SetUpdateConfigPathAsync(_pinnedConfigPath).ConfigureAwait(false);
                log.Append($"A newer release was published after {p.Tag}; checking {p.Tag} directly.");
                return;
            }
            catch (Exception ex)
            {
                // A temp folder that cannot be written, or an updater that would not take the
                // path, costs this check the right answer -- not the check.
                log.Append($"{p.Tag} could not be checked directly ({ex.Message}); checking the newest release only.");
            }
        }

        if (_pinned && shell.UpdateConfigPath is { } shippedPath)
        {
            // A pinned updater only ever sees one release; left pinned it would freeze the channel.
            // Best effort all the same: _pinned stays set on a failure, so the next check tries
            // again, and this one still runs -- at worst against the one release it is pinned to.
            try
            {
                await shell.SetUpdateConfigPathAsync(shippedPath).ConfigureAwait(false);
                _pinned = false;
            }
            catch (Exception ex)
            {
                log.Append($"The updater could not be pointed back at its own config ({ex.Message}).");
            }
        }
    }
}
